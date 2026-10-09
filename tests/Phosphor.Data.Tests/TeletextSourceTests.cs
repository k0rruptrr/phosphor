using System.Text;
using System.Text.RegularExpressions;
using Phosphor.Core;

namespace Phosphor.Data.Tests;

public class TeletextSourceTests
{
    private static string FxPath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    private static string Fx(string name) => File.ReadAllText(FxPath(name));

    /// <summary>URL GIF → биты из фикстуры tt-gif-&lt;id&gt;.gif; неизвестные GIF — 0x3F.</summary>
    private static int? GifFromFixtures(string url)
    {
        var id = Regex.Match(url, @"/chars/(\d+)\.gif").Groups[1].Value;
        var path = FxPath("tt-gif-" + id + ".gif");
        return File.Exists(path) ? TeletextSource.DecodeGifBits(File.ReadAllBytes(path)) : 0x3F;
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

    [Fact]
    public void DecodeGifBits_gives_bits_in_range_and_not_trivial()
    {
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "tt-gif-*.gif");
        Assert.NotEmpty(files);
        var values = files.Select(f => TeletextSource.DecodeGifBits(File.ReadAllBytes(f))).ToList();
        Assert.All(values, v =>
        {
            Assert.NotNull(v);
            Assert.InRange(v!.Value, 0, 63);
        });
        Assert.Contains(values, v => v != 0 && v != 63);
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
