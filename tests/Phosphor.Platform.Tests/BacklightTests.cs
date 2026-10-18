using System.Diagnostics;
using System.Text.Json;
using Phosphor.Platform;
using Xunit;
using Xunit.Abstractions;

namespace Phosphor.Platform.Tests;

/// <summary>Фейковый DDC-порт: журнал записей с временем фейковых часов, настраиваемые отказы, имитация чужих изменений.</summary>
internal sealed class FakeDdc : IDdcPort
{
    public sealed class Mon
    {
        public int Min, Cur, Max;
        public bool Responds = true;
        public int FailGets;   // сколько ближайших чтений провалить
        public int FailSets;   // сколько ближайших записей провалить
    }

    public readonly record struct SetRecord(string Device, int Value, long Time);

    private readonly object _gate = new();
    private readonly Func<long> _clock;
    public readonly Dictionary<string, Mon> Mons = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SetRecord> _sets = new();
    /// <summary>Вызывается под замком перед каждой записью (для проверки журнала).</summary>
    public Action<string, int>? OnSet;

    public FakeDdc(Func<long> clock) => _clock = clock;

    public List<SetRecord> SetsSnapshot()
    {
        lock (_gate) return new List<SetRecord>(_sets);
    }

    public void SetCurrent(string device, int value)
    {
        lock (_gate) Mons[device].Cur = value;
    }

    public int CurrentOf(string device)
    {
        lock (_gate) return Mons[device].Cur;
    }

    public IReadOnlyList<string> Devices()
    {
        lock (_gate) return Mons.Keys.ToList();
    }

    public bool TryGet(string device, out int min, out int cur, out int max)
    {
        lock (_gate)
        {
            min = cur = max = 0;
            if (!Mons.TryGetValue(device, out var m)) return false;
            if (m.FailGets > 0) { m.FailGets--; return false; }
            if (!m.Responds) return false;
            min = m.Min; cur = m.Cur; max = m.Max;
            return true;
        }
    }

    public bool TrySet(string device, int value)
    {
        lock (_gate)
        {
            if (!Mons.TryGetValue(device, out var m)) return false;
            OnSet?.Invoke(device, value);
            if (m.FailSets > 0) { m.FailSets--; return false; }
            if (!m.Responds) return false;
            m.Cur = value;
            _sets.Add(new SetRecord(device, value, _clock()));
            return true;
        }
    }

    public void Dispose() { }
}

internal sealed class FakeClock
{
    private long _ms;
    public long Now => Interlocked.Read(ref _ms);
    public void Advance(long delta) => Interlocked.Add(ref _ms, delta);
}

public class BacklightTests : IDisposable
{
    private const string Dev = "\\\\.\\DISPLAY1";
    private const int WaitMs = 2000;

    private readonly ITestOutputHelper _out;
    private readonly string _dir;
    private readonly string _journal;
    private readonly List<string> _log = new();
    private readonly object _logGate = new();

    public BacklightTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "phosphor-bl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _journal = Path.Combine(_dir, "backlight.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Log(string msg)
    {
        lock (_logGate) _log.Add(msg);
        _out.WriteLine(msg);
    }

    private IReadOnlyList<string> LogSnapshot()
    {
        lock (_logGate) return _log.ToList();
    }

    private static FakeDdc OneMonitor(FakeClock clock, int cur = 70)
    {
        var f = new FakeDdc(() => clock.Now);
        f.Mons[Dev] = new FakeDdc.Mon { Min = 0, Cur = cur, Max = 100 };
        return f;
    }

