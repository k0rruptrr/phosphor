using System.Text;
using System.Text.Json;
using Phosphor.Core;

namespace Phosphor.Data.Tests;

public sealed class SatTvSourceTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static string Fx(string id) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"kingofsat-{id}.html"));

    /// <summary>Разбор сохранённой страницы (фикстуры KingOfSat). Сеть не нужна.</summary>
    private static SatTvSatellite Parse(string id) => SatTvSource.Parse(Fx(id), id, At);

    private static SatTvSatellite Sat(string id, int channels, bool fromCache = false, DateTimeOffset? at = null) =>
        new(id, id.ToUpperInvariant(), "0.0°E",
            Enumerable.Range(0, channels).Select(i => new SatTvChannel("ch" + i, 11000 + i, 'H', 27500, "3/4", "DVB-S2", "", true)).ToList(),
            at ?? At, fromCache);

    // ---- разбор фикстур ----

    [Theory]
    [InlineData("astra1p")]
    [InlineData("yamal601")]
    public void Fixture_ParsesTvChannels(string id)
    {
        var sat = Parse(id);
        Assert.True(sat.Channels.Count > 20, $"{id}: каналов {sat.Channels.Count}");
        Assert.True(sat.Channels.Count <= 400);
    }

    [Fact]
    public void Yamal_SatelliteNameAndPosition()
    {
        var sat = Parse("yamal601");
        Assert.Equal("yamal601", sat.Id);
        Assert.Equal("Yamal 601", sat.Name);
        Assert.Equal("49.0°E", sat.Position);
    }

    [Fact]
    public void Yamal_TvCountMatchesPage()
    {
        // Число ТВ-строк в урезанной фикстуре (с VPID и непустым именем); радио/data отсеиваются. Посчитано по строкам таблицы.
        // Было 126 на полной странице (162 строки канала), фикстура урезана до 28 ТВ-строк.
        Assert.Equal(28, Parse("yamal601").Channels.Count);
    }

    [Fact]
    public void Astra_SatelliteNameAndPosition()
    {
        var sat = Parse("astra1p");
        Assert.Equal("Astra 1P", sat.Name);
        Assert.Equal("19.2°E", sat.Position);
    }

    [Fact]
    public void Yamal_KnownChannels_FieldsFromTransponderAndRow()
    {
        var sat = Parse("yamal601");

        var rt = Assert.Single(sat.Channels, c => c.Name == "Russia Today");
        Assert.Equal(3586.0, rt.FreqMhz);
        Assert.Equal('L', rt.Pol);
        Assert.Equal(8000, rt.SymbolRate);
        Assert.Equal("3/4", rt.Fec);
        Assert.Equal("DVB-S", rt.Standard);
        Assert.Equal("Russia", rt.Country);
        Assert.True(rt.Fta);

        // Первая «Rossiya 1» — на 3594 R, колонка Encryption «Clear Roscrypt» — открытый канал.
        var r1 = sat.Channels.First(c => c.Name == "Rossiya 1");
        Assert.Equal(3594.0, r1.FreqMhz);
        Assert.Equal('R', r1.Pol);
        Assert.Equal(5120, r1.SymbolRate);
        Assert.Equal("3/4", r1.Fec);
        Assert.Equal("DVB-S2", r1.Standard);
        Assert.True(r1.Fta);
    }

    [Fact]
    public void Astra_KnownChannel_ScrambledIsNotFta()
    {
        var sat = Parse("astra1p");
        var punt = sat.Channels.First(c => c.Name == "A Punt HD");
        Assert.Equal(10729.0, punt.FreqMhz);
        Assert.Equal('V', punt.Pol);
        Assert.Equal(23500, punt.SymbolRate);
        Assert.Equal("3/4", punt.Fec);
        Assert.Equal("DVB-S2", punt.Standard);
        Assert.Equal("Spain", punt.Country);
        Assert.False(punt.Fta);   // Nagravision 3
    }

    [Theory]
    [InlineData("astra1p")]
    [InlineData("yamal601")]
    public void Fixture_NoRadioOrData(string id)
    {
        var sat = Parse(id);
        Assert.DoesNotContain(sat.Channels, c => c.Name.StartsWith("Радио", StringComparison.Ordinal));
        Assert.DoesNotContain(sat.Channels, c => c.Name == "D1-2");
        Assert.DoesNotContain(sat.Channels, c => c.Name.Length == 0);
    }

    [Theory]
    [InlineData("astra1p")]
    [InlineData("yamal601")]
    public void Fixture_FrequencyAndPolarizationAreValid(string id)
    {
        var sat = Parse(id);
        Assert.All(sat.Channels, c => Assert.InRange(c.FreqMhz, 3000, 13000));
        Assert.All(sat.Channels, c => Assert.Contains(c.Pol, new[] { 'H', 'V', 'L', 'R' }));
    }

    [Fact]
    public void Astra_EqualFrequency_FtaFirst()
    {
        var sat = Parse("astra1p");
        var pairs = sat.Channels.Zip(sat.Channels.Skip(1));
        Assert.All(pairs, p => Assert.False(p.First.FreqMhz == p.Second.FreqMhz && !p.First.Fta && p.Second.Fta));
    }

    [Fact]
    public void EqualFrequency_FtaSortsFirst()
    {
        const string html = "<title>Order (1.0&deg;E)</title>" +
            "<tr data-frequency-id=\"1\" class=\"frequency-row\"><td>1.0E</td><td>S</td><td>11000 H</td><td>1</td><td>C</td>" +
            "<td>DVB-S</td><td>QPSK</td><td>27500</td><td>3/4</td><td></td><td></td></tr>" +
            "<tr data-channel-id=\"4\"><td></td><td>Zed</td><td>Spain</td><td>General</td><td>P</td><td>Nagravision 3</td><td>1</td><td>33</td></tr>" +
            "<tr data-channel-id=\"5\"><td></td><td>Alpha</td><td>Spain</td><td>General</td><td>P</td><td>Clear</td><td>1</td><td>33</td></tr>";

        var sat = SatTvSource.Parse(html, "order", At);

        Assert.Equal(new[] { "Alpha", "Zed" }, sat.Channels.Select(c => c.Name));
        Assert.True(sat.Channels[0].Fta);
        Assert.False(sat.Channels[1].Fta);
    }

    [Fact]
    public void Names_EntitiesDecoded()
    {
        var sat = Parse("astra1p");
        Assert.Contains(sat.Channels, c => c.Name == "Discovery Channel Spain & Portugal");
        Assert.All(sat.Channels, c => Assert.DoesNotContain("&amp;", c.Name));
        Assert.All(sat.Channels, c => Assert.DoesNotContain("  ", c.Name));
    }

    [Fact]
    public void Channels_SortedByFrequencyThenName()
    {
        var sat = Parse("astra1p");   // урезанная фикстура: 29 ТВ-строк
        var ordered = sat.Channels.OrderBy(c => c.FreqMhz).ThenByDescending(c => c.Fta).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(ordered.Select(c => (c.FreqMhz, c.Name)), sat.Channels.Select(c => (c.FreqMhz, c.Name)));
    }

    [Fact]
    public void Channels_CappedAt400_OnSyntheticPage()
    {
        // Синтетическая страница в той же разметке, что у KingOfSat: 3 транспондера по 150 ТВ-каналов = 450 строк.
        var sat = SatTvSource.Parse(BuildPage(3, 150), "synthetic", At);
        Assert.Equal(400, sat.Channels.Count);
        Assert.Equal(150, sat.Channels.Count(c => c.FreqMhz == 11000));
        Assert.Equal(150, sat.Channels.Count(c => c.FreqMhz == 11100));
        // Третий транспондер отрезан по порядку имён: остаются первые 100.
        Assert.Equal(100, sat.Channels.Count(c => c.FreqMhz == 11200));
        Assert.Equal("ch2-099", sat.Channels.Where(c => c.FreqMhz == 11200).Last().Name);
        var ordered = sat.Channels.OrderBy(c => c.FreqMhz).ThenByDescending(c => c.Fta).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(ordered.Select(c => (c.FreqMhz, c.Name)), sat.Channels.Select(c => (c.FreqMhz, c.Name)));
    }

    private static string BuildPage(int transponders, int channelsEach)
    {
        var sb = new StringBuilder("<title>Synthetic (1.0&deg;E) - KingOfSat</title>");
        int ch = 0;
        for (int t = 0; t < transponders; t++)
        {
            sb.Append($"<tr data-frequency-id=\"{t + 1}\" class=\"frequency-row\"><td>1.0E</td><td>S</td><td>{11000 + 100 * t} H</td><td>1</td><td>C</td><td>DVB-S</td><td>QPSK</td><td>27500</td><td>3/4</td><td></td><td></td></tr>");
            for (int i = 0; i < channelsEach; i++, ch++)
                sb.Append($"<tr data-channel-id=\"{ch + 1}\"><td></td><td>ch{t}-{i:000}</td><td>Spain</td><td>General</td><td>P</td><td>Clear</td><td>1</td><td>33</td></tr>");
        }
        return sb.ToString();
    }

    // ---- загрузка через FakeHttp ----

    [Fact]
    public async Task Fetch_ParsesPageFromHttp()
    {
        var fake = new FakeHttp().OnFile("sat-yamal601", "kingofsat-yamal601.html", "text/html");
        var src = SatTvSource.Create("yamal601")!;

        var sat = Assert.IsType<SatTvSatellite>(await src.FetchAsync(fake.Client(), CancellationToken.None));

        Assert.Equal("Yamal 601", sat.Name);
        Assert.Equal("sattv", src.Key);
        Assert.Equal("sattv.yamal601", src.Name);
        Assert.Equal(TimeSpan.FromHours(24), src.Interval);
        Assert.False(sat.FromCache);
        Assert.Equal("https://en.kingofsat.info/sat-yamal601", Assert.Single(fake.Requests));
    }

    [Fact]
    public async Task Fetch_EmptyPage_Throws()
    {
        var fake = new FakeHttp().On("sat-nothing", "<html><body>no table</body></html>", "text/html");
        var src = SatTvSource.Create("nothing")!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => src.FetchAsync(fake.Client(), CancellationToken.None));
    }

    [Fact]
    public void Create_RejectsBadIds()
    {
        Assert.NotNull(SatTvSource.Create("astra1p"));
        Assert.NotNull(SatTvSource.Create("e36wa"));
        Assert.Null(SatTvSource.Create(""));
        Assert.Null(SatTvSource.Create("Astra 1P"));
        Assert.Null(SatTvSource.Create("../etc"));
        Assert.Null(SatTvSource.Create("sat-astra1p?x=1"));
    }

    // ---- битый HTML ----

    [Theory]
    [InlineData("")]
    [InlineData("not html at all")]
    [InlineData("<<<>>>garbage<tr data-channel-id=\"5\"><td>x</td></tr>")]
    [InlineData("<title>Broken (19.2&deg;E")]
    public void BrokenHtml_GivesEmptyList_WithoutException(string html)
    {
        var sat = SatTvSource.Parse(html, "broken", At);
        Assert.Empty(sat.Channels);
        Assert.Equal("broken", sat.Id);
    }

    [Fact]
    public void Channel_WithoutTransponderAbove_IsSkipped()
    {
        const string html = "<title>Test (1.0&deg;E) - KingOfSat</title>" +
            "<tr data-channel-id=\"1\"><td></td><td class=\"ch\">Orphan</td><td>Russia</td><td>News</td><td>P</td><td>Clear</td><td>1</td><td>33</td></tr>";
        var sat = SatTvSource.Parse(html, "test", At);
        Assert.Empty(sat.Channels);
        Assert.Equal("Test", sat.Name);
        Assert.Equal("1.0°E", sat.Position);
    }

    // ---- слияние ----

    [Fact]
    public void Merge_KeepsConfigOrder_DropsEmpty()
    {
        var perSource = new List<DataSnapshot>
        {
            Sat("b", 2, false, At),
            Sat("a", 0, false, At.AddMinutes(5)),
            Sat("c", 1, false, At.AddMinutes(2)),
        };

        var set = Assert.IsType<SatTvSetSnapshot>(SatTvCatalog.Merge(perSource));

        Assert.Equal(new[] { "b", "c" }, set.Satellites.Select(s => s.Id));
        Assert.Equal(At.AddMinutes(2), set.FetchedAt);   // максимум по оставшимся спутникам; пустой (+5 мин) не учитывается
        Assert.False(set.FromCache);
    }

    [Fact]
    public void Merge_FromCacheOnlyWhenAllFromCache()
    {
        var mixed = new List<DataSnapshot> { Sat("a", 1, true, At), Sat("b", 1, false, At) };
        Assert.False(Assert.IsType<SatTvSetSnapshot>(SatTvCatalog.Merge(mixed)).FromCache);

        var cached = new List<DataSnapshot> { Sat("a", 1, true, At), Sat("b", 1, true, At) };
        Assert.True(Assert.IsType<SatTvSetSnapshot>(SatTvCatalog.Merge(cached)).FromCache);
    }

    [Fact]
    public void Merge_NoSatellitesWithChannels_IsNull()
    {
        var empty = new List<DataSnapshot> { Sat("a", 0, false, At), Sat("b", 0, false, At) };
        Assert.Null(SatTvCatalog.Merge(empty));
        Assert.Null(SatTvCatalog.Merge(new List<DataSnapshot>()));
    }

    [Fact]
    public void DefaultIds_AreFiveUniqueSlugs()
    {
        Assert.Equal(5, SatTvCatalog.DefaultIds.Length);
        Assert.Equal(SatTvCatalog.DefaultIds.Length, SatTvCatalog.DefaultIds.Distinct().Count());
        Assert.All(SatTvCatalog.DefaultIds, id => Assert.NotNull(SatTvSource.Create(id)));
    }

    // ---- хаб: один спутник тоже идёт через слияние ----

    private static DataSnapshot? MergeKey(string key, IReadOnlyList<DataSnapshot> perSource) =>
        key == "sattv" ? SatTvCatalog.Merge(perSource) : perSource.MaxBy(s => s.FetchedAt);

    [Fact]
    public void Hub_SingleSattvSource_PublishesSetSnapshot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phosphor-sattv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Кеш одного спутника в формате DataHub: имя типа, строка, JSON.
            var json = JsonSerializer.Serialize(Parse("yamal601"), typeof(SatTvSatellite));
            File.WriteAllText(Path.Combine(dir, "sattv.yamal601.json"), nameof(SatTvSatellite) + "\n" + json);

            using var hub = new DataHub(new IDataSource[] { SatTvSource.Create("yamal601")! }, online: false, _ => { }, MergeKey, dir);
            hub.Start();

            Assert.True(hub.TryGet("sattv", out var snap, out _));
            var set = Assert.IsType<SatTvSetSnapshot>(snap);
            Assert.Equal("yamal601", Assert.Single(set.Satellites).Id);
            Assert.True(set.FromCache);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- живой источник (не входит в обычный прогон) ----

    [Trait("Category", "Live")]
    [Fact]
    public async Task Live_FetchesRealPage()
    {
        using var http = new HttpClient(new CurlHttp()) { Timeout = TimeSpan.FromSeconds(120) };
        var sat = Assert.IsType<SatTvSatellite>(await SatTvSource.Create("yamal601")!.FetchAsync(http, CancellationToken.None));
        Assert.True(sat.Channels.Count > 20, $"каналов {sat.Channels.Count}");
        Assert.Contains(sat.Channels, c => c.Name == "Russia Today");
    }
}
