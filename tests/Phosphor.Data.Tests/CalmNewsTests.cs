using System.Xml.Linq;
using Phosphor.Core;
using Phosphor.Data;

namespace Phosphor.Data.Tests;

/// <summary>Фильтр «спокойно» (CalmFilter), слияние с calm=true и разбор новых лент.</summary>
public sealed class CalmNewsTests
{
    private static readonly DateTimeOffset Base = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public CalmNewsTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static string[] FixtureTitles(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        var doc = XDocument.Load(path);
        return doc.Descendants("item").Select(e => (string)e.Element("title")!).ToArray();
    }

    private static NewsItem Item(string source, string title, int hourOffset) =>
        new(source, title, Base.AddHours(hourOffset), "ru");

    [Theory]
    [InlineData("В Одессе произошел взрыв")]
    [InlineData("Лейтенант Тыченюк с ранением продолжал отражать атаку ВСУ")]
    [InlineData("В Мексике из-за бунта в тюрьме погибли 10 человек")]
    [InlineData("В Харькове прогремели взрывы")]
    [InlineData("Эвакуация раненых стала сложнейшей задачей из-за дронов, заявили в ГД")]
    [InlineData("В Махачкале семьям более 20 погибших участников СВО вручили ордена Мужества")]
    [InlineData("Africanews: эфиопские БПЛА ударили по эритрейским войскам в Тыграе")]
    [InlineData("Drone footage shows damage")]
    [InlineData("Strikes in Berlin continue")]
    [InlineData("Wartime rules changed")]
    [InlineData("В Киеве объявили воздушную тревогу")]
    public void Loud_headline_is_cut(string title) =>
        Assert.True(CalmFilter.IsLoud(title));

    [Theory]
    [InlineData("Ученый рассказал, как появляются новые острова в Арктике")]
    [InlineData("Умер лучший футболист Африки 1980 года Жан Манга-Онгене")]
    [InlineData("Маск считает, что SpaceX могла бы стоить в разы больше всей мировой экономики")]
    [InlineData("Свободная Европа открыла новый офис")]
    [InlineData("Ракурс нового снимка Земли")]
    [InlineData("Рана на ноге у кошки")]
    [InlineData("Warmest year on record")]
    [InlineData("Explorer probe returns data")]
    [InlineData("NASA explores Mars")]
    [InlineData("Тревога за детей в школе")]
    [InlineData("Воздушный шар над городом")]
    public void Calm_headline_is_kept(string title) =>
        Assert.False(CalmFilter.IsLoud(title));

    [Fact]
    public void Extra_stems_cut_by_word_start()
    {
        Assert.False(CalmFilter.IsLoud("Погода в Сочи на выходных"));
        Assert.True(CalmFilter.IsLoud("Погода в Сочи на выходных", new[] { "погод" }));
        Assert.False(CalmFilter.IsLoud("Новая погодная аномалия", new[] { "погодка" }));
    }

    [Fact]
    public void Tass_and_Ria_fixtures_filtered_selectively()
    {
        var titles = FixtureTitles("news-tass.xml").Concat(FixtureTitles("news-ria.xml")).ToArray();
        var dropped = titles.Where(t => CalmFilter.IsLoud(t)).ToArray();
        var kept = titles.Where(t => !CalmFilter.IsLoud(t)).ToArray();

        // Выборочно: отсеяно и осталось.
        Assert.Contains("В Одессе произошел взрыв", dropped);
        Assert.Contains("В Харькове прогремели взрывы", dropped);
        Assert.Contains("Лейтенант Тыченюк с ранением продолжал отражать атаку ВСУ", dropped);
        Assert.Contains("Ученый рассказал, как появляются новые острова в Арктике", kept);
        Assert.Contains("Умер лучший футболист Африки 1980 года Жан Манга-Онгене", kept);

        _out.WriteLine($"всего {titles.Length}, отсеяно {dropped.Length}, осталось {kept.Length}");
        foreach (var t in dropped) _out.WriteLine("  - " + t);

        // Фильтр не должен съедать большинство: в фикстурах около трети тревожных заголовков.
        Assert.True(dropped.Length > 10, $"отсеяно мало: {dropped.Length}");
        Assert.True(kept.Length > dropped.Length, $"отсеяно слишком много: {dropped.Length} из {titles.Length}");
    }

