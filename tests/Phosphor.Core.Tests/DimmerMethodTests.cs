using Phosphor.Core;

namespace Phosphor.Core.Tests;

/// <summary>Режим затемнения по монитору (dimmer.monitors): auto / ddc / software / off.</summary>
public sealed class DimmerMethodTests
{
    private const string Liar = "XYM2721#5&17ea06af&2&UID4355";
    private const string Asus = "AUS27A5#5&17ea06af&2&UID4352";

    private static DimmerConfig Cfg(bool hardware, params (string Key, string Mode)[] modes)
    {
        var c = new DimmerConfig { Hardware = hardware };
        foreach (var (k, m) in modes) c.Monitors[k] = m;
        return c;
    }

    [Theory]
    [InlineData(true, true, DimMethod.Backlight)]
    [InlineData(true, false, DimMethod.Software)]
    [InlineData(false, true, DimMethod.Software)]
    public void Auto_FollowsGlobalSwitchAndDdcAnswer(bool hardware, bool answers, DimMethod expected) =>
        Assert.Equal(expected, DimmerCurve.Method(Cfg(hardware), Asus, answers));

    [Fact]
    public void Software_IgnoresDdcEvenIfItAnswers() =>
        Assert.Equal(DimMethod.Software, DimmerCurve.Method(Cfg(true, (Liar, "software")), Liar, true));

    [Fact]
    public void Off_LeavesMonitorAlone() =>
        Assert.Equal(DimMethod.None, DimmerCurve.Method(Cfg(true, (Asus, "off")), Asus, true));

    [Theory]
    [InlineData(true, DimMethod.Backlight)]
    [InlineData(false, DimMethod.Software)]
    public void Ddc_WorksEvenWithGlobalSwitchOff_FallsBackWhenSilent(bool answers, DimMethod expected) =>
        Assert.Equal(expected, DimmerCurve.Method(Cfg(false, (Asus, "ddc")), Asus, answers));

    [Fact]
    public void ModeLookup_IgnoresCase_UnknownIsAuto()
    {
        var c = Cfg(true, (Liar.ToLowerInvariant(), "SOFTWARE"), (Asus, "turbo"));
        Assert.Equal("software", DimmerCurve.ModeOf(c, Liar));
        Assert.Equal("auto", DimmerCurve.ModeOf(c, Asus));
        Assert.Equal("auto", DimmerCurve.ModeOf(c, "OTHER#1"));
        Assert.Equal("auto", DimmerCurve.ModeOf(c, null));
    }

    [Fact]
    public void AnyBacklight_TrueWhenAMonitorForcesDdc()
    {
        Assert.False(DimmerCurve.AnyBacklight(Cfg(false)));
        Assert.True(DimmerCurve.AnyBacklight(Cfg(false, (Asus, "ddc"))));
        Assert.True(DimmerCurve.AnyBacklight(Cfg(true)));
    }

    [Fact]
    public void MonitorsRoundTripThroughJson()
    {
        var cfg = PhosphorConfig.Parse($$"""{ "dimmer": { "monitors": { "{{Liar}}": "software" } } }""");
        Assert.Equal("software", DimmerCurve.ModeOf(cfg.Dimmer, Liar));
        Assert.Empty(PhosphorConfig.Parse("{}").Dimmer.Monitors);
    }
}
