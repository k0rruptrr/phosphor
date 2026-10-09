namespace Phosphor.Platform;

/// <summary>Настройка DPI-осведомлённости процесса.</summary>
public static class Dpi
{
    /// <summary>Включить Per-Monitor DPI Aware (V1 через контекст -4). Ошибку игнорируем: вызов идемпотентен.</summary>
    public static void EnsurePerMonitorAware()
    {
        _ = Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE);
    }
}
