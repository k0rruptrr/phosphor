using System.Text.Json;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class ClocksSceneTests
{
    private readonly ITestOutputHelper _out;

    public ClocksSceneTests(ITestOutputHelper output) => _out = output;

    private static readonly DateTime T1405 = new(2026, 10, 9, 14, 5, 9);

    private static SceneOptions Opts(string json) => new() { Params = JsonDocument.Parse(json).RootElement.Clone() };

    private static ClocksScene Make(int seed, SceneOptions? o = null, int cols = 120, int rows = 40)
    {
        // Старый параметр face выбирает сцену (циферблаты стали отдельными сценами)
        o ??= new SceneOptions();
        var s = ClocksScene.ForFace(o.GetString("face", "flap"));
        s.Init(cols, rows, new Random(seed), o);
        return s;
    }

    private static void Feed(ClocksScene s, DateTime local) => ((IDataScene)s).OnData("clock", new ClockSnapshot(local));

    private static string Text(CharGrid g, int y) =>
        new string(Enumerable.Range(0, g.Cols).Select(x => g[x, y].Ch).ToArray());

    private static string Key(CharGrid g) => new string(g.Cells.Select(c => c.Ch).ToArray());

    [Fact]
    public void Contract()
    {
        SceneContract.VerifyAll(() => new SplitFlapScene(), "splitflap");
        SceneContract.VerifyAll(() => new NixieScene(), "nixie");
        SceneContract.VerifyAll(() => new WorldClockScene(), "worldclock");
        SceneContract.VerifyAll(() => new AnalogClockScene(), "analogclock");
    }

    [Fact]
    public void FlapShowsSnapshotTime()
    {
        var s = Make(1, Opts("{\"face\":\"flap\"}"));
        Feed(s, T1405);
        SceneContract.Run(s, 60);
        Assert.Equal("14:05:09", s.ShownTime());
        var g = SceneContract.Snap(s, 120, 40);
        Assert.Contains(g.Cells, c => c.Ch == '█');
        _out.WriteLine(SceneContract.Dump(g, "clocks-flap"));
    }

    [Fact]
    public void FlapHidesSecondsWhenOff()
    {
        var s = Make(2, Opts("{\"face\":\"flap\",\"seconds\":false}"));
        Feed(s, T1405);
        SceneContract.Run(s, 60);
        Assert.Equal("14:05", s.ShownTime());
    }

    [Fact]
    public void FlapFlipTakesSeveralFrames()
    {
        var s = Make(3, Opts("{\"face\":\"flap\"}"));
        Feed(s, new DateTime(2026, 10, 9, 14, 5, 8));
        SceneContract.Run(s, 120);

        Feed(s, T1405);
        var keys = new HashSet<string> { Key(SceneContract.Snap(s, 120, 40)) };
        for (int i = 0; i < 60; i++)
        {
            SceneContract.Run(s, 1);
            keys.Add(Key(SceneContract.Snap(s, 120, 40)));
        }
        Assert.True(keys.Count >= 3, $"переворот дал только {keys.Count} разных кадров");
        Assert.Equal("14:05:09", s.ShownTime());
    }

    [Fact]
    public void NixieCathodeRunAtTenMinuteMark()
    {
        var s = Make(4, Opts("{\"face\":\"nixie\"}"));
        Feed(s, new DateTime(2026, 10, 9, 14, 5, 0));
        Assert.False(s.CathodeRunning);
        SceneContract.Run(s, 60);
        Assert.Equal("140500", s.LampDigits());

        var s2 = Make(5, Opts("{\"face\":\"nixie\"}"));
        Feed(s2, new DateTime(2026, 10, 9, 14, 10, 0));
        Assert.True(s2.CathodeRunning);
        var seen = new HashSet<char>();
        for (int i = 0; i < 60; i++)
        {
            SceneContract.Run(s2, 1, 0.05f);
            seen.Add(s2.LampDigits()[0]);
        }
        Assert.True(seen.Count >= 5, $"прогон катодов показал цифр: {seen.Count}");
        SceneContract.Run(s2, 200, 0.02f);
        Assert.False(s2.CathodeRunning);
        Assert.Equal("141000", s2.LampDigits());
    }

    [Fact]
    public void NixieDrawsWireDigitsInTubes()
    {
        var s = Make(6, Opts("{\"face\":\"nixie\"}"));
        Feed(s, T1405);
        SceneContract.Run(s, 30);
        var g = SceneContract.Snap(s, 120, 40);
        // Горящая проволока (секстанты цвета накала), призрачные штрихи, стеклянные колбы и сетка-анод.
        Assert.Contains(g.Cells, c => GlyphSet.IsSextant(c.Ch) && c.Fg == Rgba.Hex(0xFFB060));
        Assert.Contains(g.Cells, c => GlyphSet.IsSextant(c.Ch) && c.Fg != Rgba.Hex(0xFFB060));
        Assert.Contains(g.Cells, c => c.Ch == '╭');
        Assert.Contains(g.Cells, c => c.Ch == '▒');
        _out.WriteLine(SceneContract.Dump(g, "clocks-nixie"));
    }

    [Fact]
    public void NixieLampRowTakesAboutSeventyPercentOfWidth()
    {
        var s = Make(13, Opts("{\"face\":\"nixie\"}"));
        Feed(s, T1405);
        SceneContract.Run(s, 30);
        var g = SceneContract.Snap(s, 120, 40);
        int minX = int.MaxValue, maxX = -1;
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Cols; x++)
                if (g[x, y].Ch != ' ') { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); }
        double span = (maxX - minX + 1) / (double)g.Cols;
        Assert.InRange(span, 0.6, 0.8);
    }


    [Fact]
    public void WorldTokyoOffsetIsPlusNineWithoutDst()
    {
        var tokyo = ClocksScene.WorldCities.Single(c => c.Name == "ТОКИО");
        var summer = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var winter = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(TimeSpan.FromHours(9), ClocksScene.OffsetOf(tokyo, summer));
        Assert.Equal(TimeSpan.FromHours(9), ClocksScene.OffsetOf(tokyo, winter));
    }

    [Fact]
    public void WorldLondonFollowsDaylightSaving()
    {
        var london = ClocksScene.WorldCities.Single(c => c.Name == "ЛОНДОН");
        Assert.Equal(TimeSpan.FromHours(1), ClocksScene.OffsetOf(london, new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(TimeSpan.Zero, ClocksScene.OffsetOf(london, new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void WorldFaceShowsCitiesAndOffsets()
    {
        var s = Make(7, Opts("{\"face\":\"world\"}"));
        Feed(s, T1405);
        SceneContract.Run(s, 30);
        var g = SceneContract.Snap(s, 120, 40);
        string all = string.Join("\n", Enumerable.Range(0, g.Rows).Select(y => Text(g, y)));
        Assert.Contains("ТОКИО", all);
        Assert.Contains("UTC+09:00", all);
        Assert.Contains("МОСКВА", all);
        _out.WriteLine(SceneContract.Dump(g, "clocks-world"));
    }

    [Fact]
    public void WorldSecondsToggleChangesRows()
    {
        CharGrid Shot(bool seconds)
        {
            var s = Make(7, Opts($"{{\"face\":\"world\",\"seconds\":{(seconds ? "true" : "false")}}}"));
            Feed(s, T1405);
            SceneContract.Run(s, 30);
            return SceneContract.Snap(s, 120, 40);
        }
        Assert.NotEqual(Key(Shot(true)), Key(Shot(false)));
    }

    [Fact]
    public void TerminatorNoonUtcOnMeridianIsDay()
    {
        var noon = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(ClocksScene.IsDay(0, 0, noon));
        Assert.False(ClocksScene.IsDay(0, 180, noon));
    }

    [Fact]
    public void AnalogHourHandAtThreeIsOnThree()
    {
        var (hour, minute, second) = ClocksScene.HandAngles(new DateTime(2026, 10, 9, 3, 0, 0), true);
        Assert.True(Math.Abs(hour - 90f) < 0.01f, $"часовая: {hour}");
        Assert.True(Math.Abs(minute) < 0.01f, $"минутная: {minute}");
        Assert.True(Math.Abs(second) < 0.01f, $"секундная: {second}");

        var s = Make(8, Opts("{\"face\":\"analog\"}"));
        Feed(s, new DateTime(2026, 10, 9, 3, 0, 0));
        SceneContract.Run(s, 10);
        var g = SceneContract.Snap(s, 120, 40);
        int mid = g.Rows / 2;
        int right = 0, left = 0;
        for (int y = mid - 1; y <= mid + 1; y++)
            for (int x = 0; x < g.Cols; x++)
            {
                if (g[x, y].Ch == ' ') continue;
                if (x > g.Cols / 2) right++;
                else if (x < g.Cols / 2) left++;
            }
        Assert.True(right > left, $"стрелка не на «3»: справа {right}, слева {left}");
        _out.WriteLine(SceneContract.Dump(g, "clocks-analog"));
    }

    [Fact]
    public void AnalogSecondHandSweepsSmoothly()
    {
        var (_, _, smooth) = ClocksScene.HandAngles(new DateTime(2026, 10, 9, 3, 0, 0, 500), true);
        var (_, _, stepped) = ClocksScene.HandAngles(new DateTime(2026, 10, 9, 3, 0, 0, 500), false);
        Assert.True(Math.Abs(smooth - 3f) < 0.01f);
        Assert.True(Math.Abs(stepped) < 0.01f);
    }

    [Theory]
    [InlineData("splitflap", "flap")]
    [InlineData("nixie", "nixie")]
    [InlineData("worldclock", "world")]
    [InlineData("analogclock", "analog")]
    public void EachSceneKeepsItsFace(string id, string face)
    {
        var s = Make(9, Opts($"{{\"face\":\"{face}\"}}"));
        Assert.Equal(id, s.Id);
        Assert.Equal(face, s.CurrentFace);
        SceneContract.Run(s, 200 * 60, 1f / 60f); // старый круг сменил бы циферблат через 45 с
        Assert.Equal(face, s.CurrentFace);
    }

    [Fact]
    public void SweepParamOnlyOnAnalog()
    {
        Assert.Contains(new AnalogClockScene().Params, p => p.Name == "sweep");
        Assert.DoesNotContain(new NixieScene().Params, p => p.Name == "sweep");
        Assert.All(new ClocksScene[] { new SplitFlapScene(), new NixieScene(), new WorldClockScene(), new AnalogClockScene() },
            s => Assert.DoesNotContain(s.Params, p => p.Name is "face" or "faceSec"));
    }

    [Fact]
    public void NoClockShowsPlaceholders()
    {
        var s = Make(11, Opts("{\"face\":\"world\"}"));
        SceneContract.Run(s, 60);
        var g = SceneContract.Snap(s, 120, 40);
        string all = string.Join("\n", Enumerable.Range(0, g.Rows).Select(y => Text(g, y)));
        Assert.Contains("--:--:--", all);
        Assert.DoesNotContain("UTC+", all);
    }

    [Theory]
    [InlineData(1.8f)]
    [InlineData(2.4f)]
    public void AnalogDialIsRoundInPixels(float aspect)
    {
        var o = new SceneOptions { CellAspect = aspect, Params = Opts("{\"face\":\"analog\"}").Params };
        var s = Make(12, o, 120, 40);
        Feed(s, new DateTime(2026, 10, 9, 10, 10, 30));
        SceneContract.Run(s, 10);
        var g = SceneContract.Snap(s, 120, 40);

        // Физические пиксели: ширина ячейки 10, высота = aspect × 10.
        double cw = 10, ch = aspect * cw;
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Cols; x++)
            {
                char c = g[x, y].Ch;
                if (!GlyphSet.IsSextant(c)) continue;
                int bits = c - 0xE000;
                for (int dy = 0; dy < 3; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        if (((bits >> (dy * 2 + dx)) & 1) == 0) continue;
                        double px = (x * 2 + dx) * cw / 2, py = (y * 3 + dy) * ch / 3;
                        minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
                        minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
                    }
            }
        double ratio = (maxX - minX) / (maxY - minY);
        _out.WriteLine($"aspect {aspect}: ширина {maxX - minX:F1} px, высота {maxY - minY:F1} px, отношение {ratio:F3}");
        Assert.InRange(ratio, 0.95, 1.05);
    }

    [Fact]
    public void TerminatorBoundaryNearMeridianAtEquinox()
    {
        // Около равноденствия склонение ≈ 0: граница день/ночь почти по меридиану на 0° и 50° широты.
        var utc = new DateTime(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc);
        double Boundary(double lat)
        {
            for (double lon = 0; lon <= 180; lon += 0.25)
                if (!ClocksScene.IsDay(lat, lon, utc)) return lon;
            return double.NaN;
        }
        double b0 = Boundary(0), b50 = Boundary(50);
        Assert.InRange(b0, 85, 95);
        Assert.True(Math.Abs(b0 - b50) < 10, $"граница: на 0° — {b0}, на 50° — {b50}");
    }

    [Fact]
    public void WeatherCityIsFirstWorldRow()
    {
        var s = Make(14, Opts("{\"face\":\"world\"}"));
        Assert.Contains("weather", s.DataKeys);
        ((IDataScene)s).OnData("weather", new WeatherSnapshot("Тверь", 56.86, 35.9,
            new WeatherNow(0, 0, 0, 0), Array.Empty<WeatherDay>(), DateTimeOffset.Now, false));
        Feed(s, T1405);
        SceneContract.Run(s, 30);
        Assert.Equal("ТВЕРЬ", s.WorldRowNames()[0]);
        Assert.Equal("МОСКВА", s.WorldRowNames()[1]);
        var g = SceneContract.Snap(s, 120, 40);
        Assert.Contains("ТВЕРЬ", Text(g, 3));
    }

    [Fact]
    public void WithoutWeatherMoscowIsFirst()
    {
        var s = Make(15, Opts("{\"face\":\"world\"}"));
        Assert.Equal("МОСКВА", s.WorldRowNames()[0]);
    }

    [Fact]
    public void WeatherCityMatchingFixedCityIsNotDuplicated()
    {
        var s = Make(16, Opts("{\"face\":\"world\"}"));
        ((IDataScene)s).OnData("weather", new WeatherSnapshot("Москва", 55.75, 37.62,
            new WeatherNow(0, 0, 0, 0), Array.Empty<WeatherDay>(), DateTimeOffset.Now, false));
        var names = s.WorldRowNames();
        Assert.Equal("МОСКВА", names[0]);
        Assert.Equal(1, names.Count(n => n == "МОСКВА"));
    }

    [Fact]
    public void SweepChangesSecondHandMotion()
    {
        CharGrid Analog(bool sweep)
        {
            var s = Make(17, Opts($"{{\"face\":\"analog\",\"sweep\":{(sweep ? "true" : "false")}}}"));
            Feed(s, new DateTime(2026, 10, 9, 3, 0, 20, 500));
            SceneContract.Run(s, 10);
            return SceneContract.Snap(s, 120, 40);
        }
        Assert.False(Analog(true).Cells.SequenceEqual(Analog(false).Cells));
    }

    [Fact]
    public void WeatherCityMatchingFixedCityKeepsItsCode()
    {
        var s = Make(18, Opts("{\"face\":\"world\"}"));
        ((IDataScene)s).OnData("weather", new WeatherSnapshot("Москва", 55.75, 37.62,
            new WeatherNow(0, 0, 0, 0), Array.Empty<WeatherDay>(), DateTimeOffset.Now, false));
        Assert.Equal("MOW", s.WorldRowCodes()[0]);
    }
}
