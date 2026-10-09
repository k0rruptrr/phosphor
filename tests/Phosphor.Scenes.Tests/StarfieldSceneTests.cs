using System.Diagnostics;
using System.Text.Json;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class StarfieldSceneTests
{
    private readonly ITestOutputHelper _out;

    public StarfieldSceneTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new StarfieldScene(), "starfield");

    [Fact]
    public void FillsSomeButNotMostCellsAfter2s()
    {
        var s = SceneContract.Make(() => new StarfieldScene(), 160, 50, 11);
        SceneContract.Run(s, 120);
        var g = SceneContract.Snap(s, 160, 50);
        double share = g.Cells.Count(c => c.Ch != ' ') / (double)g.Cells.Length;
        Assert.InRange(share, 0.005, 0.25);
    }

    [Fact]
    public void StarsFlyOutwardFromCenter()
    {
        var o = NoNebula();

        var early = SceneContract.Make(() => new StarfieldScene(), 160, 50, 12, o);
        SceneContract.Run(early, 6); // 0.1 с
        double dEarly = MeanDistance(SceneContract.Snap(early, 160, 50));

        var late = SceneContract.Make(() => new StarfieldScene(), 160, 50, 12, o);
        SceneContract.Run(late, 180); // 3 с
        double dLate = MeanDistance(SceneContract.Snap(late, 160, 50));

        _out.WriteLine($"средняя дистанция: 0.1 с = {dEarly:F2}, 3 с = {dLate:F2}");
        Assert.True(dLate >= dEarly, $"звёзды не разлетаются: {dEarly:F2} -> {dLate:F2}");
    }

    [Fact]
    public void BeatAddsStreaksInFirstTenFrames()
    {
        var o = NoNebula();
        var quiet = SceneContract.Make(() => new StarfieldScene(), 160, 50, 9, o);
        var beat = SceneContract.Make(() => new StarfieldScene(), 160, 50, 9, o);
        ((IAudioReactive)beat).OnAudio(Beat(0f));

        var gq = new CharGrid(160, 50);
        var gb = new CharGrid(160, 50);
        int quietStreaks = 0, beatStreaks = 0;
        for (int i = 0; i < 10; i++)
        {
            quiet.Update(SceneContract.Dt);
            quiet.Draw(gq);
            quietStreaks += CountStreaks(gq);

            beat.Update(SceneContract.Dt);
            beat.Draw(gb);
            beatStreaks += CountStreaks(gb);
        }

        _out.WriteLine($"символов шлейфов за 10 кадров: без удара {quietStreaks}, с ударом {beatStreaks}");
        Assert.True(beatStreaks > quietStreaks, $"удар не добавил шлейфов: {quietStreaks} -> {beatStreaks}");
    }

    [Fact]
    public void UpdateAndDrawAre_Fast_On240x70()
    {
        var s = SceneContract.Make(() => new StarfieldScene(), 240, 70, 31);
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

    private static SceneOptions NoNebula() =>
        new() { Params = JsonDocument.Parse("{\"nebula\":false}").RootElement };

    private static AudioFrame Beat(float bass) =>
        new(Active: true, Level: 0.5f, Bass: bass, Mid: 0f, High: 0f, Beat: true, BeatPulse: 1f, Bpm: 120f);

    /// <summary>Средняя дистанция непробельных ячеек от центра (в ячейках, с поправкой на высоту ячейки).</summary>
    private static double MeanDistance(CharGrid g)
    {
        double cx = g.Cols / 2.0, cy = g.Rows / 2.0, sum = 0;
        int n = 0;
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Cols; x++)
            {
                if (g[x, y].Ch == ' ') continue;
                double dx = x + 0.5 - cx, dy = (y + 0.5 - cy) / 0.55;
                sum += Math.Sqrt(dx * dx + dy * dy);
                n++;
            }
        return n == 0 ? 0 : sum / n;
    }

    private static int CountStreaks(CharGrid g)
    {
        int n = 0;
        foreach (var c in g.Cells)
            if (c.Ch is '─' or '│' or '/' or '\\') n++;
        return n;
    }
}
