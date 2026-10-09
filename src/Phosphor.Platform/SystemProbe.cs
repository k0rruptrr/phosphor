using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Phosphor.Core;

namespace Phosphor.Platform;

/// <summary>
/// Датчики ПК для ключа "system" (SystemSnapshot): опрос раз в 500 мс в фоновом потоке, готовый снимок — в volatile-поле.
/// Без админа и без WMI в горячем пути: NtQuerySystemInformation (CPU по ядрам, как Диспетчер задач: busy = (kernel+user) − idle),
/// GlobalMemoryStatusEx (RAM), PerformanceCounter ("GPU Engine" 3D, "PhysicalDisk"), NetworkInterface (сеть).
/// Отказ любого источника — 0 или NaN, без исключений; причина пишется в лог один раз на источник.
/// </summary>
public sealed class SystemProbe : IDisposable
{
    public static readonly TimeSpan Period = TimeSpan.FromMilliseconds(500);
    private static readonly double ProcessEverySec = 5;
    private static readonly double GpuRescanEverySec = 5;
    private const int SystemProcessorPerformanceInformation = 8;
    private const int ProcessorInfoStride = 48; // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION: 5×LARGE_INTEGER + ULONG, выравнено до 48

    private sealed record Published(SystemSnapshot Snap, long Version);

    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly HashSet<string> _warned = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly int _cores = Environment.ProcessorCount;
    private readonly string _os;
    private readonly string? _gpuName;
    private readonly PerformanceCounter? _diskRead;
    private readonly PerformanceCounter? _diskWrite;
    private readonly List<PerformanceCounter> _gpu = new();

    private Thread? _thread;
    private volatile Published? _pub;
    private long _version;
    private int _disposed;

    // Состояние для дельт — только под _gate
    private long[]? _prevIdle, _prevTotal;
    private double _lastT = -1, _gpuScanAt, _procAt = double.NegativeInfinity;
    private int _processes;
    private bool _netPrimed;
    private long _prevRx, _prevTx;

    /// <param name="log">Куда писать предупреждения о недоступных счётчиках (один раз на источник). null — молча.</param>
    public SystemProbe(Action<string>? log = null)
    {
        _log = log;
        _os = Environment.OSVersion.Version.Build >= 22000 ? "WINDOWS 11" : "WINDOWS 10";
        _gpuName = Guard<string?>("имя GPU", () => ReadGpuName(), null);
        _diskRead = Guard("диск", () => new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", true), null);
        _diskWrite = Guard("диск", () => new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true), null);
    }

    /// <summary>Первый замер сразу (синхронно), дальше — фоновый поток. Повторный вызов — no-op.</summary>
    public SystemProbe Start()
    {
        lock (_gate) { if (_thread is not null || _disposed != 0) return this; }
        SampleNow();
        var th = new Thread(Loop) { IsBackground = true, Name = "SystemProbe" };
        lock (_gate) { if (_disposed != 0) return this; _thread = th; }
        th.Start();
        return this;
    }

