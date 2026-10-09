using System.Text;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class TeletextSceneTests
{
    private const int Cols = 96, Rows = 30;
    private const int PageTop = 2;   // строка 0 страницы на сетке 96x30: (30-25)/2

    private readonly ITestOutputHelper _out;

    public TeletextSceneTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new TeletextScene(), "teletext");

    [Fact]
    public void SyntheticPageShowsTextMosaicAndPalette()
    {
        var s = SceneContract.Make(() => new TeletextScene(), Cols, Rows, 1);
        ((IDataScene)s).OnData("teletext", Teletext(MakePage(101, "NOS", "NL", "P101 NOS Teletekst")));
        SceneContract.Run(s, 1);
        var g = SceneContract.Snap(s, Cols, Rows);

        Assert.Contains("NOS", RowText(g, PageTop));
        Assert.Contains(g.Cells, c => GlyphSet.IsSextant(c.Ch));
        Assert.Contains(g.Cells, c => c.Bg == TeletextColors.Palette[4]);
    }

    [Fact]
    public void SecondPageAppearsAfterTwentyFiveSeconds()
    {
        var s = SceneContract.Make(() => new TeletextScene(), Cols, Rows, 3);
        ((IDataScene)s).OnData("teletext", Teletext(
            MakePage(101, "NOS", "NL", "P101 NOS Teletekst"),
            MakePage(102, "SVT", "SV", "P102 SVT Text")));
        SceneContract.Run(s, 1);
        string before = RowText(SceneContract.Snap(s, Cols, Rows), PageTop);

        SceneContract.Run(s, 25 * 60); // 25 с при dt = 1/60
        string after = RowText(SceneContract.Snap(s, Cols, Rows), PageTop);

        Assert.NotEqual(before, after);
        Assert.True(before.Contains("NOS") != after.Contains("NOS"),
            $"страница не сменилась: было «{before.Trim()}», стало «{after.Trim()}»");
    }

    [Fact]
    public void ClockAppearsInPageHeaderRow()
    {
        var s = SceneContract.Make(() => new TeletextScene(), Cols, Rows, 4);
        var d = (IDataScene)s;
        d.OnData("clock", new ClockSnapshot(new DateTime(2026, 10, 9, 4, 20, 15)));
        d.OnData("teletext", Teletext(MakePage(101, "NOS", "NL", "P101 NOS Teletekst")));
        SceneContract.Run(s, 1);
        var g = SceneContract.Snap(s, Cols, Rows);

        Assert.Contains("04:20:15", RowText(g, PageTop));
    }

    [Fact]
    public void ClockDoesNotOverwriteFullHeaderRowButShowsInFrame()
    {
        const string full = "P102 SVT Text fredag 09 okt 2026ABCDEFGH"; // ровно 40 символов
        Assert.Equal(40, full.Length);
        var s = SceneContract.Make(() => new TeletextScene(), Cols, Rows, 9);
        var d = (IDataScene)s;
        d.OnData("teletext", Teletext(MakePage(102, "SVT", "SV", full)));
        SceneContract.Run(s, 1);
        string before = RowText(SceneContract.Snap(s, Cols, Rows), PageTop);

        d.OnData("clock", new ClockSnapshot(new DateTime(2026, 10, 9, 4, 20, 15)));
        SceneContract.Run(s, 1);
        var g = SceneContract.Snap(s, Cols, Rows);
        string after = RowText(g, PageTop);

        Assert.Equal(before, after);
        Assert.Contains("2026ABCDEFGH", after);
        Assert.DoesNotContain("04:20:15", after);
        // Часы в рамке: справа под счётчиком (oy + 7 = строка 9)
        Assert.Contains("04:20:15", RowText(g, PageTop + 7));
    }

    [Fact]
    public void SmallGridDoesNotThrow()
    {
        var stub = SceneContract.Make(() => new TeletextScene(), 30, 10, 5);
        SceneContract.Run(stub, 300);
        SceneContract.Snap(stub, 30, 10);

        var s = SceneContract.Make(() => new TeletextScene(), 30, 10, 6);
        var d = (IDataScene)s;
        d.OnData("clock", new ClockSnapshot(new DateTime(2026, 10, 9, 4, 20, 15)));
        d.OnData("teletext", Teletext(
            MakePage(101, "NOS", "NL", "P101 NOS Teletekst"),
            MakePage(102, "SVT", "SV", "P102 SVT Text")));
        SceneContract.Run(s, 1500); // проходит и переход между страницами
        SceneContract.Snap(s, 30, 10);
    }

    [Fact]
    public void DumpForVisualCheck()
    {
        var stub = SceneContract.Make(() => new TeletextScene(), Cols, Rows, 7);
        SceneContract.Run(stub, 120);
        _out.WriteLine(SceneContract.Dump(SceneContract.Snap(stub, Cols, Rows), "teletext-stub"));

        var s = SceneContract.Make(() => new TeletextScene(), Cols, Rows, 8);
        var d = (IDataScene)s;
        d.OnData("clock", new ClockSnapshot(new DateTime(2026, 10, 9, 4, 20, 15)));
        d.OnData("teletext", Teletext(MakePage(101, "NOS", "NL", "P101 NOS Teletekst")));
        SceneContract.Run(s, 120);
        _out.WriteLine(SceneContract.Dump(SceneContract.Snap(s, Cols, Rows), "teletext-page"));
    }

    private static TeletextSnapshot Teletext(params TeletextPage[] pages) =>
        new(pages, DateTimeOffset.Now, false);

    /// <summary>Страница 40x25: синяя шапка с заголовком, мозаика в строках 2..4, текст ниже.</summary>
    private static TeletextPage MakePage(int page, string channel, string lang, string title)
    {
        const int R = 25, C = 40;
        var chars = new char[R * C];
        var fg = new byte[R * C];
        var bg = new byte[R * C];
        var mosaic = new byte[R * C];
        for (int i = 0; i < chars.Length; i++) { chars[i] = ' '; fg[i] = 7; bg[i] = 0; }

        // Строка 0: синяя шапка с заголовком
        for (int x = 0; x < C; x++)
        {
            int i = x;
            bg[i] = 4;
            fg[i] = 7;
            if (x < title.Length) chars[i] = title[x];
        }

        // Строки 2..4: мозаика, цвета разные
        for (int r = 2; r <= 4; r++)
            for (int x = 0; x < C; x++)
            {
                int i = r * C + x;
                mosaic[i] = (byte)(1 + (x * 7 + r * 11) % 63);
                fg[i] = (byte)(1 + x % 6);
                bg[i] = 4;
            }

        // Строки 6..24: текст
        const string body = "Het nieuws van vandaag, weer en sport ";
        for (int r = 6; r < R; r++)
            for (int x = 0; x < C; x++)
            {
                int i = r * C + x;
                chars[i] = body[(x + r) % body.Length];
                fg[i] = 6;
            }

        return new TeletextPage(channel, lang, page, 0, R, C, chars, fg, bg, mosaic);
    }

    private static string RowText(CharGrid g, int y)
    {
        var sb = new StringBuilder(g.Cols);
        for (int x = 0; x < g.Cols; x++) sb.Append(g[x, y].Ch);
        return sb.ToString();
    }
}
