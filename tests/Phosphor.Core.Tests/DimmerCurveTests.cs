using Phosphor.Core;

namespace Phosphor.Core.Tests;

public sealed class DimmerCurveTests
{
    private static DimmerConfig Cfg(bool enabled = true, float delayMin = 10f, float fadeSec = 60f, int hwLevel = 0, float swLevel = 0.25f) =>
        new() { Enabled = enabled, DelayMin = delayMin, FadeSec = fadeSec, Hardware = true, HardwareLevel = hwLevel, SoftwareLevel = swLevel };

    [Fact]
    public void Progress_BeforeDelay_IsZero()
    {
        var c = Cfg(delayMin: 10f, fadeSec: 60f);
        Assert.Equal(0f, DimmerCurve.Progress(c, 0));
        Assert.Equal(0f, DimmerCurve.Progress(c, 599.9));
    }

    [Fact]
    public void Progress_MiddleOfFade_IsHalf()
    {
        var c = Cfg(delayMin: 10f, fadeSec: 60f);
        // Середина фейда: 600 с задержки + 30 с → smoothstep(0.5) = 0.5
        Assert.InRange(DimmerCurve.Progress(c, 630), 0.499f, 0.501f);
    }

    [Fact]
    public void Progress_AfterFade_IsOne()
    {
        var c = Cfg(delayMin: 10f, fadeSec: 60f);
        Assert.Equal(1f, DimmerCurve.Progress(c, 660));
        Assert.Equal(1f, DimmerCurve.Progress(c, 99999));
    }

    [Fact]
    public void Progress_IsMonotone()
    {
        var c = Cfg(delayMin: 1f, fadeSec: 30f);
        float prev = -1f;
        for (double t = 0; t <= 120; t += 0.5)
        {
            float p = DimmerCurve.Progress(c, t);
            Assert.True(p >= prev, $"прогресс в t={t} уменьшился: {p} < {prev}");
            prev = p;
        }
    }

    [Fact]
    public void Progress_FadeZero_JumpsToOneAtDelay()
    {
        var c = Cfg(delayMin: 1f, fadeSec: 0f);
        Assert.Equal(0f, DimmerCurve.Progress(c, 59.9));
        Assert.Equal(1f, DimmerCurve.Progress(c, 60));
    }

    [Fact]
    public void Progress_Disabled_IsAlwaysZero()
    {
        var c = Cfg(enabled: false, delayMin: 0f, fadeSec: 0f);
        Assert.Equal(0f, DimmerCurve.Progress(c, 0));
        Assert.Equal(0f, DimmerCurve.Progress(c, 99999));
    }

    [Fact]
    public void HardwareLevel_ProgressZero_ReturnsOriginal()
    {
        Assert.Equal(0.7f, DimmerCurve.HardwareLevel(Cfg(hwLevel: 0), 0.7f, 0f));
    }

    [Fact]
    public void HardwareLevel_ProgressOne_ReturnsTarget()
    {
        Assert.Equal(0.1f, DimmerCurve.HardwareLevel(Cfg(hwLevel: 10), 0.7f, 1f), 4);
    }

    [Fact]
    public void HardwareLevel_NeverBrighterThanOriginal()
    {
        // Целевой уровень выше исходного — не повышаем, возвращаем исходный при любом прогрессе.
        var c = Cfg(hwLevel: 80);
        foreach (float p in new[] { 0f, 0.5f, 1f })
            Assert.Equal(0.3f, DimmerCurve.HardwareLevel(c, 0.3f, p));
    }

    [Fact]
    public void HardwareLevel_EqualToOriginal_ReturnsOriginal()
    {
        Assert.Equal(0.5f, DimmerCurve.HardwareLevel(Cfg(hwLevel: 50), 0.5f, 1f));
    }

    [Fact]
    public void SoftwareGain_ProgressZero_IsOne()
    {
        Assert.Equal(1f, DimmerCurve.SoftwareGain(Cfg(swLevel: 0.25f), 0f));
    }

    [Fact]
    public void SoftwareGain_ProgressOne_IsSoftwareLevel()
    {
        Assert.Equal(0.25f, DimmerCurve.SoftwareGain(Cfg(swLevel: 0.25f), 1f), 4);
        Assert.Equal(0f, DimmerCurve.SoftwareGain(Cfg(swLevel: 0f), 1f), 4);
    }
}
