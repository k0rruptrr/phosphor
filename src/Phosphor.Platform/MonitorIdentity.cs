using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Phosphor.Platform;

/// <summary>
/// Монитор «по-человечески»: Device — \\.\DISPLAYn (меняется при перестановке), Key — стабильный ключ
/// «PNP-ID#экземпляр» (переживает перезагрузку и смену номера), Model — имя модели из EDID (или из драйвера).
/// </summary>
public sealed record MonitorIdentity(string Device, string Key, string Model);

public static class MonitorIdentities
{
    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICEW
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

    /// <summary>Ключ и модель для каждого монитора из Monitors.Enumerate (в том же порядке). Ошибки — ключ = имя устройства.</summary>
    public static IReadOnlyList<MonitorIdentity> Describe(IEnumerable<MonitorRect> monitors)
    {
        var result = new List<MonitorIdentity>();
        foreach (var m in monitors) result.Add(Describe(m.Name));
        return result;
    }

    public static MonitorIdentity Describe(string device)
    {
        try
        {
            var dd = new DISPLAY_DEVICEW { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICEW>() };
            // Первый монитор на выходе видеокарты: интерфейсное имя вида \\?\DISPLAY#AUS27A1#5&2a2c0f4d&0&UID4357#{guid}
            if (EnumDisplayDevicesW(device, 0, ref dd, EDD_GET_DEVICE_INTERFACE_NAME)
                && ParseDeviceId(dd.DeviceID) is var (pnp, inst))
            {
                string? model = ReadEdidName(pnp, inst);
                return new MonitorIdentity(device, pnp + "#" + inst, model ?? NonGeneric(dd.DeviceString) ?? pnp);
            }
        }
        catch (Exception) { /* ниже — запасной вариант */ }
        return new MonitorIdentity(device, device, device);
    }

    /// <summary>\\?\DISPLAY#AUS27A1#5&amp;2a2c0f4d&amp;0&amp;UID4357#{…} → ("AUS27A1", "5&amp;2a2c0f4d&amp;0&amp;UID4357"). Иначе null.</summary>
    public static (string Pnp, string Instance)? ParseDeviceId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var parts = id.Split('#');
        if (parts.Length < 3 || !parts[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase)) return null;
        if (parts[1].Length == 0 || parts[2].Length == 0) return null;
        return (parts[1], parts[2]);
    }

    /// <summary>Имя модели из EDID: дескриптор 0xFC (до 13 символов ASCII, конец — 0x0A). Нет — null.</summary>
    public static string? EdidName(byte[]? edid)
    {
        if (edid is null || edid.Length < 128) return null;
        for (int d = 54; d + 18 <= 126; d += 18)
        {
            if (edid[d] != 0 || edid[d + 1] != 0 || edid[d + 3] != 0xFC) continue;
            var sb = new StringBuilder();
            for (int i = d + 5; i < d + 18; i++)
            {
                byte b = edid[i];
                if (b == 0x0A || b == 0) break;
                if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
            }
            string s = sb.ToString().Trim();
            return s.Length > 0 ? s : null;
        }
        return null;
    }

    private static string? ReadEdidName(string pnp, string inst)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{pnp}\{inst}\Device Parameters");
        return EdidName(k?.GetValue("EDID") as byte[]);
    }

    private static string? NonGeneric(string? s) =>
        string.IsNullOrWhiteSpace(s) || s.Contains("Generic", StringComparison.OrdinalIgnoreCase) || s.Contains("Универсальный", StringComparison.OrdinalIgnoreCase)
            ? null : s.Trim();
}
