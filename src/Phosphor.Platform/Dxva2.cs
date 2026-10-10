using System.Runtime.InteropServices;

namespace Phosphor.Platform;

/// <summary>Физический монитор из dxva2.dll (PHYSICAL_MONITOR).</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct PhysicalMonitor
{
    public nint hPhysicalMonitor;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
}

/// <summary>P/Invoke-объявления dxva2.dll: яркость и VCP-коды мониторов по DDC/CI.</summary>
internal static class Dxva2
{
    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(nint hMonitor, uint dwPhysicalMonitorArraySize,
        [Out] PhysicalMonitor[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PhysicalMonitor[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorBrightness(nint hMonitor, out uint pdwMinimumBrightness,
        out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetMonitorBrightness(nint hMonitor, uint dwNewBrightness);

    /// <summary>Запасной путь: чтение VCP-кода (pvct = null).</summary>
    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVCPFeatureAndVCPFeatureReply(nint hMonitor, byte bVCPCode, nint pvct,
        out uint pdwCurrentValue, out uint pdwMaximumValue);

    /// <summary>Запасной путь: запись VCP-кода.</summary>
    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetVCPFeature(nint hMonitor, byte bVCPCode, uint dwNewValue);
}

/// <summary>Реальный DDC-порт поверх dxva2.dll. Все обращения — из одного потока (см. Backlight).
/// Физические хендлы получаются при первом Devices(); Dispose освобождает их.</summary>
public sealed class Dxva2Port : IDdcPort
{
    private const byte VcpBrightness = 0x10;

    private readonly Action<string> _log;
    private readonly Dictionary<string, nint> _handles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _vcpMode = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PhysicalMonitor[]> _arrays = new();
    private List<string>? _devices;
    private bool _disposed;

    public Dxva2Port(Action<string> log) => _log = log;

    public IReadOnlyList<string> Devices()
    {
        if (_devices != null) return _devices;
        var names = new List<string>();
        foreach (var (hMon, name) in Monitors.EnumerateHandles())
        {
            names.Add(name);
            try
            {
                if (!Dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) || count == 0)
                    continue;
                var arr = new PhysicalMonitor[count];
                if (!Dxva2.GetPhysicalMonitorsFromHMONITOR(hMon, count, arr))
                    continue;
                _arrays.Add(arr);
                if (arr.Length > 1)
                    _log($"{name}: физических мониторов {arr.Length}, управляем первым");
                _handles[name] = arr[0].hPhysicalMonitor;
            }
            catch (Exception ex)
            {
                _log($"{name}: ошибка DDC-перечисления: {ex.Message}");
            }
        }
        _devices = names;
        return names;
    }

    public bool TryGet(string device, out int min, out int cur, out int max)
    {
        min = cur = max = 0;
        EnsureEnumerated();
        if (!_handles.TryGetValue(device, out nint h)) return false;

        if (!_vcpMode.Contains(device)
            && Dxva2.GetMonitorBrightness(h, out uint mn, out uint c, out uint mx))
        {
            min = (int)mn; cur = (int)c; max = (int)mx;
            return true;
        }
        // Запасной путь: VCP 0x10 (минимум у VCP всегда 0).
        if (Dxva2.GetVCPFeatureAndVCPFeatureReply(h, VcpBrightness, 0, out uint vc, out uint vm))
        {
            _vcpMode.Add(device);
            min = 0; cur = (int)vc; max = (int)vm;
            return true;
        }
        return false;
    }

    public bool TrySet(string device, int value)
    {
        EnsureEnumerated();
        if (!_handles.TryGetValue(device, out nint h) || value < 0) return false;
        uint v = (uint)value;
        if (_vcpMode.Contains(device))
            return Dxva2.SetVCPFeature(h, VcpBrightness, v);
        return Dxva2.SetMonitorBrightness(h, v);
    }

    private void EnsureEnumerated()
    {
        if (_devices == null) Devices();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var arr in _arrays)
        {
            try { Dxva2.DestroyPhysicalMonitors((uint)arr.Length, arr); }
            catch (Exception ex) { _log($"DestroyPhysicalMonitors: {ex.Message}"); }
        }
        _arrays.Clear();
        _handles.Clear();
    }
}
