using System.Net;
using System.Text.Json;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Data.Tests;

public sealed class FlightSourceTests
{
    private readonly ITestOutputHelper _out;
    public FlightSourceTests(ITestOutputHelper output) => _out = output;

    private const string CityUrlPart = "point/55.7558/37.6173/100";
    private static readonly (double Lat, double Lon, string Name)? Moscow = (55.7558, 37.6173, "Москва");

    private static readonly IReadOnlyList<FlightSource.Station> Stations = FlightSource.ParseStations(new[]
    {
        "HELSINKI-VANTAA:60.32:24.95", "RIGA:56.92:23.97", "BELGRADE:44.82:20.29", "ISTANBUL:41.27:28.75",
    });

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private const string Empty = "{\"ac\":[],\"msg\":\"No error\",\"total\":0}";

    // --- разбор ответа ---

    [Fact]
    public void Belgrade_ParsesAircraftFields()
    {
        var (list, airborne) = FlightSource.Parse(Fixture("adsb-belgrade.json"), 44.82, 20.29);

        Assert.Equal(60, list.Count);
        Assert.Equal(60, airborne);
        var a = Assert.Single(list, x => x.Hex == "4b8e04");
        Assert.Equal("PGT45NE", a.Callsign);          // обрезаны пробелы
        Assert.Equal("B738", a.Type);
        Assert.Equal(45.396606, a.Lat, 6);
        Assert.Equal(18.141632, a.Lon, 6);
        Assert.Equal(37000, a.AltFt);
        Assert.Equal(441f, a.SpeedKt, 1);
        Assert.Equal(116.97f, a.TrackDeg, 2);
        Assert.Equal(0f, a.VertRateFpm);
    }

    [Fact]
    public void Belgrade_SortedByDistanceToCenter()
    {
        const double cLat = 44.82, cLon = 20.29;
        var (list, _) = FlightSource.Parse(Fixture("adsb-belgrade.json"), cLat, cLon);
        double Dist(Aircraft a)
        {
            double dy = (a.Lat - cLat) * 111, dx = (a.Lon - cLon) * 111 * Math.Cos(cLat * Math.PI / 180);
            return Math.Sqrt(dx * dx + dy * dy);
        }
        for (int i = 1; i < list.Count; i++)
            Assert.True(Dist(list[i - 1]) <= Dist(list[i]) + 1e-9, $"порядок по расстоянию нарушен на {i}");
    }

    [Fact]
    public void Moscow_ZeroAircraft()
    {
        var (list, airborne) = FlightSource.Parse(Fixture("adsb-moscow.json"), 56.0, 37.5);
        Assert.Empty(list);
        Assert.Equal(0, airborne);
    }

    [Fact]
    public void Ground_AltitudeIsMinusOne_AndNotAirborne()
    {
        const string json = "{\"ac\":[" +
            "{\"hex\":\"abc\",\"flight\":\"  \",\"t\":\"A320\",\"lat\":55.0,\"lon\":37.0,\"alt_baro\":\"ground\",\"gs\":12,\"track\":10,\"baro_rate\":0}," +
            "{\"hex\":\"def\",\"flight\":\"SU100\",\"lat\":55.1,\"lon\":37.1,\"alt_baro\":2500,\"gs\":200,\"track\":90,\"baro_rate\":-500}," +
            "{\"hex\":\"nol\",\"lon\":37.1,\"alt_baro\":100}" +   // без lat — пропускается
            "]}";
        var (list, airborne) = FlightSource.Parse(json, 55.0, 37.0);

        Assert.Equal(2, list.Count);
        Assert.Equal(1, airborne);
        var ground = Assert.Single(list, x => x.Hex == "abc");
        Assert.Equal(-1, ground.AltFt);
        Assert.Equal("", ground.Callsign);
        Assert.Equal("A320", ground.Type);
        var air = Assert.Single(list, x => x.Hex == "def");
        Assert.Equal(2500, air.AltFt);
        Assert.Equal(-500f, air.VertRateFpm);
    }

