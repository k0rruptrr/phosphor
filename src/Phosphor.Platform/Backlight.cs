using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phosphor.Platform;

/// <summary>Монитор с точки зрения подсветки. DeviceName — как MonitorRect.Name (\\.\DISPLAY1).</summary>
public sealed record BacklightInfo(string DeviceName, bool Hardware, int Min, int Max, int Original, string? Reason);

/// <summary>Низкоуровневый порт DDC — для подмены в тестах.</summary>
public interface IDdcPort : IDisposable
{
    /// <summary>Мониторы с DDC-хендлами. Монитор без физических хендлов тоже возвращается (Handle-ов нет).</summary>
    IReadOnlyList<string> Devices();
    /// <summary>Чтение яркости (VCP 0x10). false — монитор не ответил.</summary>
    bool TryGet(string device, out int min, out int cur, out int max);
    bool TrySet(string device, int value);
}

/// <summary>Подсветка мониторов через DDC/CI: диммер заставки и восстановление исходной яркости.
/// Все обращения к порту — из одного фонового потока; чужие изменения яркости (Twinkle Tray) не затираем.</summary>
public sealed class Backlight : IDisposable
{
    private const int Attempts = 3;
    private const int RetryPauseMs = 50;
    private const long WriteIntervalMs = 500;
    private const int TickMs = 20;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IDdcPort _port;
    private readonly string _journalPath;
    private readonly Action<string> _log;
    private readonly Func<long> _clock;

    // Под _sync: желаемая яркость (0..1) по устройствам и заявки на RestoreAll.
    private readonly object _sync = new();
    private readonly Dictionary<string, float> _desired = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ManualResetEventSlim> _restoreRequests = new();

    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;
    private volatile bool _ready;
    private volatile IReadOnlyList<BacklightInfo> _monitors = Array.Empty<BacklightInfo>();
    private bool _disposed;

    // Состояние устройств — только из фонового потока.
    private readonly Dictionary<string, DevState> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Реальный порт (Dxva2) + фоновый поток. Сначала RecoverFromJournal, затем опрос мониторов в фоне.</summary>
    public static Backlight Start(string journalPath, Action<string> log)
    {
        var port = new Dxva2Port(log);
        try
        {
            RecoverFromJournal(journalPath, port, log);
        }
        catch (Exception ex)
        {
            log($"Backlight: восстановление из журнала упало: {ex.Message}");
        }
        return new Backlight(port, journalPath, log, () => Environment.TickCount64);
    }

    /// <summary>Для тестов: свой порт и свои часы (мс).</summary>
    public Backlight(IDdcPort port, string journalPath, Action<string> log, Func<long> clockMs)
    {
        _port = port;
        _journalPath = journalPath;
        _log = log;
        _clock = clockMs;
        _thread = new Thread(Run) { IsBackground = true, Name = "Backlight" };
        _thread.Start();
    }

    /// <summary>true, когда опрос мониторов закончен (до этого Monitors пуст).</summary>
    public bool Ready => _ready;

    public IReadOnlyList<BacklightInfo> Monitors => _monitors;

    public bool IsHardware(string device)
    {
        foreach (var m in _monitors)
            if (string.Equals(m.DeviceName, device, StringComparison.OrdinalIgnoreCase))
                return m.Hardware;
        return false;
    }

    /// <summary>Желаемая яркость монитора как доля диапазона [Min..Max], 0..1. Неблокирующий: последняя заявка побеждает;
    /// запись в монитор — в фоновом потоке, не чаще раза в 500 мс на монитор, только если целое значение изменилось.</summary>
    public void Request(string device, float level01)
    {
        if (float.IsNaN(level01)) return;
        float v = Math.Clamp(level01, 0f, 1f);
        // Без пробуждения потока: фоновый опрос каждые TickMs сам подхватит последнюю заявку (объединяет серию Request).
        lock (_sync) _desired[device] = v;
    }

    /// <summary>Вернуть исходную яркость (синхронно, не дольше timeout). Для каждого монитора, который мы меняли:
    /// прочитать текущую; если равна последней записанной нами — записать Original; иначе (кто-то поменял — Twinkle Tray,
    /// пользователь) — не трогать, записать в лог. После — удалить журнал.</summary>
    public void RestoreAll(TimeSpan timeout)
    {
        if (!_thread.IsAlive || Thread.CurrentThread == _thread)
        {
            DoRestore();
            return;
        }
        var done = new ManualResetEventSlim(false);
        lock (_sync) _restoreRequests.Add(done);
        _wake.Set();
        if (!done.Wait(timeout))
            _log("Backlight: восстановление не уложилось в таймаут, выполнится в фоне");
    }

