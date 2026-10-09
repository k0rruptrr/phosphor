using System.Runtime.InteropServices;

namespace Phosphor.Platform;

public readonly record struct MonitorRect(int X, int Y, int Width, int Height, bool Primary, string Name);

/// <summary>Перечисление мониторов. Физические пиксели, экранные координаты.</summary>
public static class Monitors
{
    /// <summary>Мониторы: основной первым, остальные по возрастанию X (при равном X — по Y).</summary>
    public static IReadOnlyList<MonitorRect> Enumerate()
    {
        var handles = new List<nint>();
        // Делегат живёт в локальной переменной на время синхронного вызова EnumDisplayMonitors.
        MonitorEnumProc proc = (hMonitor, hdc, lprc, data) =>
        {
            handles.Add(hMonitor);
            return true;
        };
        Win32.EnumDisplayMonitors(0, 0, proc, 0);
        GC.KeepAlive(proc);

        var result = new List<MonitorRect>(handles.Count);
        foreach (nint h in handles)
        {
            var info = new MONITORINFOEXW
            {
                cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>(),
                szDevice = string.Empty,
            };
            if (!Win32.GetMonitorInfoW(h, ref info))
                continue;

            RECT r = info.rcMonitor;
            bool primary = (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0;
            string name = (info.szDevice ?? string.Empty).TrimEnd('\0');
            result.Add(new MonitorRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, primary, name));
        }

        result.Sort((a, b) =>
        {
            if (a.Primary != b.Primary) return a.Primary ? -1 : 1;
            int byX = a.X.CompareTo(b.X);
            return byX != 0 ? byX : a.Y.CompareTo(b.Y);
        });
        return result;
    }

    /// <summary>Прямоугольник, покрывающий все мониторы (виртуальный экран).</summary>
    public static MonitorRect VirtualScreen()
    {
        int x = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int y = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int w = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        int h = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);
        return new MonitorRect(x, y, w, h, false, "VirtualScreen");
    }
}
