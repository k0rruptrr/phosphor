using System.Diagnostics;
using System.Text.Json;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class MatrixSceneTests
{
    private readonly ITestOutputHelper _out;

    public MatrixSceneTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new MatrixScene(), "matrix");

    [Fact]
    public void DensityFillsSomeButNotAllCells()
    {
        var s = SceneContract.Make(() => new MatrixScene(), 120, 40, 11);
        SceneContract.Run(s, 300);
        var g = SceneContract.Snap(s, 120, 40);
        double share = g.Cells.Count(c => c.Ch != ' ') / (double)g.Cells.Length;
        Assert.InRange(share, 0.10, 0.70);
    }

    [Fact]
    public void ShowsKatakana()
    {
        var s = SceneContract.Make(() => new MatrixScene(), 120, 40, 11);
        SceneContract.Run(s, 300);
        var g = SceneContract.Snap(s, 120, 40);
        Assert.Contains(g.Cells, c => c.Ch >= 'ｦ' && c.Ch <= 'ﾝ');
    }

    [Fact]
    public void StreamHeadIsBrightestAndTailFadesUpward()
    {
        // Одна колонка, density 0, один удар: ровно одна струя, голова видна (через 1.5 с она в пределах экрана).
        var o = new SceneOptions { Params = JsonDocument.Parse("{\"density\":0}").RootElement };
        var s = SceneContract.Make(() => new MatrixScene(), 1, 60, 23, o);
        ((IAudioReactive)s).OnAudio(Beat(0f));
        SceneContract.Run(s, 90);
        var g = SceneContract.Snap(s, 1, 60);

        int bottom = -1;
        for (int y = 0; y < 60; y++) if (g[0, y].Ch != ' ') bottom = y;
        Assert.True(bottom >= 0, "струя не видна");
        int top = bottom;
        while (top > 0 && g[0, top - 1].Ch != ' ') top--;
        Assert.True(bottom - top >= 2, $"хвост слишком короткий: {top}..{bottom}");

        for (int y = top + 1; y <= bottom; y++)
            Assert.True(Luma(g[0, y].Fg) >= Luma(g[0, y - 1].Fg),
                $"строка {y} тусклее строки {y - 1}: хвост должен гаснуть вверх");
        Assert.True(Luma(g[0, bottom].Fg) > Luma(g[0, top].Fg), "голова не ярче конца хвоста");
    }

    [Fact]
    public void ZeroDensityLeavesScreenEmpty()
    {
        var o = new SceneOptions { Params = JsonDocument.Parse("{\"density\":0}").RootElement };
        var s = SceneContract.Make(() => new MatrixScene(), 120, 40, 12, o);
        SceneContract.Run(s, 300);
        var g = SceneContract.Snap(s, 120, 40);
        Assert.All(g.Cells, c => Assert.Equal(' ', c.Ch));
    }

    [Fact]
    public void SpeedTwiceAsFastHalvesLifetime()
    {
        double t1 = StreamLifetime(1f);
        double t2 = StreamLifetime(2f);
        double ratio = t1 / t2;
        Assert.InRange(ratio, 1.9, 2.1);
    }

    [Fact]
    public void BeatSpawnsNewStreams()
    {
        var o = new SceneOptions { Params = JsonDocument.Parse("{\"density\":0}").RootElement };

        var quiet = SceneContract.Make(() => new MatrixScene(), 60, 40, 22, o);
        SceneContract.Run(quiet, 120);
        Assert.Equal(0, FilledColumns(SceneContract.Snap(quiet, 60, 40)));

        var beat = SceneContract.Make(() => new MatrixScene(), 60, 40, 22, o);
        ((IAudioReactive)beat).OnAudio(Beat(0f));
        SceneContract.Run(beat, 120); // 2 с: головы успевают войти на экран
        int cols = FilledColumns(SceneContract.Snap(beat, 60, 40));
        Assert.InRange(cols, 3, 6);
    }

    [Fact]
    public void UpdateAndDrawAre_Fast_On240x70()
    {
        var s = SceneContract.Make(() => new MatrixScene(), 240, 70, 31);
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

    /// <summary>Время от удара до полного ухода струи с экрана (одна колонка, одна струя).</summary>
    private static double StreamLifetime(float speed)
    {
        var o = new SceneOptions { Speed = speed, Params = JsonDocument.Parse("{\"density\":0}").RootElement };
        var s = SceneContract.Make(() => new MatrixScene(), 1, 100, 21, o);
        ((IAudioReactive)s).OnAudio(Beat(0f));

        var g = new CharGrid(1, 100);
        const float dt = 1f / 240f;
        bool seen = false;
        for (int i = 0; i < 240 * 60; i++)
        {
            s.Update(dt);
            s.Draw(g);
            bool any = g.Cells.Any(c => c.Ch != ' ');
            if (any) seen = true;
            else if (seen) return (i + 1) * dt;
        }
        Assert.Fail("струя не ушла с экрана за 60 с");
        return 0;
    }

    private static AudioFrame Beat(float bass) =>
        new(Active: true, Level: 0.5f, Bass: bass, Mid: 0f, High: 0f, Beat: true, BeatPulse: 1f, Bpm: 120f);

    private static int FilledColumns(CharGrid g)
    {
        int n = 0;
        for (int x = 0; x < g.Cols; x++)
            for (int y = 0; y < g.Rows; y++)
                if (g[x, y].Ch != ' ') { n++; break; }
        return n;
    }

    private static int Luma(Rgba c) => c.R + c.G + c.B;
}
