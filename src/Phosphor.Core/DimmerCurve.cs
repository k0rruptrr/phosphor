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
}
