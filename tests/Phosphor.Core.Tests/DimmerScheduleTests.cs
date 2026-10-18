using Phosphor.Core;

namespace Phosphor.Core.Tests;

/// <summary>Расписание диммера (только в часы) и ручное включение клавишей D.</summary>
public sealed class DimmerScheduleTests
{
    private static DimmerConfig Night(string from = "22:00", string to = "08:00") =>
        new() { Enabled = true, DelayMin = 0, FadeSec = 10, Schedule = DimmerConfig.ScheduleHours, From = from, To = to };

    private static TimeSpan T(int h, int m = 0) => new(h, m, 0);

    [Theory]
    [InlineData(23, true)]
    [InlineData(2, true)]
    [InlineData(7, true)]
    [InlineData(8, false)]
    [InlineData(14, false)]
    [InlineData(21, false)]
    [InlineData(22, true)]
    public void WindowThroughMidnight(int hour, bool inside) =>
        Assert.Equal(inside, DimmerSchedule.InWindow(Night(), T(hour)));

    [Fact]
    public void WindowInsideDay()
    {
        var c = Night("13:00", "15:30");
        Assert.True(DimmerSchedule.InWindow(c, T(14)));
        Assert.False(DimmerSchedule.InWindow(c, T(15, 30)));
        Assert.False(DimmerSchedule.InWindow(c, T(9)));
    }

    [Fact]
    public void AlwaysOrBrokenWindow_NoLimit()
    {
        Assert.True(DimmerSchedule.InWindow(new DimmerConfig(), T(14)));
        Assert.True(DimmerSchedule.InWindow(Night("abc", "08:00"), T(14)));
        Assert.True(DimmerSchedule.InWindow(Night("08:00", "08:00"), T(14)));
        Assert.Equal(T(7, 5), DimmerSchedule.ParseTime("7:05"));
        Assert.Null(DimmerSchedule.ParseTime("25:00"));
    }

    private static float Run(DimmerGate g, DimmerConfig c, TimeSpan tod, float seconds, ref double elapsed)
    {
        float p = 0;
        for (float t = 0; t < seconds; t += 0.1f) { elapsed += 0.1; p = g.Update(c, elapsed, tod, 0.1f); }
        return p;
    }

    [Fact]
    public void DayTime_DoesNotDim_NightFadesIn_MorningFadesOut()
    {
        var c = Night();
        var g = new DimmerGate();
        double el = 0;
        Assert.Equal(0f, Run(g, c, T(14), 30, ref el));                 // днём не трогает
        Assert.Equal(1f, Run(g, c, T(22, 1), 15, ref el), 3);         // наступило окно — плавно до конца за FadeSec
        float mid = Run(g, c, T(8, 1), 5, ref el);                      // утро: на полпути отпускает
        Assert.InRange(mid, 0.1f, 0.9f);
        Assert.Equal(0f, Run(g, c, T(8, 1), 10, ref el));
    }

    [Fact]
    public void KeyD_TogglesAgainstSchedule()
    {
        var c = Night();
        var g = new DimmerGate();
        double el = 0;
        Run(g, c, T(14), 1, ref el);
        Assert.True(g.Toggle());                                        // днём D — гасить сейчас, без задержки
        Assert.Equal(1f, Run(g, c, T(14), 15, ref el), 3);
        Assert.False(g.Toggle());                                       // ещё раз — отпустить
        Assert.Equal(0f, Run(g, c, T(14), 15, ref el));
    }

    [Fact]
    public void KeyD_WorksEvenWhenDimmerDisabled_AndStopsNightDimming()
    {
        var off = new DimmerConfig { Enabled = false, FadeSec = 0 };
        var g = new DimmerGate();
        double el = 0;
        Assert.Equal(0f, Run(g, off, T(23), 1, ref el));
        Assert.True(g.Toggle());
        Assert.Equal(1f, Run(g, off, T(23), 1, ref el));

        var night = new DimmerGate();
        el = 0;
        Assert.Equal(1f, Run(night, Night(), T(23), 15, ref el), 3);
        Assert.False(night.Toggle());                                   // ночью D — не гасить
        Assert.Equal(0f, Run(night, Night(), T(23), 15, ref el));
    }

    [Fact]
    public void ScheduleRoundTripsThroughJson()
    {
        var cfg = PhosphorConfig.Parse("""{ "dimmer": { "schedule": "hours", "from": "23:30", "to": "06:00" } }""");
        Assert.Equal("hours", cfg.Dimmer.Schedule);
        Assert.Equal("23:30", cfg.Dimmer.From);
        Assert.Equal("always", PhosphorConfig.Parse("{}").Dimmer.Schedule);
    }
}