    [Fact]
    public void NotJson_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => FlightSource.Parse("<html>rate limited</html>", 55, 37));
        Assert.ThrowsAny<JsonException>(() => FlightSource.Parse("{\"msg\":\"no ac\"}", 55, 37));
    }

    [Fact]
    public void ParseStations_KeepsValidAndReportsBroken()
    {
        var warns = new List<string>();
        var list = FlightSource.ParseStations(new[] { "belgrade:44.82:20.29", "bad", "X:95:0", "Y:1:abc" }, warns.Add);

        var s = Assert.Single(list);
        Assert.Equal("BELGRADE", s.Name);
        Assert.Equal(44.82, s.Lat);
        Assert.Equal(3, warns.Count);
    }

    // --- логика опроса ---

    [Fact]
    public async Task Polls_CityThenStationsCyclic_ThenCityAgain()
    {
        var http = new ScriptedHttp(url => url.Contains(CityUrlPart) ? Fixture("adsb-moscow.json") : StationBody(url));
        using var client = http.Client();
        var src = new FlightSource(() => Moscow, 100, Stations);

        var snaps = new List<FlightSnapshot>();
        for (int i = 0; i < 12; i++)
            snaps.Add(Assert.IsType<FlightSnapshot>(await src.FetchAsync(client, CancellationToken.None)));

        // Опрос 1 — город (в воздухе 0 < 3). Опросы 2..11 — станции по кругу. Опрос 12 — снова город.
        Assert.False(snaps[0].Remote);
        Assert.Equal("МОСКВА", snaps[0].StationName);
        string[] cycle = { "HELSINKI-VANTAA", "RIGA", "BELGRADE", "ISTANBUL" };
        for (int i = 1; i <= 10; i++)
        {
            Assert.True(snaps[i].Remote, $"опрос {i + 1} должен быть станционным");
            Assert.Equal(cycle[(i - 1) % cycle.Length], snaps[i].StationName);
        }
        Assert.False(snaps[11].Remote);
        Assert.Equal("МОСКВА", snaps[11].StationName);

        Assert.Equal(2, http.Requests.Count(u => u.Contains(CityUrlPart)));
        Assert.Equal(10, http.Requests.Count(u => !u.Contains(CityUrlPart)));     // по одному запросу на станционный опрос
        _out.WriteLine(string.Join(", ", snaps.Select(s => s.StationName)));
    }

    [Fact]
    public async Task StationCoordinates_InSnapshot_AndRadiusInUrl()
    {
        var http = new ScriptedHttp(url => url.Contains(CityUrlPart) ? Fixture("adsb-moscow.json") : StationBody(url));
        using var client = http.Client();
        var src = new FlightSource(() => Moscow, 100, Stations);

        await src.FetchAsync(client, CancellationToken.None);                       // город
        var st = Assert.IsType<FlightSnapshot>(await src.FetchAsync(client, CancellationToken.None));

        Assert.True(st.Remote);
        Assert.Equal("HELSINKI-VANTAA", st.StationName);
        Assert.Equal(60.32, st.CenterLat);
        Assert.Equal(24.95, st.CenterLon);
        Assert.Equal(100.0, st.RadiusNm);
        Assert.Contains(http.Requests, u => u.EndsWith("/point/60.32/24.95/100", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CityWithAircraft_StopsStationMode()
    {
        // Город пуст -> 10 станционных опросов -> город снова, уже с 60 самолётами в воздухе:
        // станционный режим выключается, и следующий опрос снова идёт в город.
        bool busy = false;
        var http = new ScriptedHttp(url => url.Contains(CityUrlPart)
            ? (busy ? Fixture("adsb-belgrade.json") : Fixture("adsb-moscow.json"))
            : StationBody(url));
        using var client = http.Client();
        var src = new FlightSource(() => Moscow, 100, Stations);

        var first = Assert.IsType<FlightSnapshot>(await src.FetchAsync(client, CancellationToken.None));
        Assert.False(first.Remote);
        for (int i = 0; i < 10; i++)
            Assert.True(Assert.IsType<FlightSnapshot>(await src.FetchAsync(client, CancellationToken.None)).Remote);
        busy = true;
        var city = Assert.IsType<FlightSnapshot>(await src.FetchAsync(client, CancellationToken.None));
        Assert.False(city.Remote);
        Assert.Equal(60, city.Aircraft.Count);
        var next = Assert.IsType<FlightSnapshot>(await src.FetchAsync(client, CancellationToken.None));
        Assert.False(next.Remote);                                                 // станции выключены
    }

    [Fact]
    public async Task ErrorDoesNotAdvanceState()
    {
        // Первый городской ответ — не JSON: ошибка источника; следующий опрос снова идёт в город.
        int cityCalls = 0;
        var http = new ScriptedHttp(url =>
        {
            if (!url.Contains(CityUrlPart)) return StationBody(url);
            return ++cityCalls == 1 ? "<html>blocked</html>" : Fixture("adsb-moscow.json");
        });
        using var client = http.Client();
        var src = new FlightSource(() => Moscow, 100, Stations);

        await Assert.ThrowsAnyAsync<JsonException>(() => src.FetchAsync(client, CancellationToken.None));
        var snap = Assert.IsType<FlightSnapshot>(await src.FetchAsync(client, CancellationToken.None));
        Assert.False(snap.Remote);
        Assert.Equal(2, cityCalls);
    }

    [Fact]
    public async Task NoCity_NoRequest()
    {
        var http = new ScriptedHttp(_ => Empty);
        using var client = http.Client();
        var src = new FlightSource(() => null, 100, Stations);

        Assert.Null(await src.FetchAsync(client, CancellationToken.None));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void Interval_IsSixtySeconds()
    {
        var src = new FlightSource(() => null, 100, Stations);
        Assert.Equal(TimeSpan.FromSeconds(60), src.Interval);
        Assert.Equal("flights", src.Key);
    }

    /// <summary>Живая проверка: один реальный запрос к Белграду. Не входит в обычный прогон (--filter "Category!=Live").</summary>
    [Trait("Category", "Live")]
    [Fact]
    public async Task Live_BelgradeHasAircraft()
    {
        using var http = new HttpClient(new CurlHttp()) { Timeout = TimeSpan.FromSeconds(120) };
        var src = new FlightSource(() => (44.82, 20.29, "Белград"), 100, Array.Empty<FlightSource.Station>());

        var snap = Assert.IsType<FlightSnapshot>(await src.FetchAsync(http, CancellationToken.None));
        _out.WriteLine($"adsb.lol live: Белград, самолётов {snap.Aircraft.Count}");
        Assert.NotEmpty(snap.Aircraft);
    }

    private static string StationBody(string url) => url switch
    {
        _ when url.Contains("point/60.32/24.95") => Empty,
        _ when url.Contains("point/56.92/23.97") => Empty,
        _ when url.Contains("point/44.82/20.29") => Fixture("adsb-belgrade.json"),
        _ when url.Contains("point/41.27/28.75") => Empty,
        _ => "<html>unexpected</html>",
    };

    /// <summary>Ответы по подстроке URL, которую можно менять между опросами. Для тестов логики станций.</summary>
    private sealed class ScriptedHttp : HttpMessageHandler
    {
        private readonly Func<string, string> _body;
        public List<string> Requests { get; } = new();

        public ScriptedHttp(Func<string, string> body) => _body = body;

        public HttpClient Client() => new(this);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body(url), System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }
}
