using System.Text;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class TeletypeSceneTests
{
    private const string LongTitle =
        "Regional council debated the water schedule past midnight, arguing over repair budgets " +
        "and priority districts for the next season ZEPHYRQUAY";

    private readonly ITestOutputHelper _out;

    public TeletypeSceneTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new TeletypeScene(), "teletype");

    [Fact]
    public void NoDataShowsServiceLinesAndHeader()
    {
        var s = SceneContract.Make(() => new TeletypeScene(), 120, 40, 41);
        SceneContract.Run(s, 20 * 60);
        var g = SceneContract.Snap(s, 120, 40);
        string screen = Screen(g);
        Assert.Contains("ТЕЛЕТАЙП", Row(g, 1));
        string[] keys = { "СВЯЗЬ С АГЕНТСТВОМ", "ПОВТОР ВЫЗОВА", "ОЖИДАНИЕ ПЕРЕДАЧИ", "НЕ ОТВЕЧАЕТ",
            "НЕ СОВПАЛА", "ПРИНЯТО 0 БАЙТ", "ОТКАЗ АГЕНТСТВА", "СИГНАЛ ПОТЕРЯН" };
        Assert.Contains(keys, k => screen.Contains(k, StringComparison.Ordinal));
    }

    [Fact]
    public void SyntheticNewsShowsSourcesTitlesAndWrapsLongTitle()
    {
        Assert.True(LongTitle.Length > 120, "заголовок должен быть длиннее ширины строки, чтобы проверить перенос");
        var s = NewScene(120, 40, 42);
        Feed(s, Items());
        SceneContract.Run(s, 20 * 60);
        var g = SceneContract.Snap(s, 120, 40);
        string screen = Screen(g);

        Assert.Contains("ТАСС", screen);
        Assert.Contains("BBC", screen);
        Assert.Contains("R/WORLDNEWS", screen);
        Assert.Contains("Правительство", screen);
        Assert.Contains("Talks on the", screen);
        Assert.Contains("Regional council", screen);
        Assert.Contains("ZEPHYRQUAY", screen);
    }

    [Fact]
    public void CyrillicAndLatinOnScreenAndOnlyKnownGlyphs()
    {
        var s = NewScene(120, 40, 43);
        Feed(s, Items());
        SceneContract.Run(s, 20 * 60);
        var g = SceneContract.Snap(s, 120, 40);
        string screen = Screen(g);

        Assert.Contains(g.Cells, c => c.Ch >= 'А' && c.Ch <= 'я');
        Assert.Contains(g.Cells, c => c.Ch >= 'A' && c.Ch <= 'Z');
        var bad = g.Cells.Select(c => c.Ch).Where(c => !GlyphSet.Contains(c)).Distinct().ToArray();
        Assert.True(bad.Length == 0, $"Символы вне GlyphSet: {string.Join(" ", bad.Select(c => $"U+{(int)c:X4}"))}");
        Assert.DoesNotContain("…", screen);
    }

    [Fact]
    public void ClockShowsTimeInHeader()
    {
        var s = NewScene(120, 40, 44);
        ((IDataScene)s).OnData("clock", new ClockSnapshot(new DateTime(2026, 10, 9, 4, 20, 15)));
        SceneContract.Run(s, 1);
        var g = SceneContract.Snap(s, 120, 40);
        Assert.Contains("04:20:15", Row(g, 1));
        Assert.Contains("ЛИНИЯ 3", Row(g, 1));
    }

    [Fact]
    public void CachedNewsShowsCacheLabelInHeader()
    {
        var s = NewScene(120, 40, 45);
        var fetched = new DateTimeOffset(2026, 10, 9, 4, 0, 0, TimeSpan.Zero);
        ((IDataScene)s).OnData("news", new NewsSnapshot(Items(), fetched, true));
        SceneContract.Run(s, 1);
        var g = SceneContract.Snap(s, 120, 40);
        Assert.Contains("КЕШ", Row(g, 1));
    }

    [Fact]
    public void EachPrintedTitleAppearsOnceOnScreen()
    {
        var items = Items();
        var s = NewScene(120, 40, 48);
        Feed(s, items);
        // Три записи печатаются примерно за 20 с; проверяем после того, как все три напечатаны.
        SceneContract.Run(s, 20 * 60);
        var g = SceneContract.Snap(s, 120, 40);
        string screen = Screen(g);
        foreach (var it in items)
        {
            string key = it.Title.Length > 30 ? it.Title.Substring(0, 30) : it.Title;
            int count = 0, at = 0;
            while ((at = screen.IndexOf(key, at, StringComparison.Ordinal)) >= 0) { count++; at += key.Length; }
            Assert.True(count == 1, $"заголовок «{key}» встречается {count} раз(а), ожидалось 1");
        }
    }

    [Fact]
    public void SafeZoneKeepsEdgesEmpty()
    {
        var s = NewScene(120, 40, 47);
        Feed(s, Items());
        SceneContract.Run(s, 20 * 60);
        var g = SceneContract.Snap(s, 120, 40);
        for (int x = 0; x < g.Cols; x++)
        {
            Assert.Equal(' ', g[x, 0].Ch);
            Assert.Equal(' ', g[x, g.Rows - 1].Ch);
        }
        for (int y = 0; y < g.Rows; y++)
        {
            Assert.Equal(' ', g[0, y].Ch);
            Assert.Equal(' ', g[1, y].Ch);
            Assert.Equal(' ', g[g.Cols - 1, y].Ch);
            Assert.Equal(' ', g[g.Cols - 2, y].Ch);
        }
        Assert.Equal('Т', g[2, 1].Ch);
        Assert.Equal('─', g[2, 2].Ch);
    }

    [Fact]
    public void DumpForVisualCheck()
    {
        var s = NewScene(120, 40, 46);
        ((IDataScene)s).OnData("clock", new ClockSnapshot(new DateTime(2026, 10, 9, 4, 20, 15)));
        Feed(s, Items());
        SceneContract.Run(s, 20 * 60);
        var g = SceneContract.Snap(s, 120, 40);
        var path = SceneContract.Dump(g, "teletype");
        _out.WriteLine(path);
        Assert.True(File.Exists(path));
    }

    private static IScene NewScene(int cols, int rows, int seed)
    {
        var s = SceneContract.Make(() => new TeletypeScene(), cols, rows, seed);
        return s;
    }

    private static void Feed(IScene s, IReadOnlyList<NewsItem> items) =>
        ((IDataScene)s).OnData("news", new NewsSnapshot(items, DateTimeOffset.Now, false));

    private static IReadOnlyList<NewsItem> Items() => new[]
    {
        new NewsItem("ТАСС", "Правительство утвердило план развития регионов", new DateTimeOffset(2026, 10, 9, 4, 31, 0, TimeSpan.Zero), "ru"),
        new NewsItem("BBC", "Talks on the new trade deal resume in Geneva", new DateTimeOffset(2026, 10, 9, 4, 12, 0, TimeSpan.Zero), "en"),
        new NewsItem("R/WORLDNEWS", LongTitle, null, "en"),
    };

    private static string Row(CharGrid g, int y)
    {
        var chars = new char[g.Cols];
        for (int x = 0; x < g.Cols; x++) chars[x] = g[x, y].Ch;
        return new string(chars);
    }

    private static string Screen(CharGrid g)
    {
        var sb = new StringBuilder();
        for (int y = 0; y < g.Rows; y++) sb.Append(Row(g, y)).Append('\n');
        return sb.ToString();
    }
}