    /// <summary>Синхронный замер (для --shot: в коротком прогоне нужен реальный интервал между двумя замерами).</summary>
    public void SampleNow()
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
            try { Sample(); }
            catch (Exception e) { Warn("замер", e); }
        }
    }

    /// <summary>Текущий снимок для DataHub.RegisterLive; null до первого замера. Дёшево: только чтение volatile-поля.</summary>
    public (DataSnapshot Snap, long Version)? TryGetLive()
    {
        var p = _pub;
        if (p is null) return null;
        return (p.Snap, p.Version);
    }

    private void Loop()
    {
        while (!_stop.Wait(Period)) SampleNow();
    }

    private void Sample()
    {
        double t = _clock.Elapsed.TotalSeconds;
        double dt = _lastT < 0 ? 0 : t - _lastT;
        _lastT = t;

        var cores = new float[_cores];
        float cpu = Guard("CPU", () => ReadCpu(cores), 0f);
        var (ramUsed, ramTotal) = Guard("RAM", () => ReadRam(), (0.0, 0.0));
        float gpu = Guard("GPU", () => ReadGpu(t), float.NaN);
        var (down, up) = Guard("сеть", () => ReadNet(dt), (0.0, 0.0));
        var (diskR, diskW) = Guard("диск", () => ReadDisk(), (0.0, 0.0));
        if (t - _procAt >= ProcessEverySec)
        {
            // Process.GetProcesses дорогой — раз в 5 с; процессы освобождаем
            _processes = Guard("процессы", CountProcesses, _processes);
            _procAt = t;
        }

        var snap = new SystemSnapshot(
            Host: Environment.MachineName,
            Os: _os,
            Uptime: TimeSpan.FromMilliseconds(Environment.TickCount64),
            CpuPct: cpu,
            CorePct: cores,
            RamUsedGb: ramUsed,
            RamTotalGb: ramTotal,
            GpuPct: gpu,
            GpuName: _gpuName,
            NetDownBps: down,
            NetUpBps: up,
            DiskReadBps: diskR,
            DiskWriteBps: diskW,
            Processes: _processes,
            At: DateTimeOffset.Now);
        _pub = new Published(snap, Interlocked.Increment(ref _version));
    }

    /// <summary>Загрузка по ядрам и общая. Первый замер — нули (нужна дельта).</summary>
    private float ReadCpu(float[] core)
    {
        var buf = new byte[_cores * ProcessorInfoStride];
        int status = Native.NtQuerySystemInformation(SystemProcessorPerformanceInformation, buf, buf.Length, out _);
        if (status != 0) throw new InvalidOperationException($"NtQuerySystemInformation: 0x{status:X8}");

        bool primed = _prevTotal is not null;
        _prevTotal ??= new long[_cores];
        _prevIdle ??= new long[_cores];
        long dBusy = 0, dTotal = 0;
        for (int i = 0; i < _cores; i++)
        {
            long idle = BitConverter.ToInt64(buf, i * ProcessorInfoStride);
            long kernel = BitConverter.ToInt64(buf, i * ProcessorInfoStride + 8);
            long user = BitConverter.ToInt64(buf, i * ProcessorInfoStride + 16);
            long total = kernel + user; // kernel уже включает idle
            if (primed)
            {
                long dT = total - _prevTotal[i], dI = idle - _prevIdle[i];
                if (dT > 0)
                {
                    core[i] = (float)Math.Clamp(100.0 * (dT - dI) / dT, 0, 100);
                    dTotal += dT;
                    dBusy += dT - dI;
                }
            }
            _prevTotal[i] = total;
            _prevIdle[i] = idle;
        }
        return dTotal > 0 ? (float)Math.Clamp(100.0 * dBusy / dTotal, 0, 100) : 0f;
    }

    /// <summary>Занято = всего − доступно (как «В использовании» в Диспетчере), ГБ.</summary>
    private static (double Used, double Total) ReadRam()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref m)) throw new InvalidOperationException($"GlobalMemoryStatusEx: {Marshal.GetLastWin32Error()}");
        const double gb = 1024.0 * 1024 * 1024;
        return ((m.ullTotalPhys - m.ullAvailPhys) / gb, m.ullTotalPhys / gb);
    }

    /// <summary>Сумма 3D-движков по всем процессам (как Диспетчер), 0..100. Список экземпляров обновляем раз в 5 с.</summary>
    private float ReadGpu(double t)
    {
        if (t >= _gpuScanAt)
        {
            RescanGpu();
            _gpuScanAt = t + GpuRescanEverySec;
        }
        if (_gpu.Count == 0) return float.NaN;
        double sum = 0;
        foreach (var c in _gpu)
        {
            // Экземпляр мог исчезнуть между сканами — пропускаем, на следующем скане его не будет
            try { sum += c.NextValue(); }
            catch (InvalidOperationException) { }
        }
        return (float)Math.Clamp(sum, 0, 100);
    }

    private void RescanGpu()
    {
        foreach (var c in _gpu) c.Dispose();
        _gpu.Clear();
        string[] names;
        try { names = new PerformanceCounterCategory("GPU Engine").GetInstanceNames(); }
        catch (Exception e) { Warn("GPU", e); return; }
        foreach (var n in names)
        {
            if (!n.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
            try { _gpu.Add(new PerformanceCounter("GPU Engine", "Utilization Percentage", n, true)); }
            catch (Exception e) { Warn("GPU", e); }
        }
    }

    /// <summary>Суммарные байты «up»-интерфейсов (без loopback) → байт/с. Первый замер — нули.</summary>
    private (double Down, double Up) ReadNet(double dt)
    {
        long rx = 0, tx = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                var st = ni.GetIPv4Statistics();
                rx += st.BytesReceived;
                tx += st.BytesSent;
            }
            catch (Exception) { /* у интерфейса нет IPv4-статистики — пропускаем */ }
        }
        bool primed = _netPrimed;
        _netPrimed = true;
        long pRx = _prevRx, pTx = _prevTx;
        _prevRx = rx; _prevTx = tx;
        if (!primed || dt <= 0) return (0, 0);
        // Интерфейс мог пропасть — разность может стать отрицательной
        return (Math.Max(0, rx - pRx) / dt, Math.Max(0, tx - pTx) / dt);
    }

    private (double Read, double Write) ReadDisk()
    {
        double r = _diskRead?.NextValue() ?? 0;
        double w = _diskWrite?.NextValue() ?? 0;
        return (r, w);
    }

    private static int CountProcesses()
    {
        var ps = Process.GetProcesses();
        foreach (var p in ps) p.Dispose();
        return ps.Length;
    }

    /// <summary>
    /// Видеокарта из реестра (один раз при старте). Предпочтение: адаптер с видеопамятью > 0 и без признаков
    /// виртуального/базового драйвера (иначе попадают «Meta Virtual Monitor», «Basic Display» и т.п.).
    /// </summary>
    private static string? ReadGpuName()
    {
        using var video = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video");
        if (video is null) return null;
        string? anyName = null;
        foreach (var guid in video.GetSubKeyNames())
        {
            using var dev = video.OpenSubKey(guid + @"\0000");
            if (dev?.GetValue("DriverDesc") is not string desc || string.IsNullOrWhiteSpace(desc)) continue;
            desc = desc.Trim();
            bool virtualOrBasic = desc.Contains("Basic", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Remote", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase);
            if (virtualOrBasic) continue;
            long vram = dev.GetValue("HardwareInformation.qwMemorySize") is long m ? m
                : dev.GetValue("HardwareInformation.qwMemorySize") is byte[] b && b.Length >= 8 ? BitConverter.ToInt64(b, 0) : 0;
            if (vram > 0) return desc;
            anyName ??= desc;
        }
        return anyName;
    }

    private T Guard<T>(string source, Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception e) { Warn(source, e); return fallback; }
    }

    private void Warn(string source, Exception e)
    {
        if (!_warned.Add(source)) return;
        _log?.Invoke($"датчики: {source} недоступен ({e.GetType().Name}: {e.Message}), значения 0/NaN");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Set();
        _thread?.Join(2000);
        lock (_gate)
        {
            _diskRead?.Dispose();
            _diskWrite?.Dispose();
            foreach (var c in _gpu) c.Dispose();
            _gpu.Clear();
        }
    }

    private static class Native
    {
        [DllImport("ntdll.dll")]
        internal static extern int NtQuerySystemInformation(int infoClass, byte[] info, int length, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

        [StructLayout(LayoutKind.Sequential)]
        internal struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }
    }
}
