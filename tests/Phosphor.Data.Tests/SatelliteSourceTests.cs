using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Phosphor.Core;

namespace Phosphor.Data.Tests;

public sealed class SatelliteSourceTests
{
    private static readonly JsonSerializerOptions CacheJson = new()
    {
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    // Москва: при широте 55.75 окно 43.25..68.25N, ширина 25/cos(lat) градусов
    private static (double Lat, double Lon, string Name)? Moscow() => (55.75, 37.62, "МОСКВА");
    private static Func<(double Lat, double Lon, string Name)?> MoscowWhere => Moscow;

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static byte[] FixtureBytes(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static FakeHttp MeteosatHttp(string? capsDefaultTime = null)
    {
        var caps = Fixture("sat-meteosat-caps.xml");
        if (capsDefaultTime is not null) caps = caps.Replace("2026-10-09T13:10:00Z", capsDefaultTime);
        return new FakeHttp()
            .On("request=GetCapabilities", caps, "application/xml")
            .OnFile("request=GetMap", "sat-meteosat-frame.png", "image/png");
    }

    private static List<string> GetMaps(FakeHttp fake) => fake.Requests.Where(r => r.Contains("request=GetMap")).ToList();

    private static string TimeOf(string url) => url.Substring(url.IndexOf("time=", StringComparison.Ordinal) + 5);

    private static DateTimeOffset At(int h, int m) => new(2026, 10, 9, h, m, 0, TimeSpan.Zero);

    private static SatelliteFeed Feed(string id, int frames) => new(id, "L", new SatelliteArea(SatelliteProjection.Equirect, 0, 0, 1, 1, 0, 1,
        double.NaN, double.NaN, null),
        Enumerable.Range(0, frames).Select(i => new SatelliteFrame(At(12, i), 1, 1, new byte[3])).ToList(),
        At(12, 0), false);

    private static byte[] BlackPng()
    {
        using var bmp = new Bitmap(550, 550, PixelFormat.Format24bppRgb);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static byte[] BlackJpeg()
    {
        using var bmp = new Bitmap(480, 270, PixelFormat.Format24bppRgb);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Jpeg);
        return ms.ToArray();
    }

    private static IDataSource Make(string id, Func<(double Lat, double Lon, string Name)?> where, TimeSpan? pause = null) =>
        SatelliteSource.Create(id, where, pause ?? TimeSpan.Zero)!;

    // ---- meteosat ----

    [Fact]
    public async Task Meteosat_EmptyCache_FetchesTwelveHistoryFramesAscending()
    {
        var fake = MeteosatHttp();
        var src = Make(SatelliteSource.MeteosatId, MoscowWhere);

        var feed = Assert.IsType<SatelliteFeed>(await src.FetchAsync(fake.Client(), CancellationToken.None));

        Assert.Equal("satellite.meteosat", src.Name);
        Assert.Equal("satellite", src.Key);
        Assert.Equal(TimeSpan.FromMinutes(10), src.Interval);
        Assert.Equal("meteosat", feed.Id);
        Assert.Contains("METEOSAT-12", feed.Label);
        Assert.False(feed.FromCache);

        var times = feed.Frames.Select(f => f.TakenAt).ToList();
        Assert.Equal(12, times.Count);
        Assert.Equal(At(11, 20), times[0]);     // последний 13:10 минус 110 мин
        Assert.Equal(At(13, 10), times[^1]);    // последний кадр слоя (default GetCapabilities)
        for (int i = 1; i < times.Count; i++) Assert.Equal(TimeSpan.FromMinutes(10), times[i] - times[i - 1]);

        // Запросы истории: 12 GetMap с явным time по возрастанию
        var maps = GetMaps(fake);
        Assert.Equal(12, maps.Count);
        Assert.Equal(times.Select(t => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
            maps.Select(TimeOf));
    }

    [Fact]
    public async Task Meteosat_Url_HasLatLonBboxOrderAndTrueProportions()
    {
        var fake = MeteosatHttp();
        await Make(SatelliteSource.MeteosatId, MoscowWhere).FetchAsync(fake.Client(), CancellationToken.None);

        var url = GetMaps(fake)[0];
        Assert.Contains("crs=EPSG:4326", url);
        Assert.Contains("version=1.3.0", url);
        var bbox = System.Net.WebUtility.UrlDecode(url.Split("bbox=")[1].Split('&')[0]).Split(',')
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        double s = bbox[0], w = bbox[1], n = bbox[2], e = bbox[3];

        // Порядок WMS 1.3.0 + EPSG:4326: S, W, N, E
        Assert.Equal(43.25, s, 6);
        Assert.Equal(68.25, n, 6);
        Assert.True(w < 37.62 && 37.62 < e, $"долгота точки вне окна: {w}..{e}");
        // Ширина в градусах = высота / cos(lat)
        Assert.Equal(25 / Math.Cos(55.75 * Math.PI / 180), e - w, 2);

        // Пиксели: высота пропорциональна окну (ширина 384)
        Assert.Contains("width=384", url);
        Assert.Contains($"height={(int)Math.Round(384 * 25 / (e - w))}", url);
    }

    [Fact]
    public async Task Meteosat_NewerLatest_ShiftsRingByOneFrame()
    {
        var src = Make(SatelliteSource.MeteosatId, MoscowWhere);
        var first = Assert.IsType<SatelliteFeed>(await src.FetchAsync(MeteosatHttp().Client(), CancellationToken.None));

        var fake = MeteosatHttp("2026-10-09T13:20:00Z");
        var second = Assert.IsType<SatelliteFeed>(await src.FetchAsync(fake.Client(), CancellationToken.None));

        // Только один новый кадр; старейший выпал
        var map = Assert.Single(GetMaps(fake));
        Assert.Equal(At(13, 20), DateTimeOffset.Parse(TimeOf(map), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
        Assert.Equal(12, second.Frames.Count);
        Assert.Equal(At(11, 30), second.Frames[0].TakenAt);
        Assert.Equal(At(13, 20), second.Frames[^1].TakenAt);
        Assert.Equal(first.Frames[1].TakenAt, second.Frames[0].TakenAt);   // старые кадры сохранились, не перекачаны
        Assert.Equal(first.Frames[11].TakenAt, second.Frames[10].TakenAt);
    }

    [Fact]
    public async Task Meteosat_RestoredRing_DoesNotRefetchHistory()
    {
        var feed = Assert.IsType<SatelliteFeed>(await Make(SatelliteSource.MeteosatId, MoscowWhere)
            .FetchAsync(MeteosatHttp().Client(), CancellationToken.None));

        var src = Make(SatelliteSource.MeteosatId, MoscowWhere);
        ((IRestorable)src).Restore(feed);
        var fake = MeteosatHttp();
        var again = Assert.IsType<SatelliteFeed>(await src.FetchAsync(fake.Client(), CancellationToken.None));

        Assert.Empty(GetMaps(fake));   // все 12 кадров уже в кеше
        Assert.Equal(12, again.Frames.Count);
    }

    [Fact]
    public async Task Meteosat_FailedHistorySlot_IsNotRequestedAgain()
    {
        // 11:50 отдаёт пустой ответ (не картинка) — это пропуск в середине кольца
        var caps = Fixture("sat-meteosat-caps.xml");
        var fake1 = new FakeHttp()
            .On("request=GetCapabilities", caps, "application/xml")
            .OnBytes("time=2026-10-09T11:50:00Z", Array.Empty<byte>(), "image/png")
            .OnFile("request=GetMap", "sat-meteosat-frame.png", "image/png");
        var src = Make(SatelliteSource.MeteosatId, MoscowWhere);
        var first = Assert.IsType<SatelliteFeed>(await src.FetchAsync(fake1.Client(), CancellationToken.None));

        Assert.Equal(12, GetMaps(fake1).Count);
        Assert.Equal(11, first.Frames.Count);
        Assert.DoesNotContain(At(11, 50), first.Frames.Select(f => f.TakenAt));

        // Следующее обновление (13:20): 11:50 больше не запрашивается, в кольце дырка
        var fake2 = MeteosatHttp("2026-10-09T13:20:00Z");
        var second = Assert.IsType<SatelliteFeed>(await src.FetchAsync(fake2.Client(), CancellationToken.None));

        var maps = GetMaps(fake2);
        var only = Assert.Single(maps);
        Assert.Equal(At(13, 20), DateTimeOffset.Parse(TimeOf(only), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
        Assert.Equal(11, second.Frames.Count);
        Assert.DoesNotContain(At(11, 50), second.Frames.Select(f => f.TakenAt));
    }

    [Fact]
    public async Task Meteosat_FailedNewestSlot_IsRetriedOnNextUpdate()
    {
        // 13:10 (самый свежий) пока не отдан — перезапросим на следующем обновлении
        var caps = Fixture("sat-meteosat-caps.xml");
        var fake1 = new FakeHttp()
            .On("request=GetCapabilities", caps, "application/xml")
            .OnBytes("time=2026-10-09T13:10:00Z", Array.Empty<byte>(), "image/png")
            .OnFile("request=GetMap", "sat-meteosat-frame.png", "image/png");
        var src = Make(SatelliteSource.MeteosatId, MoscowWhere);
        var first = Assert.IsType<SatelliteFeed>(await src.FetchAsync(fake1.Client(), CancellationToken.None));
        Assert.Equal(11, first.Frames.Count);

        var fake2 = MeteosatHttp();
        var second = Assert.IsType<SatelliteFeed>(await src.FetchAsync(fake2.Client(), CancellationToken.None));

        var only = Assert.Single(GetMaps(fake2));
        Assert.Equal(At(13, 10), DateTimeOffset.Parse(TimeOf(only), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
        Assert.Equal(12, second.Frames.Count);
    }

    [Fact]
    public async Task Meteosat_CityChange_ResetsRing()
    {
        var feed = Assert.IsType<SatelliteFeed>(await Make(SatelliteSource.MeteosatId, MoscowWhere)
            .FetchAsync(MeteosatHttp().Client(), CancellationToken.None));

        var src = Make(SatelliteSource.MeteosatId, () => (48.85, 2.35, "ПАРИЖ"));
        ((IRestorable)src).Restore(feed);
        var fake = MeteosatHttp();
        await src.FetchAsync(fake.Client(), CancellationToken.None);

        Assert.Equal(12, GetMaps(fake).Count);   // окно другое — история качается заново
    }

    [Fact]
    public async Task Meteosat_HistoryRequests_ArePacedByOneSecond()
    {
        var fake = MeteosatHttp();
        var src = Make(SatelliteSource.MeteosatId, MoscowWhere, TimeSpan.FromSeconds(1));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await src.FetchAsync(fake.Client(), CancellationToken.None);
        sw.Stop();

        Assert.Equal(12, GetMaps(fake).Count);
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(11), $"паузы между 12 запросами: {sw.Elapsed}");
    }

    [Fact]
    public async Task Meteosat_NoPoint_UsesDefaultWindow()
    {
        var fake = MeteosatHttp();
        var feed = Assert.IsType<SatelliteFeed>(await Make(SatelliteSource.MeteosatId, () => null)
            .FetchAsync(fake.Client(), CancellationToken.None));

        Assert.Equal(45, feed.Area.South, 6);
        Assert.Equal(65, feed.Area.North, 6);
        Assert.Equal(20, feed.Area.West, 6);
        Assert.Equal(60, feed.Area.East, 6);
        Assert.True(double.IsNaN(feed.Area.MarkLat));
        Assert.Null(feed.Area.MarkName);
    }

    [Fact]
    public async Task Meteosat_FramesAreDownscaledToSideAndHaveRgbSize()
    {
        var feed = Assert.IsType<SatelliteFeed>(await Make(SatelliteSource.MeteosatId, MoscowWhere)
            .FetchAsync(MeteosatHttp().Client(), CancellationToken.None));

        foreach (var f in feed.Frames)
        {
            Assert.Equal(384, f.Width);
            Assert.Equal(216, f.Height);
            Assert.Equal(f.Width * f.Height * 3, f.Rgb.Length);
        }
    }

    // ---- himawari ----

    /// <summary>latest.json (13:20) и реальные плитки одного слота (по умолчанию 13:20).</summary>
    private static FakeHttp HimawariHttp(string slot = "132000") =>
        AddTiles(new FakeHttp().On("latest.json", Fixture("sat-himawari-latest.json")), slot, null);

    private static FakeHttp AddTiles(FakeHttp fake, string slot, byte[]? blank)
    {
        string[] names = { "sat-him-0_0.png", "sat-him-1_0.png", "sat-him-0_1.png", "sat-him-1_1.png" };
        string[] keys = { "_0_0.png", "_1_0.png", "_0_1.png", "_1_1.png" };
        for (int i = 0; i < 4; i++)
            fake = blank is null
                ? fake.OnBytes(slot + keys[i], FixtureBytes(names[i]), "image/png")
                : fake.OnBytes(slot + keys[i], blank, "image/png");
        return fake;
    }

    [Fact]
    public async Task Himawari_LatestSlot_StitchesDiskTo480()
    {
        var src = Make(SatelliteSource.HimawariId, MoscowWhere);
        var feed = Assert.IsType<SatelliteFeed>(await src.FetchAsync(HimawariHttp().Client(), CancellationToken.None));

        Assert.Equal("satellite.himawari", src.Name);
        Assert.Contains("HIMAWARI-9", feed.Label);
        var frame = Assert.Single(feed.Frames);
        Assert.Equal(At(13, 20), frame.TakenAt);
        Assert.Equal(480, frame.Width);
        Assert.Equal(480, frame.Height);
        Assert.Equal(480 * 480 * 3, frame.Rgb.Length);

        Assert.Equal(SatelliteProjection.FullDisk, feed.Area.Projection);
        Assert.Equal(140.7, feed.Area.SubLon, 6);
        Assert.Equal(0.99, feed.Area.DiskScale, 6);
    }

    [Fact]
    public async Task Himawari_MarkIsCityWhenVisible_OtherwiseTokyo()
    {
        // Москва вне видимой полусфера (угол ~97°) — метка Токио
        var far = Assert.IsType<SatelliteFeed>(await Make(SatelliteSource.HimawariId, MoscowWhere)
            .FetchAsync(HimawariHttp().Client(), CancellationToken.None));
        Assert.Equal(35.68, far.Area.MarkLat, 6);
        Assert.Equal(139.69, far.Area.MarkLon, 6);
        Assert.Equal("ТОКИО", far.Area.MarkName);

        // Осака видна (угол ~6°) — метка города
        var near = Assert.IsType<SatelliteFeed>(await Make(SatelliteSource.HimawariId, () => (35.0, 135.0, "ОСАКА"))
            .FetchAsync(HimawariHttp().Client(), CancellationToken.None));
        Assert.Equal("ОСАКА", near.Area.MarkName);
        Assert.Equal(35.0, near.Area.MarkLat, 6);
    }

    [Fact]
    public async Task Himawari_MissingLatestSlot_RollsBackTenMinutes()
    {
        // 13:20 ещё не выложен (404), 13:10 есть
        var fake = HimawariHttp(slot: "131000");
        var feed = Assert.IsType<SatelliteFeed>(await Make(SatelliteSource.HimawariId, MoscowWhere)
            .FetchAsync(fake.Client(), CancellationToken.None));

        Assert.Equal(At(13, 10), Assert.Single(feed.Frames).TakenAt);
        Assert.Contains(fake.Requests, r => r.Contains("132000_0_0.png"));
        Assert.Contains(fake.Requests, r => r.Contains("131000_0_0.png"));
    }

    [Fact]
    public async Task Himawari_NoImagePlaceholder_RollsBackTenMinutes()
    {
        // 13:20 отдаёт почти чёрную заглушку (крошечные PNG), 13:10 — настоящий кадр
        var black = BlackPng();
        var fake = AddTiles(new FakeHttp().On("latest.json", Fixture("sat-himawari-latest.json")), "132000", black);
        fake = AddTiles(fake, "131000", null);
        var feed = Assert.IsType<SatelliteFeed>(await Make(SatelliteSource.HimawariId, MoscowWhere)
            .FetchAsync(fake.Client(), CancellationToken.None));

        Assert.Equal(At(13, 10), Assert.Single(feed.Frames).TakenAt);
    }

    [Fact]
    public async Task Himawari_NothingAvailable_Throws()
    {
        var fake = new FakeHttp().On("latest.json", Fixture("sat-himawari-latest.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Make(SatelliteSource.HimawariId, MoscowWhere).FetchAsync(fake.Client(), CancellationToken.None));
    }

    // ---- viirs ----

    private static string DayUtc(int back) => DateTime.UtcNow.Date.AddDays(-back).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Viirs_UsesYesterday_AndSkipsWhenAlreadyInRing()
    {
        var yesterday = DayUtc(1);
        var fake = new FakeHttp().OnFile("TIME=" + yesterday, "sat-viirs.jpg", "image/jpeg");
        var src = Make(SatelliteSource.ViirsId, MoscowWhere);

        var feed = Assert.IsType<SatelliteFeed>(await src.FetchAsync(fake.Client(), CancellationToken.None));
        Assert.Equal("satellite.viirs", src.Name);
        Assert.Equal(TimeSpan.FromHours(6), src.Interval);
        var frame = Assert.Single(feed.Frames);
        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-1), DateTimeKind.Utc)), frame.TakenAt);
        Assert.Equal(480, frame.Width);
        Assert.Equal(270, frame.Height);
        Assert.Equal(1, fake.Requests.Count(r => r.Contains("TIME=" + yesterday)));

        var again = new FakeHttp();
        await src.FetchAsync(again.Client(), CancellationToken.None);
        Assert.Empty(again.Requests);
    }

    [Fact]
    public async Task Viirs_DarkYesterday_FallsBackToDayBefore()
    {
        var fake = new FakeHttp()
            .OnBytes("TIME=" + DayUtc(1), BlackJpeg(), "image/jpeg")
            .OnFile("TIME=" + DayUtc(2), "sat-viirs.jpg", "image/jpeg");
        var feed = Assert.IsType<SatelliteFeed>(await Make(SatelliteSource.ViirsId, MoscowWhere)
            .FetchAsync(fake.Client(), CancellationToken.None));

        var frame = Assert.Single(feed.Frames);
        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-2), DateTimeKind.Utc)), frame.TakenAt);
        Assert.Equal(2, fake.Requests.Count);
    }

    // ---- слияние и кеш ----

    [Fact]
    public void Merge_OrdersMeteosatHimawariViirs_SkipsEmpty()
    {
        var parts = new DataSnapshot[]
        {
            Feed(SatelliteSource.ViirsId, 1),
            Feed(SatelliteSource.HimawariId, 0),
            Feed(SatelliteSource.MeteosatId, 12),
        };
        var set = Assert.IsType<SatelliteSetSnapshot>(SatelliteCatalog.Merge(parts));
        Assert.Equal(new[] { "meteosat", "viirs" }, set.Feeds.Select(f => f.Id));

        Assert.Null(SatelliteCatalog.Merge(new DataSnapshot[] { Feed(SatelliteSource.HimawariId, 0) }));
    }

    [Fact]
    public async Task Cache_RoundTripThroughDataHub_RestoresRingWithoutRefetch()
    {
        var feed = Assert.IsType<SatelliteFeed>(await Make(SatelliteSource.MeteosatId, MoscowWhere)
            .FetchAsync(MeteosatHttp().Client(), CancellationToken.None));

        var dir = Path.Combine(Path.GetTempPath(), "phosphor-sat-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var text = feed.GetType().Name + "\n" + JsonSerializer.Serialize(feed, feed.GetType(), CacheJson);
            File.WriteAllText(Path.Combine(dir, "satellite.meteosat.json"), text);
            Assert.True(new FileInfo(Path.Combine(dir, "satellite.meteosat.json")).Length < 5 * 1024 * 1024);

            var src = Make(SatelliteSource.MeteosatId, MoscowWhere);
            using var hub = new DataHub(new[] { src }, online: false, _ => { }, Merge, dir);
            hub.Start();

            Assert.True(hub.TryGet("satellite", out var snap, out _));
            var set = Assert.IsType<SatelliteSetSnapshot>(snap);
            Assert.True(set.FromCache);
            var restored = Assert.Single(set.Feeds);
            Assert.Equal(12, restored.Frames.Count);
            Assert.Equal(feed.Frames[3].Rgb, restored.Frames[3].Rgb);
            Assert.Equal(feed.Area.Projection, restored.Area.Projection);
            Assert.True(double.IsNaN(restored.Area.MarkLat) == double.IsNaN(feed.Area.MarkLat));

            // Кеш уже содержит 13:10 и всю историю — сеть не нужна для повторной выборки
            var fake = MeteosatHttp();
            await src.FetchAsync(fake.Client(), CancellationToken.None);
            Assert.Empty(GetMaps(fake));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static DataSnapshot? Merge(string key, IReadOnlyList<DataSnapshot> perSource) =>
        key == "satellite" ? SatelliteCatalog.Merge(perSource) : perSource.MaxBy(s => s.FetchedAt);

    // ---- живые источники (не входят в обычный прогон) ----

    [Trait("Category", "Live")]
    [Theory]
    [InlineData("meteosat")]
    [InlineData("himawari")]
    [InlineData("viirs")]
    public async Task Live_FetchesRealFrames(string id)
    {
        using var http = new HttpClient(new CurlHttp()) { Timeout = TimeSpan.FromSeconds(120) };
        var feed = Assert.IsType<SatelliteFeed>(await Make(id, MoscowWhere, TimeSpan.Zero).FetchAsync(http, CancellationToken.None));
        Assert.NotEmpty(feed.Frames);
        Assert.All(feed.Frames, f => Assert.Equal(f.Width * f.Height * 3, f.Rgb.Length));
    }

    /// <summary>Проверка стаженного кеша: DataHub офлайн из PHOSPHOR_SAT_CACHE_OUT отдаёт SatelliteSetSnapshot из трёх лент.</summary>
    [Trait("Category", "Live")]
    [Fact]
    public void Live_StagedSceneCacheLoadsThroughHub()
    {
        var dir = Environment.GetEnvironmentVariable("PHOSPHOR_SAT_CACHE_OUT");
        if (string.IsNullOrWhiteSpace(dir)) return;

        var sources = SatelliteSource.Ids.Select(id => Make(id, MoscowWhere)).ToList();
        using var hub = new DataHub(sources, online: false, _ => { }, Merge, dir);
        hub.Start();

        Assert.True(hub.TryGet("satellite", out var snap, out _));
        var set = Assert.IsType<SatelliteSetSnapshot>(snap);
        Assert.True(set.FromCache);
        Assert.Equal(SatelliteSource.Ids, set.Feeds.Select(f => f.Id));
        Assert.All(set.Feeds, f => Assert.NotEmpty(f.Frames));
    }

    /// <summary>
    /// Снимок для сцены: из PHOSPHOR_SAT_CACHE_OUT (каталог) читает weather.json (точка), качает три ленты и пишет их
    /// в формате кеша DataHub: satellite.meteosat.json и т.д. Без переменной — пропуск.
    /// </summary>
    [Trait("Category", "Live")]
    [Fact]
    public async Task Live_WritesSatelliteCacheForScene()
    {
        var outDir = Environment.GetEnvironmentVariable("PHOSPHOR_SAT_CACHE_OUT");
        if (string.IsNullOrWhiteSpace(outDir)) return;

        var weatherText = File.ReadAllText(Path.Combine(outDir, "weather.json"));
        var weather = JsonSerializer.Deserialize<WeatherSnapshot>(weatherText[(weatherText.IndexOf('\n') + 1)..], CacheJson)!;
        Func<(double Lat, double Lon, string Name)?> where = () => (weather.Lat, weather.Lon, weather.City.ToUpperInvariant());

        using var http = new HttpClient(new CurlHttp()) { Timeout = TimeSpan.FromSeconds(120) };
        foreach (var id in SatelliteSource.Ids)
        {
            var src = Make(id, where, TimeSpan.FromSeconds(1));
            var feed = Assert.IsType<SatelliteFeed>(await src.FetchAsync(http, CancellationToken.None));
            var text = feed.GetType().Name + "\n" + JsonSerializer.Serialize(feed, feed.GetType(), CacheJson);
            File.WriteAllText(Path.Combine(outDir, src.Name + ".json"), text);
        }
    }
}
