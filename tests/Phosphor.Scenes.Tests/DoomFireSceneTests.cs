using System.Diagnostics;
using System.Text.Json;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class DoomFireSceneTests
{
    private const string AsciiRamp = " .:-=+*#%@";

    private readonly ITestOutputHelper _out;

    public DoomFireSceneTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new DoomFireScene(), "doomfire");

    [Fact]
    public void BottomIsBrighterThanMiddleAndMiddleThanTop()
    {
        var s = SceneContract.Make(() => new DoomFireScene(), 120, 40, 41);
        SceneContract.Run(s, 180);
        var g = SceneContract.Snap(s, 120, 40);

        double top = AvgLuma(g, 0, 40 / 3);
        double mid = AvgLuma(g, 40 / 3, 80 / 3);
        double bot = AvgLuma(g, 80 / 3, 40);
        Assert.True(bot > mid, $"нижняя треть {bot:F1} не ярче средней {mid:F1}");
        Assert.True(mid > top, $"средняя треть {mid:F1} не ярче верхней {top:F1}");
    }

    [Fact]
    public void BottomRowHasHotYellowWhiteCells()
    {
        var s = SceneContract.Make(() => new DoomFireScene(), 120, 40, 42);
        SceneContract.Run(s, 180);
        var g = SceneContract.Snap(s, 120, 40);

        int hot = 0;
        for (int x = 0; x < g.Cols; x++)
        {
            var c = g[x, g.Rows - 1];
            if (IsHot(c.Fg) || IsHot(c.Bg)) hot++;
        }
        Assert.True(hot > 0, "в нижнем ряду нет почти белых/жёлтых ячеек");
    }

    [Fact]
    public void UpperPartIsMostlyBlackOn120x40() =>
        Assert.InRange(DarkRowShare(120, 40), 0.35, 0.65);

    [Fact]
    public void UpperPartIsMostlyBlackOn240x70() =>
        Assert.InRange(DarkRowShare(240, 70), 0.35, 0.65);

    [Fact]
    public void AsciiModeUsesOnlyRampCharacters()
    {
        var o = new SceneOptions { Params = JsonDocument.Parse("{\"mode\":\"ascii\"}").RootElement };
        var s = SceneContract.Make(() => new DoomFireScene(), 120, 40, 43, o);
        SceneContract.Run(s, 180);
        var g = SceneContract.Snap(s, 120, 40);
        Assert.All(g.Cells, c => Assert.True(AsciiRamp.IndexOf(c.Ch) >= 0, $"символ вне палитры ascii: U+{(int)c.Ch:X4}"));
    }

    [Fact]
    public void UpdateAndDrawAre_Fast_On240x70()
    {
        var s = SceneContract.Make(() => new DoomFireScene(), 240, 70, 31);
        var g = new CharGrid(240, 70);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 600; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
        }
        sw.Stop();
        _out.WriteLine($"600 кадров Update+Draw 240x70: {sw.Elapsed.TotalMilliseconds:F1} мс");
        Assert.True(sw.ElapsedMilliseconds < 600, $"600 кадров заняли {sw.ElapsedMilliseconds} мс");
    }

    /// <summary>Доля пиксель-строк (из 2×rows), где средняя яркость почти ноль, через 6 с симуляции.</summary>
    private static double DarkRowShare(int cols, int rows)
    {
        var s = SceneContract.Make(() => new DoomFireScene(), cols, rows, 51);
        SceneContract.Run(s, 360);
        var g = SceneContract.Snap(s, cols, rows);

        int dark = 0;
        for (int y = 0; y < rows; y++)
        {
            long top = 0, bottom = 0;
            for (int x = 0; x < cols; x++)
            {
                top += Luma(g[x, y].Fg);
                bottom += Luma(g[x, y].Bg);
            }
            if (top / (double)cols < 12) dark++;
            if (bottom / (double)cols < 12) dark++;
        }
        return dark / (double)(rows * 2);
    }

    private static bool IsHot(Rgba c) => c.R > 200 && c.G > 150;

    private static double AvgLuma(CharGrid g, int y0, int y1)
    {
        double sum = 0;
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = 0; x < g.Cols; x++)
            {
                var c = g[x, y];
                sum += Luma(c.Fg) + Luma(c.Bg);
                n += 2;
            }
        return sum / n;
    }

    private static int Luma(Rgba c) => c.R + c.G + c.B;
}