    [Fact]
    public void Merge_calm_false_cuts_nothing()
    {
        var a = new NewsSnapshot(new[] { Item("A", "Удар по рынку", 3), Item("A", "Тихий день", 2) }, Base, false);
        var merged = Assert.IsType<NewsSnapshot>(NewsCatalog.Merge(new DataSnapshot[] { a }, calm: false));
        Assert.Equal(2, merged.Items.Count);
    }

    [Fact]
    public void Merge_calm_drops_loud_and_spreads_sources()
    {
        // Час 0: B; час -1: A x4 (одна «Удар»); час -2: C. Без calm порядок A,A,A подряд; с calm одна лента не больше 2 подряд.
        var a = new NewsSnapshot(new[] { Item("A", "Первая тихая", -1), Item("A", "Вторая тихая", -1), Item("A", "Третья тихая", -1), Item("A", "Удар по рынку", -1) }, Base, false);
        var b = new NewsSnapshot(new[] { Item("B", "Новая тихая", 0) }, Base, false);
        var c = new NewsSnapshot(new[] { Item("C", "Последняя тихая", -2) }, Base, false);
        var plain = Assert.IsType<NewsSnapshot>(NewsCatalog.Merge(new DataSnapshot[] { a, b, c }, calm: false));
        Assert.Equal(new[] { "B", "A", "A", "A", "A", "C" }, plain.Items.Select(i => i.Source).ToArray());

        var calm = Assert.IsType<NewsSnapshot>(NewsCatalog.Merge(new DataSnapshot[] { a, b, c }, calm: true));
        Assert.DoesNotContain(calm.Items, i => i.Title == "Удар по рынку");
        Assert.Equal(5, calm.Items.Count);
        var sources = calm.Items.Select(i => i.Source).ToArray();
        for (var i = 2; i < sources.Length; i++)
            Assert.False(sources[i] == sources[i - 1] && sources[i] == sources[i - 2], $"три подряд из одной ленты: {string.Join(",", sources)}");
        Assert.Equal(new[] { "B", "A", "A", "C", "A" }, sources);
    }

    [Fact]
    public void Merge_calm_uses_extra_words()
    {
        var a = new NewsSnapshot(new[] { Item("A", "Погода на неделю", 2), Item("A", "Кошки и собаки", 1) }, Base, false);
        var merged = Assert.IsType<NewsSnapshot>(NewsCatalog.Merge(new DataSnapshot[] { a }, calm: true, extraWords: new[] { " Погод ", "" }));
        Assert.Single(merged.Items);
        Assert.Equal("Кошки и собаки", merged.Items[0].Title);
    }

    [Theory]
    [InlineData("nplus1", "N+1", "ru", "news-nplus1.xml", "nplus1.ru/rss")]
    [InlineData("habr", "HABR", "ru", "news-habr.xml", "habr.com/ru/rss")]
    [InlineData("naked", "NAKED", "ru", "news-naked.xml", "naked-science.ru/rss")]
    [InlineData("nasa", "NASA", "en", "news-nasa.xml", "nasa.gov/feed")]
    [InlineData("esa", "ESA", "en", "news-esa.xml", "esa.int/rssfeed")]
    [InlineData("hn", "HN", "en", "news-hn.xml", "hnrss.org/frontpage")]
    public async Task New_feed_parses_fixture(string id, string label, string lang, string fixture, string urlPart)
    {
        var http = new FakeHttp().OnFile(urlPart, fixture, "application/xml").Client();
        var src = NewsCatalog.Create(id);
        Assert.NotNull(src);
        var snap = Assert.IsType<NewsSnapshot>(await src!.FetchAsync(http, CancellationToken.None));
        Assert.True(snap.Items.Count >= 5, $"мало элементов: {snap.Items.Count}");
        Assert.All(snap.Items, i => Assert.Equal(label, i.Source));
        Assert.All(snap.Items, i => Assert.Equal(lang, i.Lang));
        Assert.All(snap.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.Title)));
        Assert.True(snap.Items.Count(i => i.Published is not null) * 2 > snap.Items.Count, "у большинства нет даты");
    }
}
