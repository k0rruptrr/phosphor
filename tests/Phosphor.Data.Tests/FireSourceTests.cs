using System.Globalization;
using System.Text;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Data.Tests;

public sealed class FireSourceTests
{
    private readonly ITestOutputHelper _out;
    public FireSourceTests(ITestOutputHelper output) => _out = output;

    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private const string Header = "latitude,longitude,bright_ti4,scan,track,acq_date,acq_time,satellite,confidence,version,bright_ti5,frp,daynight";

    private static string MoscowFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fires-firms-moscow.csv"));

    /// <summary>Фрагмент CSV с произвольным порядком колонок (сюда же — проверка сопоставления по именам).</summary>
    private static string Line(double lat, double lon, string conf, string time, string day, double frp) =>
        $"{lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)},330,0.4,0.4,2026-10-09,{time},N20,{conf},2.0NRT,280,{frp.ToString(CultureInfo.InvariantCulture)},{day}";

    [Fact]
    public void RoundHalf_RoundsToNearestHalfDegree()
    {
        Assert.Equal(56.0, FireSource.RoundHalf(55.75));
        Assert.Equal(37.5, FireSource.RoundHalf(37.62));
        Assert.Equal(55.0, FireSource.RoundHalf(55.2));
        Assert.Equal(-0.5, FireSource.RoundHalf(-0.26));
    }

    [Fact]
    public async Task Moscow_FiltersSquareAroundCenter()
    {
        // Радиус 100 км: по широте ±100/111 = ±0.90°, по долготе ±100/(111·cos56°) ≈ ±1.61°.
        var snap = await Parse(MoscowFixture(), 100);

        Assert.Equal(56.0, snap.CenterLat);
        Assert.Equal(37.5, snap.CenterLon);
        Assert.Equal(100, snap.RadiusKm);
        Assert.Equal(4, snap.Points.Count);
        double dLat = 100 / 111.0, dLon = 100 / (111.0 * Math.Cos(56.0 * Math.PI / 180));
        foreach (var p in snap.Points)
        {
            Assert.InRange(p.Lat, 56.0 - dLat, 56.0 + dLat);
            Assert.InRange(p.Lon, 37.5 - dLon, 37.5 + dLon);
        }
    }

    [Fact]
    public async Task Points_SortedByFrpDescending()
    {
        var snap = await Parse(MoscowFixture(), 500);
        Assert.NotEmpty(snap.Points);
        for (int i = 1; i < snap.Points.Count; i++)
            Assert.True(snap.Points[i - 1].Frp >= snap.Points[i].Frp, $"порядок FRP нарушен на {i}");
    }

