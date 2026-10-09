using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using Phosphor.Core;

namespace Phosphor.Data.Tests;

public class TeletextSourceTests
{
    private static string FxPath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    private static string Fx(string name) => File.ReadAllText(FxPath(name));

    /// <summary>URL GIF + фон ячейки → биты из фикстуры tt-gif-&lt;id&gt;.gif; неизвестные GIF — 0x3F.</summary>
    private static int? GifFromFixtures(string url, byte bg)
    {
        var id = Regex.Match(url, @"/chars/(\d+)\.gif").Groups[1].Value;
        var path = FxPath("tt-gif-" + id + ".gif");
        return File.Exists(path) ? TeletextSource.DecodeGifBits(File.ReadAllBytes(path), bg) : 0x3F;
    }

    private static string Row(TeletextPage p, int r) => new string(p.Chars, r * p.Cols, p.Cols);

    private static void AssertGrid(TeletextPage p)
    {
        Assert.Equal(25, p.Rows);
        Assert.Equal(40, p.Cols);
        Assert.Equal(1000, p.Chars.Length);
        Assert.Equal(1000, p.Fg.Length);
        Assert.Equal(1000, p.Bg.Length);
        Assert.Equal(1000, p.Mosaic.Length);
        Assert.All(p.Chars, c => Assert.True(GlyphSet.Contains(c), $"символ U+{(int)c:X4} не из GlyphSet"));
        Assert.All(p.Fg, f => Assert.InRange(f, (byte)0, (byte)7));
        Assert.All(p.Bg, b => Assert.InRange(b, (byte)0, (byte)7));
    }

