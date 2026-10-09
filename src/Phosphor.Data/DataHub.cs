using System.Collections.Concurrent;
using System.Text.Json;
using Phosphor.Core;

namespace Phosphor.Data;

/// <summary>
/// Фоновая доставка данных. Источники опрашиваются в своём потоке; последний снимок по каждому ключу
/// лежит в памяти и в кеше на диске (%LOCALAPPDATA%\Phosphor\cache). Главный поток только читает Latest.
/// Без сети (или Network.Enabled=false) — снимки из кеша с FromCache=true.
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
        CacheDir = cacheDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Phosphor", "cache");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PHOSPHOR-screensaver/0.1 (ascii screensaver; personal use)");
    }

    /// <summary>Загрузить кеш и (если онлайн) запустить фоновый опрос.</summary>
    public void Start()
    {
        foreach (var s in _sources) LoadCache(s);
        if (_online) _loop = Task.Run(() => LoopAsync(_cts.Token));
        else _log("data: сеть выключена в конфиге, только кеш");
    }

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
                    next[s] = DateTime.UtcNow + Interval(s);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception e)
                {
                    fails[s]++;
                    var delay = TimeSpan.FromSeconds(Math.Min(Interval(s).TotalSeconds, 30 * Math.Pow(2, fails[s] - 1)));
                    _log($"data: {s.Name} ошибка ({e.GetType().Name}: {e.Message}), повтор через {delay.TotalSeconds:F0} с");
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
        var merged = sameKey.Count == 1 ? snap : _merge(s.Key, sameKey);
        if (merged is null) return;
        _latest[s.Key] = (merged, Interlocked.Increment(ref _version));
    }

    // Кеш — JSON со служебным полем типа. Битый кеш молча игнорируется.
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

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