    /// <summary>Восстановление после падения: журнал есть — то же правило, что в RestoreAll, затем удалить журнал.</summary>
    public static void RecoverFromJournal(string journalPath, IDdcPort port, Action<string> log)
    {
        if (!File.Exists(journalPath)) return;

        JournalFile? journal = null;
        try
        {
            journal = JsonSerializer.Deserialize<JournalFile>(File.ReadAllText(journalPath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log($"Backlight: журнал повреждён, пропускаю: {ex.Message}");
        }

        if (journal?.Monitors != null)
        {
            log($"Backlight: найден журнал после падения (pid {journal.Pid}), восстанавливаю яркость");
            foreach (var e in journal.Monitors)
                RestoreEntry(port, log, e.Device, e.Original, e.LastWritten);
        }
        DeleteFile(journalPath, log);
    }

    /// <summary>RestoreAll(2 с) + остановка потока + закрытие порта.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        RestoreAll(TimeSpan.FromSeconds(2));
        _stop = true;
        _wake.Set();
        if (!_thread.Join(TimeSpan.FromSeconds(3)))
        {
            _log("Backlight: фоновый поток не остановился, порт не закрываю");
            return;
        }
        _port.Dispose();
        _wake.Dispose();
    }

    // ---------- фоновый поток ----------

    private void Run()
    {
        try
        {
            Poll();
        }
        catch (Exception ex)
        {
            _log($"Backlight: опрос мониторов упал: {ex.Message}");
        }
        finally
        {
            _ready = true;
        }

        while (!_stop)
        {
            try
            {
                ServiceRestore();
                Tick();
            }
            catch (Exception ex)
            {
                _log($"Backlight: ошибка в фоновом потоке: {ex.Message}");
            }
            _wake.WaitOne(TickMs);
        }

        // Заявки, пришедшие во время остановки, выполняем здесь же.
        try
        {
            ServiceRestore();
        }
        catch (Exception ex)
        {
            _log($"Backlight: восстановление при остановке упало: {ex.Message}");
        }
    }

    private void Poll()
    {
        var list = new List<BacklightInfo>();
        foreach (string dev in _port.Devices())
        {
            BacklightInfo info;
            if (!TryGetRetry(_port, dev, out int min, out int cur, out int max))
            {
                info = new BacklightInfo(dev, false, 0, 0, 0, "DDC не отвечает");
            }
            else if (max <= min)
            {
                info = new BacklightInfo(dev, false, min, max, cur, "нет диапазона яркости (Max<=Min)");
            }
            else
            {
                info = new BacklightInfo(dev, true, min, max, cur, null);
            }

            _states[dev] = new DevState(info);
            list.Add(info);
            _log($"Backlight: {dev} hardware={info.Hardware} min={info.Min} cur={cur} max={info.Max}"
                 + (info.Reason != null ? $" ({info.Reason})" : ""));
        }
        _monitors = list;
    }

    /// <summary>Выполнить заявку RestoreAll, если она есть.</summary>
    private void ServiceRestore()
    {
        List<ManualResetEventSlim> pending;
        lock (_sync)
        {
            if (_restoreRequests.Count == 0) return;
            pending = new List<ManualResetEventSlim>(_restoreRequests);
            _restoreRequests.Clear();
        }
        try
        {
            DoRestore();
        }
        finally
        {
            foreach (var done in pending) done.Set();
        }
    }

    private void Tick()
    {
        long now = _clock();
        foreach (var s in _states.Values)
        {
            if (!s.Info.Hardware) continue;

            float level;
            lock (_sync)
            {
                if (!_desired.TryGetValue(s.Info.DeviceName, out level)) continue;
            }

            int target = ToValue(s.Info, level);
            if (s.LastWritten == target) continue;
            if (s.LastWriteAt is long last && now - last < WriteIntervalMs) continue;
            WriteTo(s, target, now);
        }
    }

    private void WriteTo(DevState s, int target, long now)
    {
        s.LastWriteAt = now;
        try
        {
            // Журнал пишется ДО первой записи в монитор.
            if (!s.Journaled)
            {
                WriteJournalFile(BuildJournal(s));
                s.Journaled = true;
            }
        }
        catch (Exception ex)
        {
            _log($"Backlight: не записал журнал, яркость {s.Info.DeviceName} не меняю: {ex.Message}");
            return;
        }

        if (!TrySetRetry(_port, s.Info.DeviceName, target))
        {
            _log($"Backlight: {s.Info.DeviceName} не принял яркость {target}");
            return;
        }

        s.LastWritten = target;
        try
        {
            WriteJournalFile(BuildJournal(null));
        }
        catch (Exception ex)
        {
            _log($"Backlight: не обновил журнал: {ex.Message}");
        }
    }

    /// <summary>Восстановление исходной яркости (только из фонового потока или при остановленном потоке).</summary>
    private void DoRestore()
    {
        foreach (var s in _states.Values)
        {
            if (!s.Journaled) continue;
            string dev = s.Info.DeviceName;
            int expected = s.LastWritten ?? s.Info.Original;

            if (!TryGetRetry(_port, dev, out _, out int cur, out _))
            {
                _log($"Backlight: {dev} не ответил, яркость не восстановлена (оригинал {s.Info.Original})");
            }
            else if (cur == expected)
            {
                bool ok = TrySetRetry(_port, dev, s.Info.Original);
                _log(ok
                    ? $"Backlight: {dev} восстановлена яркость {s.Info.Original}"
                    : $"Backlight: {dev} не удалось восстановить яркость {s.Info.Original}");
            }
            else
            {
                _log($"Backlight: {dev} яркость изменена извне (сейчас {cur}, мы писали {expected}), не трогаю");
            }

            s.Journaled = false;
            s.LastWritten = null;
            s.LastWriteAt = null;
        }

        lock (_sync) _desired.Clear();
        DeleteFile(_journalPath, _log);
    }

    private JournalFile BuildJournal(DevState? extra)
    {
        var j = new JournalFile { Pid = Environment.ProcessId };
        foreach (var s in _states.Values)
        {
            if (!s.Journaled && !ReferenceEquals(s, extra)) continue;
            j.Monitors.Add(new JournalEntry
            {
                Device = s.Info.DeviceName,
                Original = s.Info.Original,
                LastWritten = s.LastWritten ?? s.Info.Original,
            });
        }
        return j;
    }

    private void WriteJournalFile(JournalFile j)
    {
        string full = Path.GetFullPath(_journalPath);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = full + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(j, JsonOptions));
        File.Move(tmp, full, overwrite: true);
    }

