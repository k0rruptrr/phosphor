using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Phosphor.Core;

namespace Phosphor.Data.Tests;

/// <summary>DataHub: кеш на диске, статус последней попытки и классификация ошибок. Без сети (временная папка кеша).</summary>
public sealed class DataHubWeatherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phosphor-w5-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    /// <summary>Источник, который всегда падает с заданным исключением.</summary>
    private sealed class FailingSource : IDataSource
    {
        private readonly Exception _error;
        public FailingSource(Exception error) => _error = error;
        public string Key => "weather";
        public string Name => "weather";
        public TimeSpan Interval => TimeSpan.FromHours(1);
        public Task<DataSnapshot?> FetchAsync(HttpClient http, CancellationToken ct) => throw _error;
    }

    /// <summary>Источник, который отдаёт готовый снимок.</summary>
    private sealed class FixedSource : IDataSource
    {
        private readonly DataSnapshot _snap;
        public FixedSource(DataSnapshot snap) => _snap = snap;
        public string Key => "weather";
        public string Name => "weather";
        public TimeSpan Interval => TimeSpan.FromHours(1);
        public Task<DataSnapshot?> FetchAsync(HttpClient http, CancellationToken ct) => Task.FromResult<DataSnapshot?>(_snap);
    }

    private static bool WaitUntil(Func<bool> cond, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (cond()) return true;
            Thread.Sleep(20);
        }
        return cond();
    }

    [Fact]
    public void FetchError_ReachesStatus()
    {
        var error = new HttpRequestException("сеть", new SocketException(10013));
        using var hub = new DataHub(new IDataSource[] { new FailingSource(error) }, online: true, _ => { }, cacheDir: _dir);
        hub.Start();

        Assert.True(WaitUntil(() => hub.TryGet("weather.status", out _, out _)));
        hub.TryGet("weather.status", out var st, out _);
        Assert.Equal("BLOCKED", Assert.IsType<SourceStatusSnapshot>(st).Kind);
        Assert.False(hub.TryGet("weather", out _, out _));
    }

    [Fact]
    public void Cache_RoundTrip_KeepsNaNAndQuery()
    {
        var snap = new WeatherSnapshot("Москва", 55.75, 37.62, new WeatherNow(double.NaN, 5, 45, 3),
            new[] { new WeatherDay(new DateOnly(2026, 10, 9), 3.2, double.NaN, 0, -1) },
            DateTimeOffset.Now, false, "met.no", "Москва");

        using (var online = new DataHub(new IDataSource[] { new FixedSource(snap) }, online: true, _ => { }, cacheDir: _dir))
        {
            online.Start();
            Assert.True(WaitUntil(() => online.TryGet("weather", out _, out _)));
        } // Dispose дожидается цикла, в котором кеш записывается

        using var offline = new DataHub(new IDataSource[] { new WeatherSource("Москва") }, online: false, _ => { }, cacheDir: _dir);
        offline.Start();

        Assert.True(offline.TryGet("weather", out var cached, out _));
        var w = Assert.IsType<WeatherSnapshot>(cached);
        Assert.True(w.FromCache);
        Assert.Equal("met.no", w.Source);
        Assert.Equal("Москва", w.Query);
        Assert.True(double.IsNaN(w.Now.TempC));
        Assert.Equal(-1, w.Days[0].Code);

        // Сеть выключена в конфиге — статус OFF, а не вечное «ожидание»
        Assert.True(offline.TryGet("weather.status", out var st, out _));
        Assert.Equal("OFF", Assert.IsType<SourceStatusSnapshot>(st).Kind);
    }

    [Fact]
    public void OldCacheWithoutSourceAndQuery_StillLoads()
    {
        Directory.CreateDirectory(_dir);
        const string json = """
        WeatherSnapshot
        {"City":"Москва","Lat":55.75,"Lon":37.62,"Now":{"TempC":7.4,"WindKmh":5.4,"WindDirDeg":45,"Code":3},
         "Days":[{"Date":"2026-10-09","MinC":3.2,"MaxC":12.4,"PrecipMm":0,"Code":3}],
         "FetchedAt":"2026-10-09T04:15:00+03:00","FromCache":false}
        """;
        File.WriteAllText(Path.Combine(_dir, "weather.json"), json);

        using var hub = new DataHub(new IDataSource[] { new WeatherSource("Москва") }, online: false, _ => { }, cacheDir: _dir);
        hub.Start();

        Assert.True(hub.TryGet("weather", out var snap, out _));
        var w = Assert.IsType<WeatherSnapshot>(snap);
        Assert.Equal("open-meteo", w.Source);
        Assert.Null(w.Query);
        Assert.True(w.FromCache);
        Assert.Equal(7.4, w.Now.TempC);
    }

    [Fact]
    public void CacheDir_FromEnvironment_WhenNoExplicitDir()
    {
        var previous = Environment.GetEnvironmentVariable("PHOSPHOR_CACHE_DIR");
        try
        {
            Environment.SetEnvironmentVariable("PHOSPHOR_CACHE_DIR", _dir);
            using var hub = new DataHub(Array.Empty<IDataSource>(), online: false, _ => { });
            Assert.Equal(_dir, hub.CacheDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PHOSPHOR_CACHE_DIR", previous);
        }
    }

    [Fact]
    public void Classify_AccessDenied_IsBlocked()
    {
        var (kind, _) = FetchErrors.Classify(new HttpRequestException("x", new SocketException(10013)));
        Assert.Equal("BLOCKED", kind);
    }

    [Fact]
    public void Classify_Timeout()
    {
        var (kind, _) = FetchErrors.Classify(new TaskCanceledException("t", new TimeoutException()));
        Assert.Equal("TIMEOUT", kind);
    }

    [Fact]
    public void Classify_HostNotFound_IsDns()
    {
        var (kind, _) = FetchErrors.Classify(new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound)));
        Assert.Equal("DNS", kind);
    }

    [Fact]
    public void Classify_HttpStatus()
    {
        var (kind, message) = FetchErrors.Classify(new HttpRequestException("x", null, HttpStatusCode.NotFound));
        Assert.Equal("HTTP", kind);
        Assert.Equal("HTTP 404", message);
    }
}