    private static string Dump(TeletextPage p)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < p.Rows; r++)
        {
            for (int c = 0; c < p.Cols; c++)
            {
                int i = r * p.Cols + c;
                sb.Append(p.Mosaic[i] != 0 ? '#' : p.Chars[i]);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    [Theory]
    [InlineData("tt-nos-100.json", 100)]
    [InlineData("tt-nos-101.json", 101)]
    [InlineData("tt-nos-702.json", 702)]
    public void ParseNos_gives_normalized_grid(string file, int page)
    {
        var p = TeletextSource.ParseNos(Fx(file), page);
        AssertGrid(p);
        Assert.Equal("NOS", p.Channel);
        Assert.Equal("nl", p.Lang);
        Assert.Equal(page, p.Page);
        Assert.Contains(p.Mosaic, m => m != 0);
        Assert.True(p.Fg.Any(f => f != 7) || p.Bg.Any(b => b != 0), "ожидались цвета кроме белого на чёрном");
    }

    [Fact]
    public void ParseNos_101_contains_NOS_title()
    {
        var p = TeletextSource.ParseNos(Fx("tt-nos-101.json"), 101);
        Assert.Contains("NOS", Row(p, 0));
    }

    /// <summary>
    /// Сущности мозаики NOS → биты секстеля, выписаны вручную по кодам телетекста: биты 0..4 — код &amp; 0x1F
    /// (TL, TR, ML, MR, BL), бит 5 (BR) — код 0x40. &#xF03F; → 0b011111 = 31; &#xF060; → только BR = 32;
    /// &#xF07F; → все шесть = 63.
    /// </summary>
    [Fact]
    public void ParseNos_mosaic_entities_map_to_sextant_bits()
    {
        const string json = "{\"content\":\"&#xF03F;&#xF060;&#xF07F;\"}";
        var p = TeletextSource.ParseNos(json, 100);
        Assert.Equal(31, p.Mosaic[0]);
        Assert.Equal(32, p.Mosaic[1]);
        Assert.Equal(63, p.Mosaic[2]);
    }

    [Fact]
    public void ParseSvt_100_gives_normalized_grid_with_yellow_title()
    {
        var p = TeletextSource.ParseSvt(Fx("tt-svt-100.json"), 100, GifFromFixtures);
        AssertGrid(p);
        Assert.Equal("SVT", p.Channel);
        Assert.Equal("sv", p.Lang);
        Assert.Equal(100, p.Page);

        var r0 = Row(p, 0);
        int at = r0.IndexOf("SVT Text", StringComparison.Ordinal);
        Assert.True(at >= 0, r0);
        for (int i = 0; i < "SVT Text".Length; i++)
            Assert.Equal(3, p.Fg[at + i]);

        Assert.Contains(p.Mosaic, m => m != 0);
        Assert.Contains("pågå", Row(p, 8));
        Assert.Contains('å', p.Chars);
    }

    [Fact]
    public void ParseSvt_DH_rows_are_stretched_over_two_rows()
    {
        // В svt-100 строки 6, 15 и 22 помечены DH (class="line DH"); строка k+1 в HTML — пустой placeholder.
        var p = TeletextSource.ParseSvt(Fx("tt-svt-100.json"), 100, GifFromFixtures);
        Assert.Contains("Trump: Inga nya attacker", Row(p, 6));
        foreach (var k in new[] { 6, 15, 22 })
        {
            for (int c = 0; c < p.Cols; c++)
            {
                int top = k * p.Cols + c, bottom = (k + 1) * p.Cols + c;
                Assert.Equal(' ', p.Chars[bottom]);
                Assert.Equal(p.Fg[top], p.Fg[bottom]);
                Assert.Equal(p.Bg[top], p.Bg[bottom]);
            }
        }
    }

    private static int[] SextantRows(byte v) => new[] { v & 3, (v >> 2) & 3, (v >> 4) & 3 };

    [Fact]
    public void StretchSextant_is_correct_for_all_64_values()
    {
        for (int bits = 0; bits < 64; bits++)
        {
            var (top, bottom) = TeletextSource.StretchSextant((byte)bits);
            int r0 = bits & 3, r1 = (bits >> 2) & 3, r2 = (bits >> 4) & 3;
            Assert.Equal(new[] { r0, r0, r1 }, SextantRows(top));
            Assert.Equal(new[] { r1, r2, r2 }, SextantRows(bottom));
        }
        Assert.Equal(((byte)63, (byte)63), TeletextSource.StretchSextant(63));
        Assert.Equal(((byte)0, (byte)0), TeletextSource.StretchSextant(0));
        // Только r0 полностью: верх — ряд 0 дважды (бits 0..1 → 15), низ — пусто.
        Assert.Equal(((byte)15, (byte)0), TeletextSource.StretchSextant(3));
    }

    /// <summary>
    /// Ожидаемые биты выписаны вручную по пикселям фикстур (13×16; колонки [0..5] и [6..12]; ряды [0..4], [5..9], [10..15]).
    /// Фон — цвет класса bgXX ячейки из tt-svt-100.json; «включено» — пиксели другого цвета (переднего плана).
    /// 2015754887, фон W: синий — правые 7 колонок во всех рядах → биты 1, 3, 5 = 2 + 8 + 32 = 42.
    /// 2335531887, фон B: белый — правые 7 колонок, ряды 11..15 → бит 5 = 32.
    /// 2413702233, фон B: белый — ряды 5..15 целиком → биты 2, 3, 4, 5 = 60.
    /// 2754943555, фон B: белый — левые 6 колонок, ряды 5..15 → биты 2, 4 = 20.
    /// 693852549, фон B: белый — правые 7 колонок, ряды 5..15 → биты 3, 5 = 40.
    /// </summary>
    [Theory]
    [InlineData("2015754887", 7, 42)]
    [InlineData("2335531887", 4, 32)]
    [InlineData("2413702233", 4, 60)]
    [InlineData("2754943555", 4, 20)]
    [InlineData("693852549", 4, 40)]
    public void DecodeGifBits_fixtures_match_hand_decoded_bits(string id, byte bg, int expected)
    {
        var bytes = File.ReadAllBytes(FxPath("tt-gif-" + id + ".gif"));
        Assert.Equal(expected, TeletextSource.DecodeGifBits(bytes, bg));
    }

    /// <summary>
    /// Порядок бит декодера = порядок GlyphSet.Sextant: бит r*2+c — ряд r (0..2) и колонка c (0..1). Синтетический GIF 13×16,
    /// чёрный фон, белая ровно одна область 2×3 с битом b; декодер должен вернуть 1 &lt;&lt; b.
    /// </summary>
    [Fact]
    public void DecodeGifBits_bit_order_matches_GlyphSet_sextant()
    {
        for (int b = 0; b < 6; b++)
        {
            int r = b / 2, c = b % 2;
            using var bmp = new Bitmap(13, 16, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 13; x++)
                {
                    bool on = x >= c * 13 / 2 && x < (c + 1) * 13 / 2 && y >= r * 16 / 3 && y < (r + 1) * 16 / 3;
                    bmp.SetPixel(x, y, on ? Color.White : Color.Black);
                }
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Gif);
            Assert.Equal(1 << b, TeletextSource.DecodeGifBits(ms.ToArray(), 0));
        }
    }

    [Fact]
    public void DecodeGifBits_fixtures_stay_in_range()
    {
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "tt-gif-*.gif");
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            var v = TeletextSource.DecodeGifBits(File.ReadAllBytes(f), 0);
            Assert.NotNull(v);
            Assert.InRange(v!.Value, 0, 63);
        }
    }

    [Fact]
    public async Task FetchAsync_nos_skips_failed_pages()
    {
        var fake = new FakeHttp()
            .OnFile("/json/100", "tt-nos-100.json")
            .OnFile("/json/101", "tt-nos-101.json")
            .OnFile("/json/702", "tt-nos-702.json");
        var src = TeletextSource.Create("nos")!;
        src.Delay = TimeSpan.Zero;

        var snap = Assert.IsType<TeletextSnapshot>(await src.FetchAsync(fake.Client(), CancellationToken.None));
        Assert.Equal(new[] { 100, 101, 702 }, snap.Pages.Select(p => p.Page).ToArray());
        Assert.Equal(10, fake.Requests.Count);
        Assert.Equal("teletext", src.Key);
        Assert.Equal("teletext.nos", src.Name);
    }

    [Fact]
    public async Task FetchAsync_throws_when_no_page_loaded()
    {
        var src = TeletextSource.Create("svt")!;
        src.Delay = TimeSpan.Zero;
        await Assert.ThrowsAsync<InvalidOperationException>(() => src.FetchAsync(new FakeHttp().Client(), CancellationToken.None));
    }

    [Fact]
    public void Create_accepts_only_known_channels()
    {
        Assert.NotNull(TeletextSource.Create("nos"));
        Assert.NotNull(TeletextSource.Create("svt"));
        Assert.Null(TeletextSource.Create("bbc"));
    }

    [Fact]
    public void Merge_combines_pages_of_all_channels()
    {
        var nos = TeletextSource.ParseNos(Fx("tt-nos-100.json"), 100);
        var svt = TeletextSource.ParseSvt(Fx("tt-svt-100.json"), 100, GifFromFixtures);
        var parts = new DataSnapshot[]
        {
            new TeletextSnapshot(new[] { nos }, DateTimeOffset.Now, false),
            new TeletextSnapshot(new[] { svt }, DateTimeOffset.Now, false),
        };
        var merged = Assert.IsType<TeletextSnapshot>(TeletextCatalog.Merge(parts));
        Assert.Equal(2, merged.Pages.Count);
        Assert.Null(TeletextCatalog.Merge(Array.Empty<DataSnapshot>()));
    }

    /// <summary>Пишет текстовые дампы для ручной проверки (мозаика → '#').</summary>
    [Fact]
    public void Dump_pages_for_inspection()
    {
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "phosphor-tt-nos101.txt"),
            Dump(TeletextSource.ParseNos(Fx("tt-nos-101.json"), 101)));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "phosphor-tt-svt100.txt"),
            Dump(TeletextSource.ParseSvt(Fx("tt-svt-100.json"), 100, GifFromFixtures)));
    }
}