    [Fact]
    public async Task Point_FieldsParsedFromFixture()
    {
        // Строка 55.25668,38.74367 — VIIRS, время 2341 UTC, ночь, FRP 0.75.
        var snap = await Parse(MoscowFixture(), 100);
        var p = Assert.Single(snap.Points, q => Math.Abs(q.Lat - 55.25668) < 1e-6);
        Assert.Equal(38.74367, p.Lon, 5);
        Assert.Equal(0.75f, p.Frp, 2);
        Assert.Equal(301.83f, p.BrightK, 2);
        Assert.Equal("nominal", p.Confidence);
        Assert.False(p.Day);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 23, 41, 0, TimeSpan.Zero), p.AcquiredAt);
    }

    [Fact]
    public async Task Daytime_AndShortTime_Parsed()
    {
        // Строка 54.58882,39.88372: время "0952" (UTC), дневная (daynight = D), FRP 3.49.
        var snap = await Parse(MoscowFixture(), 500);
        var p = Assert.Single(snap.Points, q => Math.Abs(q.Lat - 54.58882) < 1e-6);
        Assert.True(p.Day);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 9, 52, 0, TimeSpan.Zero), p.AcquiredAt);
    }

    [Fact]
    public async Task Confidence_LetterAndWordForms_Mapped()
    {
        var csv = string.Join("\n", Header,
            Line(56.0, 37.5, "l", "0100", "N", 1), Line(56.0, 37.5, "n", "0100", "N", 1),
            Line(56.0, 37.5, "h", "0100", "N", 1), Line(56.0, 37.5, "low", "0100", "N", 1),
            Line(56.0, 37.5, "nominal", "0100", "N", 1), Line(56.0, 37.5, "high", "0100", "N", 1));
        var snap = await Parse(csv, 100);
        Assert.Equal(new[] { "low", "nominal", "high", "low", "nominal", "high" }, snap.Points.Select(p => p.Confidence));
    }

    [Fact]
    public async Task ColumnOrder_IsByName()
    {
        // Колонки переставлены: сопоставление — по именам из заголовка.
        var csv = "frp,daynight,acq_time,acq_date,confidence,longitude,latitude,bright_ti4\n" +
                  "2.5,D,0730,2026-10-10,h,37.6,55.8,320";
        var snap = await Parse(csv, 100);
        var p = Assert.Single(snap.Points);
        Assert.Equal(55.8, p.Lat, 5);
        Assert.Equal(2.5f, p.Frp);
        Assert.Equal(320f, p.BrightK);
        Assert.Equal("high", p.Confidence);
        Assert.True(p.Day);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 7, 30, 0, TimeSpan.Zero), p.AcquiredAt);
    }

    [Fact]
    public async Task BrokenLines_Skipped()
    {
        var csv = string.Join("\n", Header,
            "abc,def,330,0.4,0.4,2026-10-09,0100,N20,nominal,2.0NRT,280,1.0,N",
            "55.8,37.6,330,0.4,0.4,2026-13-45,0100,N20,nominal,2.0NRT,280,1.0,N",
            "55.8,37.6,330,0.4,0.4,2026-10-09,2599,N20,nominal,2.0NRT,280,1.0,N",
            "55.8,37.6",
            "",
            Line(55.8, 37.6, "n", "0100", "N", 2.0));
        var snap = await Parse(csv, 100);
        var p = Assert.Single(snap.Points);
        Assert.Equal(2.0f, p.Frp);
    }

    [Fact]
    public async Task Limit_Keeps2000StrongestPoints()
    {
        var sb = new StringBuilder(Header).Append('\n');
        for (int i = 0; i < 2500; i++)
            sb.Append(Line(56.0 + i * 1e-4, 37.5, "n", "0100", "N", i + 1)).Append('\n');
        var snap = await Parse(sb.ToString(), 500);

        Assert.Equal(2000, snap.Points.Count);
        Assert.Equal(2500f, snap.Points[0].Frp);
        Assert.Equal(501f, snap.Points[^1].Frp);   // 2500 - 2000 + 1: слабейшие 500 отброшены
    }

    [Fact]
    public async Task EmptyArea_ReturnsEmptySnapshot()
    {
        var snap = await Parse(MoscowFixture(), 100, centerLat: -40, centerLon: 150);
        Assert.Empty(snap.Points);
        Assert.False(snap.FromCache);
    }

    [Fact]
    public void BadHeader_Throws()
    {
        Assert.ThrowsAsync<InvalidDataException>(() =>
            FireSource.ParseAsync(new StringReader("foo,bar\n1,2\n"), 56, 37.5, "X", 100, Now, CancellationToken.None));
        Assert.ThrowsAsync<InvalidDataException>(() =>
            FireSource.ParseAsync(new StringReader(""), 56, 37.5, "X", 100, Now, CancellationToken.None));
    }

    [Fact]
    public async Task Fetch_UsesRoundedCenterFromWhere()
    {
        var fake = new FakeHttp().OnFile("firms.modaps.eosdis.nasa.gov", "fires-firms-moscow.csv", "text/csv");
        using var http = fake.Client();
        var src = new FireSource(() => (55.7558, 37.6173, "Москва"), 500);

        var snap = Assert.IsType<FireSnapshot>(await src.FetchAsync(http, CancellationToken.None));
        Assert.Equal(56.0, snap.CenterLat);
        Assert.Equal(37.5, snap.CenterLon);
        Assert.Equal("Москва", snap.CenterName);
        Assert.Equal(500, snap.RadiusKm);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task NoPoint_NoRequest()
    {
        var fake = new FakeHttp();
        using var http = fake.Client();
        var src = new FireSource(() => null, 500);

        Assert.Null(await src.FetchAsync(http, CancellationToken.None));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public void Interval_IsThreeHours()
    {
        var src = new FireSource(() => null, 500);
        Assert.Equal(TimeSpan.FromHours(3), src.Interval);
        Assert.Equal("fires", src.Key);
    }

    /// <summary>Живая проверка: один реальный запрос (~8 МБ). Не входит в обычный прогон (--filter "Category!=Live").</summary>
    [Trait("Category", "Live")]
    [Fact]
    public async Task Live_FetchesRealFiresAroundMoscow()
    {
        using var http = new HttpClient(new CurlHttp()) { Timeout = TimeSpan.FromSeconds(180) };
        var src = new FireSource(() => (55.7558, 37.6173, "Москва"), 500);

        var snap = Assert.IsType<FireSnapshot>(await src.FetchAsync(http, CancellationToken.None));
        _out.WriteLine($"FIRMS live: центр {snap.CenterLat}/{snap.CenterLon}, очагов {snap.Points.Count}");
        Assert.True(snap.Points.Count <= 2000);
        Assert.All(snap.Points, p => Assert.InRange(p.Lat, snap.CenterLat - 4.6, snap.CenterLat + 4.6));
    }

    private static Task<FireSnapshot> Parse(string csv, double radiusKm, double centerLat = 56.0, double centerLon = 37.5) =>
        FireSource.ParseAsync(new StringReader(csv), centerLat, centerLon, "МОСКВА", radiusKm, Now, CancellationToken.None);
}
