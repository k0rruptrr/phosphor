using System.Text.RegularExpressions;
using Phosphor.Core;
using Phosphor.Data;
using Xunit.Abstractions;

namespace Phosphor.Data.Tests;

public sealed class NewsSourceTests
{
    private readonly ITestOutputHelper _out;

    public NewsSourceTests(ITestOutputHelper output) => _out = output;

    private static async Task<NewsSnapshot> FetchFixture(string id, string urlPart, string fixture)
    {
        var http = new FakeHttp().OnFile(urlPart, fixture, "application/xml").Client();
        var src = NewsCatalog.Create(id);
        Assert.NotNull(src);
        var snap = await src!.FetchAsync(http, CancellationToken.None);
        return Assert.IsType<NewsSnapshot>(snap);
    }

    private static void AssertCommon(NewsSnapshot snap, string label, string lang)
    {
        Assert.True(snap.Items.Count >= 5, $"мало элементов: {snap.Items.Count}");
        Assert.All(snap.Items, i => Assert.Equal(label, i.Source));
        Assert.All(snap.Items, i => Assert.Equal(lang, i.Lang));
        Assert.All(snap.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.Title)));
        Assert.All(snap.Items, i => Assert.All(i.Title, c => Assert.True(GlyphSet.Contains(c), $"символ не рисуется: U+{(int)c:X4} в '{i.Title}'")));
        Assert.True(snap.Items.Count(i => i.Published is not null) * 2 > snap.Items.Count, "у большинства нет даты");
    }

    [Fact]
    public async Task Tass_Ru_Rss()
    {
        var snap = await FetchFixture("tass", "tass.ru/rss", "news-tass.xml");
        AssertCommon(snap, "TASS", "ru");
    }

    [Fact]
    public async Task Tass_En_Rss()
    {
        var snap = await FetchFixture("tass-en", "tass.com/rss", "news-tass-en.xml");
        AssertCommon(snap, "TASS", "en");
    }

    [Fact]
    public async Task Ria_Rss()
    {
        var snap = await FetchFixture("ria", "ria.ru/export/rss2", "news-ria.xml");
        AssertCommon(snap, "RIA", "ru");
    }

    [Fact]
    public async Task Interfax_Cyrillic_Readable()
    {
        var snap = await FetchFixture("interfax", "interfax.ru/rss.asp", "news-interfax.xml");
        AssertCommon(snap, "INTERFAX", "ru");
        Assert.Contains(snap.Items, i => Regex.IsMatch(i.Title, "[А-Яа-я]"));
        Assert.All(snap.Items, i => Assert.DoesNotMatch(@"\?{3,}", i.Title));
    }

    [Fact]
    public async Task Bbc_Rss()
    {
        var snap = await FetchFixture("bbc", "feeds.bbci.co.uk/news/world", "news-bbc.xml");
        AssertCommon(snap, "BBC", "en");
    }

    [Fact]
    public async Task Reuters_Google_News_Suffix_Stripped()
    {
        var snap = await FetchFixture("reuters", "news.google.com/rss/search", "news-reuters.xml");
        AssertCommon(snap, "REUTERS", "en");
        Assert.All(snap.Items, i => Assert.False(i.Title.EndsWith(" - Reuters", StringComparison.Ordinal), i.Title));
    }

    [Fact]
    public async Task Reddit_Atom_Parsed()
    {
        var snap = await FetchFixture("reddit:worldnews", "reddit.com/r/worldnews/top/.rss", "news-reddit-worldnews.xml");
        AssertCommon(snap, "R/WORLDNEWS", "en");
        Assert.Null(NewsCatalog.Create("reddit:bad/sub"));
    }

    [Fact]
    public void Create_Rejects_Bad_Subreddit_And_Unknown_Id()
    {
        Assert.Null(NewsCatalog.Create("reddit:bad/sub"));
        Assert.Null(NewsCatalog.Create("reddit:"));
        Assert.Null(NewsCatalog.Create("nope"));
    }

    [Fact]
    public void Merge_Two_Snapshots_Sorted_And_Capped()
    {
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        var a = Enumerable.Range(0, 40).Select(i => new NewsItem("TASS", "a" + i, now.AddMinutes(-7 * i), "ru")).ToList();
        var b = Enumerable.Range(0, 40).Select(i => new NewsItem("BBC", "b" + i, i % 10 == 0 ? null : now.AddMinutes(-9 * i - 3), "en")).ToList();
        var sa = new NewsSnapshot(a, now, false);
        var sb = new NewsSnapshot(b, now.AddMinutes(-1), true);

        var merged = Assert.IsType<NewsSnapshot>(NewsCatalog.Merge(new DataSnapshot[] { sa, sb }));

        Assert.Equal(60, merged.Items.Count);
        Assert.Equal(now, merged.FetchedAt);
        Assert.False(merged.FromCache);

        // Порядок по часу убывает; без даты — в самом конце.
        long? prevHour = long.MaxValue;
        bool seenNull = false;
        foreach (var it in merged.Items)
        {
            if (it.Published is null) { seenNull = true; continue; }
            Assert.False(seenNull, "датированный элемент после элемента без даты");
            long hour = it.Published.Value.UtcTicks / TimeSpan.TicksPerHour;
            Assert.True(hour <= prevHour, "нарушен порядок по времени");
            prevHour = hour;
        }
    }
}

/// <summary>Живая проверка: по одному реальному запросу. Не входит в обычный прогон (--filter "Category!=Live").</summary>
public sealed class NewsSourceLiveTests
{
    private readonly ITestOutputHelper _out;

    public NewsSourceLiveTests(ITestOutputHelper output) => _out = output;

    [Trait("Category", "Live")]
    [Theory]
    [InlineData("tass")]
    [InlineData("bbc")]
    [InlineData("reddit:worldnews")]
    public async Task Live_Three_Headlines(string id)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PHOSPHOR-screensaver/0.1 (ascii screensaver; personal use)");
        var src = NewsCatalog.Create(id);
        Assert.NotNull(src);

        var snap = Assert.IsType<NewsSnapshot>(await src!.FetchAsync(http, CancellationToken.None));
        _out.WriteLine($"== {id} ({snap.Items.Count} шт.)");
        foreach (var it in snap.Items.Take(3))
            _out.WriteLine($"[{it.Source}] {it.Published:yyyy-MM-dd HH:mm zzz} {it.Title}");
    }
}
