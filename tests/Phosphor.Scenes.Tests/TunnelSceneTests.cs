using System.Diagnostics;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class TunnelSceneTests
{
    private readonly ITestOutputHelper _out;

    public TunnelSceneTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new TunnelScene(), "tunnel");

    [Fact]
    public void NoMusicIsAlive()
    {
        var s = SceneContract.Make(() => new TunnelScene(), 120, 40, 81);
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
        var s = SceneContract.Make(() => new TunnelScene(), cols, rows, 61);
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
        var s = SceneContract.Make(() => new TunnelScene(), 80, 25, 71);
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
    public void DepthRingsAlongRadius()
    {
        var s = SceneContract.Make(() => new TunnelScene(), 160, 50, 91);
        SceneContract.Run(s, 300);
        var g = SceneContract.Snap(s, 160, 50);
        var (cx, cy) = Centre(g);
        float half = MathF.Min(g.Cols, g.Rows * 2) * 0.5f;

        // Луч от центра к левому краю; яркость по пикселям, бинаризация по среднему.
        var samples = new List<float>();
        for (float r = 0f; r < half * 0.95f; r += 0.5f) samples.Add(Luma(g, cx - r, cy));
        int transitions = Transitions(samples);
        _out.WriteLine($"чередований светлое/тёмное вдоль радиуса: {transitions}");
        Assert.True(transitions >= 4, $"вдоль радиуса только {transitions} чередований, кольца не читаются");
    }

    [Fact]
    public void DepthRingsAlongCircumference()
    {
        var s = SceneContract.Make(() => new TunnelScene(), 160, 50, 92);
        SceneContract.Run(s, 300);
        var g = SceneContract.Snap(s, 160, 50);
        var (cx, cy) = Centre(g);
        float half = MathF.Min(g.Cols, g.Rows * 2) * 0.5f;

        // Окружность на 60% полуразмера (вне тумана), 720 отсчётов.
        var samples = new List<float>();
        float r = 0.6f * half;
        for (int i = 0; i < 720; i++)
        {
            float a = i * MathF.Tau / 720f;
            samples.Add(Luma(g, cx + r * MathF.Cos(a), cy + r * MathF.Sin(a)));
        }
        int transitions = Transitions(samples);
        _out.WriteLine($"чередований светлое/тёмное по окружности: {transitions}");
        Assert.True(transitions >= 4, $"по окружности только {transitions} чередований");
    }

    /// <summary>Центр туннеля — центроид самых тёмных клеток (там туман уводит всё в чёрный). Координаты в пикселях холста.</summary>
    private static (float x, float y) Centre(CharGrid g)
    {
        var lum = new float[g.Cells.Length];
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < lum.Length; i++)
        {
            lum[i] = (CellLuma(g.Cells[i].Fg) + CellLuma(g.Cells[i].Bg)) * 0.5f;
            min = MathF.Min(min, lum[i]); max = MathF.Max(max, lum[i]);
        }
        float limit = min + (max - min) * 0.05f;
        double sx = 0, sy = 0; int n = 0;
        for (int y = 0; y < g.Rows; y++)
        for (int x = 0; x < g.Cols; x++)
        {
            if (lum[y * g.Cols + x] > limit) continue;
            sx += x + 0.5; sy += 2 * (y + 0.5); n++;
        }
        return n == 0 ? (g.Cols * 0.5f, g.Rows * 1f) : ((float)(sx / n), (float)(sy / n));
    }

    private static float CellLuma(Rgba c) => c.R * 0.299f + c.G * 0.587f + c.B * 0.114f;

    /// <summary>Яркость ячейки, под которой лежит точка холста (x — колонка, y — пиксель холста, 2 пикселя на строку).</summary>
    private static float Luma(CharGrid g, float px, float py)
    {
        int x = Math.Clamp((int)MathF.Floor(px), 0, g.Cols - 1);
        int row = Math.Clamp((int)MathF.Floor(py / 2f), 0, g.Rows - 1);
        var c = g[x, row];
        return (CellLuma(c.Fg) + CellLuma(c.Bg)) * 0.5f;
    }

    /// <summary>Число смен светлое/тёмное в последовательности (порог — среднее между min и max).</summary>
    private static int Transitions(List<float> v)
    {
        float min = v.Min(), max = v.Max();
        float thr = (min + max) * 0.5f;
        int n = 0;
        for (int i = 1; i < v.Count; i++)
            if ((v[i] > thr) != (v[i - 1] > thr)) n++;
        return n;
    }

    [Fact]
    public void UpdateAndDrawAre_Fast_On192x60()
    {
        var s = SceneContract.Make(() => new TunnelScene(), 192, 60, 31);
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
