using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Phosphor.Core;

namespace Phosphor.Data;

/// <summary>Источник, которому хаб отдаёт снимок из кеша до первого запроса (например, координаты погоды).</summary>
public interface IRestorable
{
    void Restore(DataSnapshot cached);
}

/// <summary>Классификация ошибки загрузки для статуса "&lt;key&gt;.status" (см. SourceStatusSnapshot).</summary>
public static class FetchErrors
{
    /// <summary>Вид ошибки и короткое пояснение. Вид — см. SourceStatusSnapshot.</summary>
    public static (string Kind, string Message) Classify(Exception e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
        {
            if (x is SocketException se)
            {
                if (se.SocketErrorCode == SocketError.AccessDenied) return ("BLOCKED", "WSAEACCES " + se.ErrorCode);
                if (se.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain) return ("DNS", se.Message);
            }
        }
        for (Exception? x = e; x is not null; x = x.InnerException)
        {
            if (x is TimeoutException or TaskCanceledException) return ("TIMEOUT", e.Message);
        }
        if (e is HttpRequestException hre && hre.StatusCode is { } code) return ("HTTP", $"HTTP {(int)code}");
        if (e is HttpRequestException) return ("NET", e.Message);
        if (e is System.Text.Json.JsonException) return ("PARSE", e.Message);
        if (e is InvalidOperationException) return ("NODATA", e.Message);
        return ("ERROR", e.Message);
    }
}