    /// <summary>Ждёт условие, опрашивая каждые 10 мс, не дольше timeoutMs.</summary>
    private static bool WaitUntil(Func<bool> cond, int timeoutMs = WaitMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(10);
        }
        return cond();
    }

    private Backlight Create(FakeDdc fake, FakeClock clock) =>
        new(fake, _journal, Log, () => clock.Now);

    private static int LastValue(FakeDdc fake)
    {
        var s = fake.SetsSnapshot();
        return s.Count == 0 ? -1 : s[^1].Value;
    }

    // 1. Опрос

    [Fact]
    public void Poll_MonitorResponds_IsHardware_OriginalFromCurrent()
    {
        var clock = new FakeClock();
        var fake = OneMonitor(clock, cur: 70);
        using var bl = Create(fake, clock);
        Assert.True(WaitUntil(() => bl.Ready));

        var m = Assert.Single(bl.Monitors);
        Assert.True(m.Hardware);
        Assert.Equal(0, m.Min);
        Assert.Equal(100, m.Max);
        Assert.Equal(70, m.Original);
        Assert.Null(m.Reason);
        Assert.True(bl.IsHardware(Dev));
    }

    [Fact]
    public void Poll_MonitorDoesNotRespond_NotHardware_WithReason()
    {
        var clock = new FakeClock();
        var fake = OneMonitor(clock);
        fake.Mons[Dev].Responds = false;
        using var bl = Create(fake, clock);
        Assert.True(WaitUntil(() => bl.Ready));

        var m = Assert.Single(bl.Monitors);
        Assert.False(m.Hardware);
        Assert.Contains("DDC", m.Reason);
        Assert.False(bl.IsHardware(Dev));
    }

    [Fact]
    public void Poll_RespondsOnSecondAttempt_IsHardware()
    {
        var clock = new FakeClock();
        var fake = OneMonitor(clock);
        fake.Mons[Dev].FailGets = 1;
        using var bl = Create(fake, clock);
        Assert.True(WaitUntil(() => bl.Ready));

        Assert.True(Assert.Single(bl.Monitors).Hardware);
    }

    // 2. Серия Request, троттлинг, последняя заявка побеждает. Время — только фейковые часы.

    [Fact]
    public void Request_Burst_WritesOnlyLast_ThrottledByClock_SameValueSkipped()
    {
        var clock = new FakeClock();
        var fake = OneMonitor(clock, cur: 70);
        using var bl = Create(fake, clock);
        Assert.True(WaitUntil(() => bl.Ready));

        // 1) Серия 0.1..1.0 без продвижения часов. Первое значение могло уйти до остальных — записей не больше одной.
        for (int i = 1; i <= 10; i++) bl.Request(Dev, i / 10f);
        WaitUntil(() => fake.SetsSnapshot().Count >= 1);
        Assert.True(fake.SetsSnapshot().Count <= 1);

        // 2) Часы +500 мс — последняя запись должна быть последним значением серии (1.0 -> Max = 100).
        clock.Advance(500);
        Assert.True(WaitUntil(() => LastValue(fake) == 100));

        // 3) Соседние записи разделены не меньше чем 500 мс фейковых часов.
        var sets = fake.SetsSnapshot();
        for (int i = 1; i < sets.Count; i++)
            Assert.True(sets[i].Time - sets[i - 1].Time >= 500,
                $"записи {i - 1} и {i} разделены {sets[i].Time - sets[i - 1].Time} мс");

        // 4) Тот же Request после продвижения часов — новой записи нет.
        int before = fake.SetsSnapshot().Count;
        clock.Advance(1000);
        bl.Request(Dev, 1.0f);
        Assert.False(WaitUntil(() => fake.SetsSnapshot().Count > before));
    }

    [Fact]
    public void Request_NewValueWithinThrottleWindow_WaitsForClock()
    {
        var clock = new FakeClock();
        var fake = OneMonitor(clock, cur: 70);
        using var bl = Create(fake, clock);
        Assert.True(WaitUntil(() => bl.Ready));

        bl.Request(Dev, 0.5f);
        Assert.True(WaitUntil(() => fake.SetsSnapshot().Count == 1));
        int first = fake.SetsSnapshot().Count;

        // Новое значение раньше 500 мс от последней записи — не пишется.
        clock.Advance(100);
        bl.Request(Dev, 0.2f);
        Assert.False(WaitUntil(() => fake.SetsSnapshot().Count > first));

        // Через 500 мс от последней записи — пишется.
        clock.Advance(400);
        Assert.True(WaitUntil(() => fake.SetsSnapshot().Count == 2));
        Assert.Equal(20, LastValue(fake));
    }

    // 3. Журнал до первой записи

    [Fact]
    public void Journal_ExistsBeforeFirstSet_ContainsOriginal()
    {
        var clock = new FakeClock();
        var fake = OneMonitor(clock, cur: 70);
        bool existedAtSet = false;
        string? text = null;
        fake.OnSet = (_, _) =>
        {
            existedAtSet = File.Exists(_journal);
            if (existedAtSet) text = File.ReadAllText(_journal);
        };
        using var bl = Create(fake, clock);
        Assert.True(WaitUntil(() => bl.Ready));

        bl.Request(Dev, 0f);
        Assert.True(WaitUntil(() => fake.SetsSnapshot().Count >= 1));

        Assert.True(existedAtSet);
        using var doc = JsonDocument.Parse(text!);
        var mon = doc.RootElement.GetProperty("monitors")[0];
        Assert.Equal(Dev, mon.GetProperty("device").GetString());
        Assert.Equal(70, mon.GetProperty("original").GetInt32());
    }

    // 4. RestoreAll

    [Fact]
    public void RestoreAll_CurrentEqualsLastWritten_RestoresOriginal_DeletesJournal()
    {
        var clock = new FakeClock();
        var fake = OneMonitor(clock, cur: 70);
        using var bl = Create(fake, clock);
        Assert.True(WaitUntil(() => bl.Ready));

        bl.Request(Dev, 0f);
        Assert.True(WaitUntil(() => fake.CurrentOf(Dev) == 0));

        bl.RestoreAll(TimeSpan.FromSeconds(2));

        Assert.Equal(70, fake.CurrentOf(Dev));
        Assert.False(File.Exists(_journal));
    }

    [Fact]
    public void RestoreAll_CurrentChangedExternally_DoesNotTouch_LogsIt()
    {
        var clock = new FakeClock();
        var fake = OneMonitor(clock, cur: 70);
        using var bl = Create(fake, clock);
        Assert.True(WaitUntil(() => bl.Ready));

        bl.Request(Dev, 0f);
        Assert.True(WaitUntil(() => fake.CurrentOf(Dev) == 0));

        // Имитация Twinkle Tray: пользователь поменял яркость сам.
        fake.SetCurrent(Dev, 40);
        bl.RestoreAll(TimeSpan.FromSeconds(2));

        Assert.Equal(40, fake.CurrentOf(Dev));
        Assert.DoesNotContain(fake.SetsSnapshot(), s => s.Value == 70);
        Assert.Contains(LogSnapshot(), l => l.Contains("изменена извне"));
        Assert.False(File.Exists(_journal));
    }

    // 5. RecoverFromJournal

    [Fact]
    public void RecoverFromJournal_RestoresOriginal_DeletesJournal()
    {
        var clock = new FakeClock();
        File.WriteAllText(_journal, JsonSerializer.Serialize(new
        {
            pid = 1234,
            monitors = new[] { new { device = Dev, original = 70, lastWritten = 0 } },
        }));
        var fake = OneMonitor(clock, cur: 0);

        Backlight.RecoverFromJournal(_journal, fake, Log);

        Assert.Equal(70, fake.CurrentOf(Dev));
        Assert.False(File.Exists(_journal));
    }

    [Fact]
    public void RecoverFromJournal_NoJournal_DoesNothing()
    {
        var clock = new FakeClock();
        var fake = OneMonitor(clock, cur: 33);

        Backlight.RecoverFromJournal(_journal, fake, Log);

        Assert.Empty(fake.SetsSnapshot());
        Assert.Equal(33, fake.CurrentOf(Dev));
    }

    // 6. Не-железный монитор — в порт ничего

    [Fact]
    public void Request_OnNonHardwareMonitor_WritesNothing()
    {
        var clock = new FakeClock();
        var fake = OneMonitor(clock);
        fake.Mons[Dev].Responds = false;
        using var bl = Create(fake, clock);
        Assert.True(WaitUntil(() => bl.Ready));

        bl.Request(Dev, 0f);
        bl.Request("\\\\.\\DISPLAY9", 0f);
        Assert.False(WaitUntil(() => fake.SetsSnapshot().Count > 0));

        Assert.Empty(fake.SetsSnapshot());
        Assert.False(File.Exists(_journal));
    }

    // 7. Live: только чтение реального Dxva2Port. TrySet здесь НЕ вызывается.

    [Fact]
    [Trait("Category", "Live")]
    public void Live_ReadOnly_ListsRealMonitors()
    {
        using var port = new Dxva2Port(msg => _out.WriteLine(msg));
        var devices = port.Devices();
        _out.WriteLine($"Мониторов: {devices.Count}");
        foreach (string d in devices)
        {
            bool ok = port.TryGet(d, out int min, out int cur, out int max);
            _out.WriteLine(ok
                ? $"{d}: min={min} cur={cur} max={max}"
                : $"{d}: DDC не отвечает");
        }
    }

    // «Моргнуть» (окно настроек)

    [Fact]
    public void Blink_DipsToMinimumThenRestoresOriginal()
    {
        var clock = new FakeClock();
        var fake = OneMonitor(clock, cur: 70);
        using var bl = Create(fake, clock);
        // Пока «держим», ждём записи минимума реальным временем: запись идёт из фонового потока
        string msg = bl.Blink(Dev, 0, ms => { if (ms > 0) Thread.Sleep(ms); else Assert.True(WaitUntil(() => LastValue(fake) == 0)); });
        Assert.StartsWith("моргнул", msg);
        Assert.Equal(new[] { 0, 70 }, fake.SetsSnapshot().Select(s => s.Value).ToArray());
        Assert.Equal(70, fake.CurrentOf(Dev));
        Assert.False(File.Exists(_journal));
    }

    [Fact]
    public void Blink_AtMinimum_FlashesUpInstead()
    {
        var clock = new FakeClock();
        var fake = OneMonitor(clock, cur: 10);
        using var bl = Create(fake, clock);
        bl.Blink(Dev, 0, ms => { if (ms > 0) Thread.Sleep(ms); else Assert.True(WaitUntil(() => LastValue(fake) == 100)); });
        Assert.Equal(new[] { 100, 10 }, fake.SetsSnapshot().Select(s => s.Value).ToArray());
    }

    [Fact]
    public void Blink_SilentMonitor_WritesNothing()
    {
        var clock = new FakeClock();
        var fake = OneMonitor(clock);
        fake.Mons[Dev].Responds = false;
        using var bl = Create(fake, clock);
        string msg = bl.Blink(Dev, 0, ms => Thread.Sleep(Math.Max(ms, 1)));
        Assert.Contains("не отвечает", msg);
        Assert.Empty(fake.SetsSnapshot());
    }
}
