using System.Diagnostics;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class TorusSceneTests
{
    private const string Ramp = ".,-~:;=!*#$@";
    private const string CaptionChars = "TORUS R1=.0123456789AB";

    private readonly ITestOutputHelper _out;

    public TorusSceneTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new TorusScene(), "torus");

    [Fact]
    public void AfterOneSecond_FillsSomeCells_UsesOnlyRampAndCaption()
    {
        var s = SceneContract.Make(() => new TorusScene(), 120, 40, 41);
        SceneContract.Run(s, 60);
        var g = SceneContract.Snap(s, 120, 40);

        int filled = 0;
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Cols; x++)
            {
                char c = g[x, y].Ch;
                if (c == ' ') continue;
                filled++;
                bool inRamp = Ramp.Contains(c);
                bool inCaption = y == g.Rows - 2 && CaptionChars.Contains(c);
                Assert.True(inRamp || inCaption, $"лишний символ '{c}' в ({x},{y})");
            }

        double share = filled / (double)g.Cells.Length;
        Assert.InRange(share, 0.10, 0.60);
    }

    [Fact]
    public void WithinThreeSeconds_TorusIsBigInSomeOrientation()
    {
        // Тор вращается: за 3 с берём максимум размеров bbox (ориентация может быть ребровой).
        var s = SceneContract.Make(() => new TorusScene(), 120, 40, 42);
        var g = new CharGrid(120, 40);
        int maxW = 0, maxH = 0;
        for (int i = 0; i < 180; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
            var (w, h) = BoundsSize(g);
            maxW = Math.Max(maxW, w);
            maxH = Math.Max(maxH, h);
        }
        Assert.True(maxW >= 0.4 * g.Cols, $"максимальная ширина тора {maxW} < 40% от {g.Cols}");
        Assert.True(maxH >= 0.4 * g.Rows, $"максимальная высота тора {maxH} < 40% от {g.Rows}");
    }

    [Fact]
    public void DumpLargeGridForHoleCheck()
    {
        var s = SceneContract.Make(() => new TorusScene(), 480, 140, 46);
        SceneContract.Run(s, 120);
        var g = SceneContract.Snap(s, 480, 140);
        var path = SceneContract.Dump(g, "torus-480x140");
        _out.WriteLine(path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void AspectRatioStaysNearSquareInTenSeconds()
    {
        var s = SceneContract.Make(() => new TorusScene(), 120, 40, 43);
        var g = new CharGrid(120, 40);
        for (int i = 0; i < 600; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
            var (w, h) = BoundsSize(g);
            double ratio = (w * 10.0) / (h * 18.0);
            Assert.InRange(ratio, 0.65, 2.2);
        }
    }

    [Fact]
    public void BeatBrightensNextFrames()
    {
        var quiet = SceneContract.Make(() => new TorusScene(), 120, 40, 44);
        var beat = SceneContract.Make(() => new TorusScene(), 120, 40, 44);
        SceneContract.Run(quiet, 60);
        SceneContract.Run(beat, 60);
        ((IAudioReactive)beat).OnAudio(Beat(0f));

        var gq = new CharGrid(120, 40);
        var gb = new CharGrid(120, 40);
        double sumQuiet = 0, sumBeat = 0;
        for (int i = 0; i < 5; i++)
        {
            quiet.Update(SceneContract.Dt);
            beat.Update(SceneContract.Dt);
            quiet.Draw(gq);
            beat.Draw(gb);
            sumQuiet += AverageFgLuma(gq);
            sumBeat += AverageFgLuma(gb);
        }

        Assert.True(sumBeat > sumQuiet,
            $"удар не усилил яркость: без удара {sumQuiet / 5:F1}, с ударом {sumBeat / 5:F1}");
    }

    [Fact]
    public void UpdateAndDrawAre_Fast_On240x70()
    {
        var s = SceneContract.Make(() => new TorusScene(), 240, 70, 31);
        var g = new CharGrid(240, 70);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 600; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
        }
        sw.Stop();
        _out.WriteLine($"600 кадров Update+Draw 240x70: {sw.Elapsed.TotalMilliseconds:F1} мс");
        Assert.True(sw.ElapsedMilliseconds < 900, $"600 кадров заняли {sw.ElapsedMilliseconds} мс");
    }

    [Fact]
    public void DumpForVisualCheck()
    {
        var s = SceneContract.Make(() => new TorusScene(), 120, 40, 45);
        SceneContract.Run(s, 120); // 2 с
        var g = SceneContract.Snap(s, 120, 40);
        var path = SceneContract.Dump(g, "torus");
        _out.WriteLine(path);
        Assert.True(File.Exists(path));
    }

    /// <summary>Размер bbox непробельных символов в ячейках, без строки подписи.</summary>
    private static (int w, int h) BoundsSize(CharGrid g)
    {
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        for (int y = 0; y < g.Rows; y++)
        {
            if (y == g.Rows - 2) continue;
            for (int x = 0; x < g.Cols; x++)
            {
                if (g[x, y].Ch == ' ') continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < minX) return (0, 0);
        return (maxX - minX + 1, maxY - minY + 1);
    }

    private static double AverageFgLuma(CharGrid g)
    {
        double sum = 0;
        int n = 0;
        foreach (var c in g.Cells)
        {
            if (c.Ch == ' ') continue;
            sum += c.Fg.R + c.Fg.G + c.Fg.B;
            n++;
        }
        return n == 0 ? 0 : sum / n;
    }

    private static AudioFrame Beat(float bass) =>
        new(Active: true, Level: 0.5f, Bass: bass, Mid: 0f, High: 0f, Beat: true, BeatPulse: 1f, Bpm: 120f);
}