/// <summary>
/// Фоновая доставка данных. Источники опрашиваются в своём потоке; последний снимок по каждому ключу
/// лежит в памяти и в кеше на диске (%LOCALAPPDATA%\Phosphor\cache). Главный поток только читает Latest.
/// Без сети (или Network.Enabled=false) — снимки из кеша с FromCache=true.
/// Статус последней попытки каждого источника — снимок SourceStatusSnapshot под ключом "&lt;key&gt;.status".
/// </summary>
public sealed class DataHub : IDisposable
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);

    private readonly List<IDataSource> _sources;
    private readonly bool _online;
    private readonly Action<string> _log;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, (DataSnapshot Snap, long Version)> _latest = new();
    private readonly ConcurrentDictionary<string, DataSnapshot> _perSource = new();
    private readonly Func<string, IReadOnlyList<DataSnapshot>, DataSnapshot?> _merge;
    private long _version;
    private Task? _loop;

    public string CacheDir { get; }

    /// <param name="merge">Как объединить снимки нескольких источников одного ключа (например, новости). null — берётся последний.</param>
    public DataHub(IEnumerable<IDataSource> sources, bool online, Action<string> log,
        Func<string, IReadOnlyList<DataSnapshot>, DataSnapshot?>? merge = null, string? cacheDir = null)
    {
        _sources = sources.ToList();
        _online = online;
        _log = log;
        _merge = merge ?? ((_, list) => list.MaxBy(s => s.FetchedAt));
        CacheDir = cacheDir
            ?? (Environment.GetEnvironmentVariable("PHOSPHOR_CACHE_DIR") is { Length: > 0 } env
                ? env
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Phosphor", "cache"));
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PHOSPHOR-screensaver/0.1 (ascii screensaver; personal use)");
    }

    private readonly ConcurrentDictionary<string, Func<(DataSnapshot Snap, long Version)?>> _live = new();

    /// <summary>
    /// Живой ключ без сети и кеша ("system", "phosphor"): хост сам держит свежий снимок, хаб просто отдаёт его сценам.
    /// provider вызывается из главного потока на каждом TryGet — должен быть дешёвым (вернуть готовое); Version растёт при обновлении.
    /// </summary>
    public void RegisterLive(string key, Func<(DataSnapshot Snap, long Version)?> provider) => _live[key] = provider;

    /// <summary>Загрузить кеш и (если онлайн) запустить фоновый опрос.</summary>
    public void Start()
    {
        foreach (var s in _sources) LoadCache(s);
        if (_online) _loop = Task.Run(() => LoopAsync(_cts.Token));
        else
        {
            _log("data: сеть выключена в конфиге, только кеш");
            foreach (var s in _sources) SetStatus(s, "OFF", "сеть выключена в конфиге");
        }
    }

    private void SetStatus(IDataSource s, string? kind, string? message) =>
        _latest[s.Key + ".status"] = (new SourceStatusSnapshot(kind, message, DateTimeOffset.Now), Interlocked.Increment(ref _version));

    /// <summary>Последний снимок по ключу и его версия (растёт при каждом обновлении).</summary>
    public bool TryGet(string key, out DataSnapshot snapshot, out long version)
    {
        if (key == "clock")
        {
            // Часы — не источник: всегда свежие, версия = номер секунды.
            var now = DateTime.Now;
            snapshot = new ClockSnapshot(now);
            version = now.Ticks / TimeSpan.TicksPerSecond;
            return true;
        }
        if (_latest.TryGetValue(key, out var v)) { snapshot = v.Snap; version = v.Version; return true; }
        if (_live.TryGetValue(key, out var live) && live() is { } ls) { snapshot = ls.Snap; version = ls.Version; return true; }
        snapshot = null!; version = 0;
        return false;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var next = _sources.ToDictionary(s => s, _ => DateTime.UtcNow);
        var fails = _sources.ToDictionary(s => s, _ => 0);
        // Источники с хорошим кешем не дёргаем сразу
        foreach (var s in _sources)
            if (_perSource.TryGetValue(s.Name, out var c) && DateTimeOffset.Now - c.FetchedAt < Interval(s) / 2)
                next[s] = DateTime.UtcNow + (Interval(s) / 2 - (DateTimeOffset.Now - c.FetchedAt));

        while (!ct.IsCancellationRequested)
        {
            foreach (var s in _sources)
            {
                if (DateTime.UtcNow < next[s] || ct.IsCancellationRequested) continue;
                try
                {
                    var snap = await s.FetchAsync(_http, ct).ConfigureAwait(false);
                    if (snap is not null)
                    {
                        Publish(s, snap);
                        SaveCache(s, snap);
                    }
                    fails[s] = 0;
                    SetStatus(s, null, null);
                    next[s] = DateTime.UtcNow + Interval(s);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception e)
                {
                    fails[s]++;
                    var delay = TimeSpan.FromSeconds(Math.Min(Interval(s).TotalSeconds, 30 * Math.Pow(2, fails[s] - 1)));
                    _log($"data: {s.Name} ошибка ({e.GetType().Name}: {e.Message}), повтор через {delay.TotalSeconds:F0} с");
                    var (kind, message) = FetchErrors.Classify(e);
                    SetStatus(s, kind, message);
                    next[s] = DateTime.UtcNow + delay;
                }
            }
            try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    private static TimeSpan Interval(IDataSource s) => s.Interval < MinInterval ? MinInterval : s.Interval;

    private void Publish(IDataSource s, DataSnapshot snap)
    {
        _perSource[s.Name] = snap;
        var sameKey = _sources.Where(x => x.Key == s.Key && _perSource.ContainsKey(x.Name)).Select(x => _perSource[x.Name]).ToList();
        // Ключи, где merge собирает Set-снимок (satellite, sattv), всегда идут через merge — даже с одним источником:
        // сцена получает SatelliteSetSnapshot / SatTvSetSnapshot, а не сырой снимок.
        var merged = sameKey.Count == 1 && snap is not SatelliteFeed and not SatTvSatellite ? snap : _merge(s.Key, sameKey);
        if (merged is null) return;
        _latest[s.Key] = (merged, Interlocked.Increment(ref _version));
    }

    // Кеш — JSON со служебным полем типа. Битый кеш молча игнорируется.
    // NaN — пропущенное значение источника (см. WeatherSnapshot): в JSON пишем и читаем как "NaN".
    private static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private string CachePath(IDataSource s) => Path.Combine(CacheDir, s.Name + ".json");

    private void SaveCache(IDataSource s, DataSnapshot snap)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var tmp = CachePath(s) + ".tmp";
            File.WriteAllText(tmp, snap.GetType().Name + "\n" + JsonSerializer.Serialize(snap, snap.GetType(), Json));
            File.Move(tmp, CachePath(s), overwrite: true);
        }
        catch (Exception e) { _log($"data: кеш {s.Name} не записан: {e.Message}"); }
    }

    private void LoadCache(IDataSource s)
    {
        try
        {
            var path = CachePath(s);
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path);
            int nl = text.IndexOf('\n');
            var type = typeof(DataSnapshot).Assembly.GetType("Phosphor.Core." + text[..nl]);
            if (type is null || !typeof(DataSnapshot).IsAssignableFrom(type)) return;
            if (JsonSerializer.Deserialize(text[(nl + 1)..], type, Json) is not DataSnapshot snap) return;
            Publish(s, snap with { FromCache = true });
            if (s is IRestorable r) r.Restore(snap);
            _log($"data: {s.Name} из кеша от {snap.FetchedAt:dd.MM HH:mm}");
        }
        catch (Exception e) { _log($"data: кеш {s.Name} битый, пропускаю: {e.Message}"); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch { /* выход */ }
        _http.Dispose();
        _cts.Dispose();
    }
}