    // ---------- общие правила ----------

    private static int ToValue(BacklightInfo info, float level01)
    {
        int v = info.Min + (int)Math.Round(level01 * (info.Max - info.Min), MidpointRounding.AwayFromZero);
        return Math.Clamp(v, info.Min, info.Max);
    }

    private static void RestoreEntry(IDdcPort port, Action<string> log, string device, int original, int lastWritten)
    {
        if (!TryGetRetry(port, device, out _, out int cur, out _))
        {
            log($"Backlight: {device} не ответил, яркость не восстановлена (оригинал {original})");
            return;
        }
        if (cur != lastWritten)
        {
            log($"Backlight: {device} яркость изменена извне (сейчас {cur}, мы писали {lastWritten}), не трогаю");
            return;
        }
        bool ok = TrySetRetry(port, device, original);
        log(ok
            ? $"Backlight: {device} восстановлена яркость {original}"
            : $"Backlight: {device} не удалось восстановить яркость {original}");
    }

    private static bool TryGetRetry(IDdcPort port, string device, out int min, out int cur, out int max)
    {
        for (int i = 0; i < Attempts; i++)
        {
            if (port.TryGet(device, out min, out cur, out max)) return true;
            if (i < Attempts - 1) Thread.Sleep(RetryPauseMs);
        }
        min = cur = max = 0;
        return false;
    }

    private static bool TrySetRetry(IDdcPort port, string device, int value)
    {
        for (int i = 0; i < Attempts; i++)
        {
            if (port.TrySet(device, value)) return true;
            if (i < Attempts - 1) Thread.Sleep(RetryPauseMs);
        }
        return false;
    }

    private static void DeleteFile(string path, Action<string> log)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Backlight: не удалось удалить журнал: {ex.Message}");
        }
    }

    private sealed class DevState
    {
        public DevState(BacklightInfo info) => Info = info;

        public BacklightInfo Info { get; }
        public bool Journaled;
        public int? LastWritten;
        public long? LastWriteAt;
    }

    internal sealed class JournalFile
    {
        [JsonPropertyName("pid")] public int Pid { get; set; }
        [JsonPropertyName("monitors")] public List<JournalEntry> Monitors { get; set; } = new();
    }

    internal sealed class JournalEntry
    {
        [JsonPropertyName("device")] public string Device { get; set; } = "";
        [JsonPropertyName("original")] public int Original { get; set; }
        [JsonPropertyName("lastWritten")] public int LastWritten { get; set; }
    }
}
