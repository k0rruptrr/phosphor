using System.Diagnostics;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class RotozoomSceneTests
{
    private readonly ITestOutputHelper _out;

    public RotozoomSceneTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new RotozoomScene(), "rotozoom");

    [Fact]
    public void NoMusicIsAlive()
    {
        var s = SceneContract.Make(() => new RotozoomScene(), 120, 40, 81);
        SceneContract.Run(s, 60);
        var a = SceneContract.Snap(s, 120, 40);
        SceneContract.Run(s, 180);                       // 3 с без музыки
        var b = SceneContract.Snap(s, 120, 40);
        double share = ChangedShare(a, b);
        _out.WriteLine($"без музыки за 3 с изменилось {share:P0} ячеек");
        Assert.True(share > 0.5, $"за 3 с изменилось лишь {share:P0} ячеек");
    }

    [Fact]
    public void BeatReactionIsLockedToTheBeat()
    {
        const int cols = 160, rows = 50, beatEvery = 30; // 0.5 с при 60 кадрах/с
        var s = SceneContract.Make(() => new RotozoomScene(), cols, rows, 61);
        SceneContract.Run(s, 120);
        var audio = (IAudioReactive)s;
        var prev = SceneContract.Snap(s, cols, rows);
        var cur = new CharGrid(cols, rows);
        double early = 0, late = 0;
        int nEarly = 0, nLate = 0;

        for (int f = 0; f < 10 * beatEvery; f++)
        {
            int phase = f % beatEvery;
            float sinceMs = phase * 1000f / 60f;
            float pulse = MathF.Exp(-sinceMs / 150f);
            audio.OnAudio(new AudioFrame(Active: true, Level: 0.5f, Bass: 0.7f * pulse, Mid: 0f, High: 0f,
                Beat: phase == 0, BeatPulse: pulse, Bpm: 120f));
            s.Update(SceneContract.Dt);
            s.Draw(cur);
            double share = ChangedShare(prev, cur);
            if (sinceMs < 150f) { early += share; nEarly++; }
            else if (sinceMs >= 250f && sinceMs < 400f) { late += share; nLate++; }
            prev = Copy(cur);
        }

        double e = early / nEarly, l = late / nLate;
        _out.WriteLine($"доля изменившихся ячеек: 0–150 мс после удара {e:P1}, 250–400 мс {l:P1}");
        Assert.True(e >= 1.3 * l, $"реакция не привязана к удару: {e:P2} против {l:P2}");
    }

    [Fact]
    public void PaletteIsAmiga12Steps()
    {
        var s = SceneContract.Make(() => new RotozoomScene(), 80, 25, 71);
        SceneContract.Run(s, 300);
        var g = SceneContract.Snap(s, 80, 25);
        var colors = g.Cells.SelectMany(c => new[] { c.Fg, c.Bg }).Distinct().ToArray();
        Assert.True(colors.Length >= 2, "в кадре меньше двух цветов");
        Assert.All(colors, c =>
        {
            Assert.Equal(0, c.R % 17);
            Assert.Equal(0, c.G % 17);
            Assert.Equal(0, c.B % 17);
        });
    }

    [Fact]
    public void UpdateAndDrawAre_Fast_On192x60()
    {
        var s = SceneContract.Make(() => new RotozoomScene(), 192, 60, 31);
        var g = new CharGrid(192, 60);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 600; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
        }
        sw.Stop();
        _out.WriteLine($"600 кадров Update+Draw 192x60: {sw.Elapsed.TotalMilliseconds:F1} мс");
        Assert.True(sw.ElapsedMilliseconds < 1500, $"600 кадров заняли {sw.ElapsedMilliseconds} мс");
    }

    private static double ChangedShare(CharGrid a, CharGrid b)
    {
        int n = 0;
        for (int i = 0; i < a.Cells.Length; i++)
            if (a.Cells[i].Fg != b.Cells[i].Fg || a.Cells[i].Bg != b.Cells[i].Bg) n++;
        return n / (double)a.Cells.Length;
    }

    private static CharGrid Copy(CharGrid src)
    {
        var g = new CharGrid(src.Cols, src.Rows);
        Array.Copy(src.Cells, g.Cells, src.Cells.Length);
        return g;
    }
}
