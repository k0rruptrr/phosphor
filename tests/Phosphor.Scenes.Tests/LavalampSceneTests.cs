using System.Diagnostics;
using System.Text.Json;
using Phosphor.Core;
using Phosphor.Scenes;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class LavalampSceneTests
{
    private readonly ITestOutputHelper _out;

    public LavalampSceneTests(ITestOutputHelper output) => _out = output;

    private static SceneOptions Opts(string json) => new() { Params = JsonDocument.Parse(json).RootElement };

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new LavalampScene(), "lavalamp");

    [Fact]
    public void NoMusicIsAlive()
    {
        var s = SceneContract.Make(() => new LavalampScene(), 120, 40, 81);
        SceneContract.Run(s, 60);
        var a = SceneContract.Snap(s, 120, 40);
        SceneContract.Run(s, 180);                       // 3 с без музыки
        var b = SceneContract.Snap(s, 120, 40);
        double share = ChangedShare(a, b);
        _out.WriteLine($"без музыки за 3 с изменилось {share:P1} ячеек");
        Assert.True(share > 0.02, $"за 3 с изменилось лишь {share:P1} ячеек");
    }

    [Fact]
    public void BeatReactionIsLockedToTheBeat()
    {
        const int cols = 160, rows = 50, beatEvery = 30; // 0.5 с при 60 кадрах/с
        var s = SceneContract.Make(() => new LavalampScene(), cols, rows, 61);
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
    public void UpdateAndDrawAre_Fast_On192x60()
    {
        var s = SceneContract.Make(() => new LavalampScene(), 192, 60, 31);
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

    [Fact]
    public void WaxStaysInsideTheFlask()
    {
        var s = (LavalampScene)SceneContract.Make(() => new LavalampScene(), 120, 40, 101);
        int insideMax = 0, outsideTotal = 0;
        for (int f = 0; f < 60 * 60; f++)
        {
            s.Update(SceneContract.Dt);
            if (f % 30 != 0) continue;
            for (int i = 0; i < s.BlobCount; i++)
                Assert.True(LavalampScene.InFlask(s.BlobX(i), s.BlobY(i)),
                    $"капля {i} вышла за колбу: x={s.BlobX(i):F3} y={s.BlobY(i):F3} на кадре {f}");
            var c = s.WaxPixelCounts();
            insideMax = Math.Max(insideMax, c.Inside);
            outsideTotal += c.Outside;
        }
        _out.WriteLine($"пикселей воска внутри колбы (максимум) {insideMax}, за контуром за 60 с {outsideTotal}");
        Assert.True(insideMax > 0, "воска в колбе не видно");
        Assert.Equal(0, outsideTotal);
    }

    [Fact]
    public void WaxFillsAboutAQuarterOfTheFlask()
    {
        var s = (LavalampScene)SceneContract.Make(() => new LavalampScene(), 120, 40, 105);
        double min = 1, max = 0, sum = 0;
        int samples = 0;
        for (int f = 0; f < 60 * 60; f++)
        {
            s.Update(SceneContract.Dt);
            if (f % 30 != 0) continue;
            var c = s.WaxPixelCounts();
            double share = c.Inside / (double)c.Flask;
            min = Math.Min(min, share); max = Math.Max(max, share); sum += share; samples++;
        }
        double avg = sum / samples;
        _out.WriteLine($"доля воска в колбе за 60 с: среднее {avg:P1}, от {min:P1} до {max:P1}");
        Assert.InRange(avg, 0.18, 0.27);
    }

    [Fact]
    public void PoolAlwaysLiesInTheBottomTenPercent()
    {
        var s = (LavalampScene)SceneContract.Make(() => new LavalampScene(), 120, 40, 106);
        int minBottom = int.MaxValue;
        for (int f = 0; f < 60 * 60; f++)
        {
            s.Update(SceneContract.Dt);
            if (f % 30 != 0) continue;
            minBottom = Math.Min(minBottom, s.WaxPixelCounts().BottomWax);
        }
        _out.WriteLine($"минимум воска в нижних 10% колбы за 60 с: {minBottom} пикселей");
        Assert.True(minBottom > 0, "в нижних 10% колбы иногда нет воска");
    }

    [Fact]
    public void EveryBlobVisitsBothTopAndBottomThirds()
    {
        var s = (LavalampScene)SceneContract.Make(() => new LavalampScene(), 120, 40, 102);
        int n = s.BlobCount;
        var (top, bottom) = LavalampScene.FlaskSpan;
        float third = (bottom - top) / 3f;
        float upLine = top + third, downLine = top + 2 * third;
        var atTop = new bool[n];
        var atBottom = new bool[n];
        for (int f = 0; f < 60 * 60; f++)
        {
            s.Update(SceneContract.Dt);
            for (int i = 0; i < n; i++)
            {
                if (s.BlobY(i) <= upLine) atTop[i] = true;
                if (s.BlobY(i) >= downLine) atBottom[i] = true;
            }
        }
        int both = 0;
        for (int i = 0; i < n; i++)
        {
            if (atTop[i] && atBottom[i]) both++;
            _out.WriteLine($"капля {i}: верхняя треть {atTop[i]}, нижняя треть {atBottom[i]}");
        }
        Assert.Equal(n, both);
    }

    [Fact]
    public void HeatMakesTheBlobsMoveMore()
    {
        float low = PathLength(0.2f), high = PathLength(2f);
        _out.WriteLine($"путь капель за 5 с: heat=0.2 {low:F3}, heat=2 {high:F3} единиц лампы");
        Assert.True(high > 2f * low, $"при heat=2 капли прошли {high:F3}, при 0.2 — {low:F3}");
    }

    private static float PathLength(float heat)
    {
        var s = (LavalampScene)SceneContract.Make(() => new LavalampScene(), 120, 40, 103,
            Opts($"{{\"heat\":{heat.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"));
        var px = new float[s.BlobCount];
        var py = new float[s.BlobCount];
        for (int i = 0; i < s.BlobCount; i++) { px[i] = s.BlobX(i); py[i] = s.BlobY(i); }
        float sum = 0;
        for (int f = 0; f < 300; f++)
        {
            s.Update(SceneContract.Dt);
            for (int i = 0; i < s.BlobCount; i++)
            {
                sum += MathF.Abs(s.BlobX(i) - px[i]) + MathF.Abs(s.BlobY(i) - py[i]);
                px[i] = s.BlobX(i); py[i] = s.BlobY(i);
            }
        }
        return sum;
    }

    [Fact]
    public void MinAndMaxOfEachParamDrawDifferentPictures()
    {
        foreach (var (name, lo, hi) in new[]
        {
            ("blobs", "{\"blobs\":3}", "{\"blobs\":6}"),
            ("heat", "{\"heat\":0.2}", "{\"heat\":2}"),
            ("scheme", "{\"scheme\":\"red\"}", "{\"scheme\":\"cycle\"}"),
        })
        {
            var a = Picture(lo);
            var b = Picture(hi);
            double share = ChangedShare(a, b);
            _out.WriteLine($"{name}: min и max отличаются в {share:P1} ячеек");
            Assert.True(share > 0.02, $"параметр {name}: min и max почти одинаковы ({share:P1})");
        }
    }

    private static CharGrid Picture(string json)
    {
        var s = SceneContract.Make(() => new LavalampScene(), 120, 40, 104, Opts(json));
        SceneContract.Run(s, 120);
        return SceneContract.Snap(s, 120, 40);
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
