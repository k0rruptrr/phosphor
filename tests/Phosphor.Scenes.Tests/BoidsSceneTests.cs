using System.Diagnostics;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class BoidsSceneTests
{
    private readonly ITestOutputHelper _out;

    public BoidsSceneTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new BoidsScene(), "boids");

    [Fact]
    public void FlockFormsClustersAndShowsArrows()
    {
        const int cols = 120, rows = 40;
        const int n = 120 * 40 / 60; // 80 агентов
        var s = SceneContract.Make(() => new BoidsScene(), cols, rows, 41);
        SceneContract.Run(s, 600); // 10 с
        var g = SceneContract.Snap(s, cols, rows);

        int arrows = g.Cells.Count(c => IsArrow(c.Ch));
        Assert.True(arrows >= n / 2, $"стрелок на экране {arrows}, ожидалось >= {n / 2}");

        var boids = (BoidsScene)s;
        Assert.InRange(boids.ClusterCount, 1, n / 3);
    }

    [Fact]
    public void LocalAlignmentEmergesAfter15Seconds()
    {
        var s = (BoidsScene)SceneContract.Make(() => new BoidsScene(), 120, 40, 42);
        SceneContract.Run(s, 900); // 15 с
        float a = s.LocalAlignment();
        _out.WriteLine($"LocalAlignment через 15 с: {a:F3}");
        Assert.True(a > 0.8f, $"локальное выравнивание {a:F3}, ожидалось > 0.8");
    }

    [Fact]
    public void FlockIsDenseAndAlignedAfter30Seconds()
    {
        var s = (BoidsScene)SceneContract.Make(() => new BoidsScene(), 192, 60, 47);
        SceneContract.Run(s, 1800); // 30 с
        float a = s.LocalAlignment();
        _out.WriteLine($"через 30 с на 192x60: CLUSTERS {s.ClusterCount}, LocalAlignment {a:F3}");
        Assert.True(s.ClusterCount <= 8, $"групп {s.ClusterCount}, ожидалось <= 8");
        Assert.True(a > 0.85f, $"локальное выравнивание {a:F3}, ожидалось > 0.85");
    }

    [Fact]
    public void PredatorAppearsWithin120Seconds()
    {
        var s = (BoidsScene)SceneContract.Make(() => new BoidsScene(), 120, 40, 43);
        bool seen = false;
        for (int i = 0; i < 120 * 60 && !seen; i++)
        {
            s.Update(SceneContract.Dt);
            if (s.PredatorActive) seen = true;
        }
        Assert.True(seen, "хищник не появился за 120 с");
    }

    [Fact]
    public void CaptureFrameAndTargetLabelAppear()
    {
        var s = SceneContract.Make(() => new BoidsScene(), 120, 40, 44);
        var g = new CharGrid(120, 40);
        bool corner = false, label = false;
        for (int i = 0; i < 20 * 60 && !(corner && label); i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
            if (!corner) corner = g.Cells.Any(c => c.Ch == '┌');
            if (!label) label = HasText(g, "TGT-01");
        }
        Assert.True(corner, "уголок рамки захвата '┌' не появился");
        Assert.True(label, "подпись «TGT-01» не появилась");
    }

    [Fact]
    public void UpdateAndDrawAre_Fast_On240x70()
    {
        var s = SceneContract.Make(() => new BoidsScene(), 240, 70, 45);
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
        var s = SceneContract.Make(() => new BoidsScene(), 120, 40, 46);
        SceneContract.Run(s, 600);
        var g = SceneContract.Snap(s, 120, 40);
        var path = SceneContract.Dump(g, "boids");
        _out.WriteLine($"Дамп: {path}");
    }

    private static bool IsArrow(char c) => c is '►' or '◄' or '▲' or '▼' or '/' or '\\';

    private static bool HasText(CharGrid g, string text)
    {
        for (int y = 0; y < g.Rows; y++)
        {
            for (int x = 0; x + text.Length <= g.Cols; x++)
            {
                bool ok = true;
                for (int k = 0; k < text.Length && ok; k++) ok = g[x + k, y].Ch == text[k];
                if (ok) return true;
            }
        }
        return false;
    }
}
