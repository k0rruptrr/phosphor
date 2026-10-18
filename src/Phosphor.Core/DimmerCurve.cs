namespace Phosphor.Core;

/// <summary>
/// Кривая диммера заставки (см. DimmerConfig): прогресс затемнения по времени и уровни подсветки / картинки.
/// Чистые функции, без состояния и побочных эффектов.
/// </summary>
public static class DimmerCurve
{
    /// <summary>0 до DelayMin минут, затем smoothstep до 1 за FadeSec. FadeSec=0 — сразу 1. Enabled=false — всегда 0.</summary>
    public static float Progress(DimmerConfig c, double elapsedSec)
    {
        if (!c.Enabled) return 0f;
        double delay = c.DelayMin * 60.0;
        if (elapsedSec < delay) return 0f;
        if (c.FadeSec <= 0f) return 1f;
        double t = Math.Clamp((elapsedSec - delay) / c.FadeSec, 0.0, 1.0);
        return (float)(t * t * (3.0 - 2.0 * t));
    }

    /// <summary>
    /// Уровень подсветки 0..1 (доля диапазона монитора) на прогрессе progress. Диммер никогда не делает ярче:
    /// если целевой уровень не ниже исходного — возвращается исходный.
    /// </summary>
    public static float HardwareLevel(DimmerConfig c, float originalLevel01, float progress)
    {
        float target = c.HardwareLevel / 100f;
        if (target >= originalLevel01) return originalLevel01;
        float p = Math.Clamp(progress, 0f, 1f);
        return originalLevel01 + (target - originalLevel01) * p;
    }

    /// <summary>Множитель яркости картинки 0..1 для мониторов без DDC: lerp(1, SoftwareLevel, progress).</summary>
    public static float SoftwareGain(DimmerConfig c, float progress)
    {
        float p = Math.Clamp(progress, 0f, 1f);
        return 1f + (c.SoftwareLevel - 1f) * p;
    }

    /// <summary>
    /// Чем гасить монитор. Режим из DimmerConfig.Monitors по ключу монитора (нет — auto):
    /// auto — подсветка, если включён Hardware и монитор отвечает по DDC, иначе картинка;
    /// ddc — подсветка, если отвечает (не отвечает — картинка: гасить всё равно надо); both — подсветка и картинка
    /// (минимум подсветки дешёвой панели ещё яркий); software — только картинка
    /// (монитор «врёт» по DDC); off — не трогать.
    /// </summary>
    public static DimMethod Method(DimmerConfig c, string? monitorKey, bool ddcAnswers) =>
        ModeOf(c, monitorKey) switch
        {
            DimmerConfig.ModeOff => DimMethod.None,
            DimmerConfig.ModeSoftware => DimMethod.Software,
            DimmerConfig.ModeDdc => ddcAnswers ? DimMethod.Backlight : DimMethod.Software,
            DimmerConfig.ModeBoth => ddcAnswers ? DimMethod.Both : DimMethod.Software,
            _ => c.Hardware && ddcAnswers ? DimMethod.Backlight : DimMethod.Software,
        };

    /// <summary>Режим монитора по ключу (без учёта регистра); неизвестное значение — auto.</summary>
    public static string ModeOf(DimmerConfig c, string? monitorKey)
    {
        if (monitorKey is null) return DimmerConfig.ModeAuto;
        foreach (var (k, v) in c.Monitors)
            if (string.Equals(k, monitorKey, StringComparison.OrdinalIgnoreCase))
                return Array.Find(DimmerConfig.Modes, m => string.Equals(m, v, StringComparison.OrdinalIgnoreCase)) ?? DimmerConfig.ModeAuto;
        return DimmerConfig.ModeAuto;
    }

    /// <summary>Нужна ли подсветка вообще (стоит ли запускать опрос DDC).</summary>
    public static bool AnyBacklight(DimmerConfig c) =>
        c.Hardware || c.Monitors.Values.Any(v => string.Equals(v, DimmerConfig.ModeDdc, StringComparison.OrdinalIgnoreCase)
                                                  || string.Equals(v, DimmerConfig.ModeBoth, StringComparison.OrdinalIgnoreCase));
}

public enum DimMethod { Backlight, Software, Both, None }
