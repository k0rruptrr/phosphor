using System.Diagnostics;
using System.Text.Json;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class TopoSceneTests
{
    private readonly ITestOutputHelper _out;

    public TopoSceneTests(ITestOutputHelper output) => _out = output;

    private static SceneOptions Opts(string json) => new() { Params = JsonDocument.Parse(json).RootElement };

    private static TopoScene Make(int cols, int rows, int seed, string json = "{}")
    {
        var s = new TopoScene();
        s.Init(cols, rows, new Random(seed), Opts(json));
        return s;
    }

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new TopoScene(), "topo");

    [Fact]
    public void NoMusicIsAlive()
    {
        var s = SceneContract.Make(() => new TopoScene(), 120, 40, 81);
        SceneContract.Run(s, 60);
        var a = SceneContract.Snap(s, 120, 40);
        SceneContract.Run(s, 180);                       // 3 с без музыки
        var b = SceneContract.Snap(s, 120, 40);
        double share = ChangedShare(a, b);
        _out.WriteLine($"без музыки за 3 с изменилось {share:P0} ячеек");
        Assert.True(share > 0.2, $"за 3 с изменилось лишь {share:P0} ячеек");
    }

    [Fact]
    public void Fast_On192x60()
    {
        var s = SceneContract.Make(() => new TopoScene(), 192, 60, 31);
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
    public void SmallerIntervalGivesMoreContourCells()
    {
        var fine = Make(120, 40, 11, "{\"interval\":10}");
        var coarse = Make(120, 40, 11, "{\"interval\":50}");
        SceneContract.Run(fine, 60);
        SceneContract.Run(coarse, 60);
        int nFine = ContourCells(fine), nCoarse = ContourCells(coarse);
        _out.WriteLine($"ячеек-изолиний: interval=10 → {nFine}, interval=50 → {nCoarse}");
        Assert.True(nFine > 1.5 * nCoarse, $"interval=10 дал {nFine} ячеек, interval=50 — {nCoarse}");
    }

    [Fact]
    public void LabelsSitOnMajorContours()
    {
        var s = Make(192, 60, 21);
        var g = new CharGrid(192, 60);
        int found = 0;
        for (int frame = 0; frame < 300; frame++)
        {
            s.Update(SceneContract.Dt);
            if (frame % 30 != 0) continue;
            s.Draw(g);
            foreach (var (x, y, value) in s.Labels)
            {
                found++;
                int step = s.Interval;
                Assert.Equal(0, value % (5 * step));               // подпись только у жирной изолинии
                string digits = value.ToString();
                for (int k = 0; k < digits.Length; k++)
                    Assert.Equal(digits[k], g[x + k, y].Ch);      // цифры нарисованы в разрыве
                // Рядом с подписью есть точки и выше, и ниже уровня (±interval).
                float lo = float.MaxValue, hi = float.MinValue;
                for (int dy = 0; dy < 3; dy++)
                for (int dx = 0; dx < 2; dx++)
                {
                    float h = s.HeightAtSub(2 * x + dx, 3 * y + dy);
                    lo = MathF.Min(lo, h);
                    hi = MathF.Max(hi, h);
                }
                Assert.True(lo < value + step && hi > value - step,
                    $"подпись {value} у ячейки ({x},{y}): высоты {lo:F0}..{hi:F0}");
            }
        }
        _out.WriteLine($"проверено подписей: {found}");
        Assert.True(found > 0, "подписи высот не появились ни разу");
    }

    [Fact]
    public void ContourLevelIsCrossedOncePerGradientRay()
    {
        // Подъём по самому крутому соседу строго возрастает, и каждый уровень изолинии встречается на нём один раз:
        // значит изолинии разных уровней не пересекаются и не сливаются на одном луче.
        var s = Make(160, 50, 41);
        SceneContract.Run(s, 120);
        int step = s.Interval;
        int rays = 0;
        for (int sy = 3; sy < s.SubHeight - 3 && rays < 12; sy += 9)
        for (int sx = 3; sx < s.SubWidth - 3 && rays < 12; sx += 13)
        {
            var seq = Ascend(s, sx, sy, 200);
            if (seq.Count < 6) continue;
            rays++;
            for (int i = 1; i < seq.Count; i++)
                Assert.True(seq[i] > seq[i - 1], $"подъём не монотонен: {seq[i - 1]:F1} → {seq[i]:F1}");
            // Уровни не убывают, значит каждый уровень — один непрерывный отрезок луча.
            var levels = seq.Select(h => (int)(h / step)).ToList();
            for (int i = 1; i < levels.Count; i++)
                Assert.True(levels[i] >= levels[i - 1], $"уровень убыл на подъёме: {levels[i - 1]} → {levels[i]}");
        }
        _out.WriteLine($"проверено лучей подъёма: {rays}");
        Assert.True(rays >= 3, $"нашлось лишь {rays} лучей подъёма");
    }

    [Fact]
    public void ProfileShowsUnderTheCursor()
    {
        var s = Make(120, 40, 51);
        SceneContract.Run(s, 30);
        s.ForceProfileAt(40, 60);
        var g = new CharGrid(120, 40);
        s.Draw(g);
        _out.WriteLine($"ячеек профиля с рисунком: {s.ProfileBitsSet}");
        Assert.True(s.ProfileBitsSet > 20, $"профиль почти пуст: {s.ProfileBitsSet} ячеек");
        Assert.Contains("PROFILE", string.Concat(Enumerable.Range(0, 120).Select(x => g[x, 40 - 9 - 3].Ch)));
    }

    [Theory]
    [InlineData("interval", "{\"interval\":10}", "{\"interval\":50}")]
    [InlineData("drift", "{\"drift\":0}", "{\"drift\":3}")]
    [InlineData("shading", "{\"shading\":false}", "{\"shading\":true}")]
    public void EveryParamMinAndMaxDrawDifferently(string name, string min, string max)
    {
        var a = Make(120, 40, 61, min);
        var b = Make(120, 40, 61, max);
        SceneContract.Run(a, 120);
        SceneContract.Run(b, 120);
        var ga = SceneContract.Snap(a, 120, 40);
        var gb = SceneContract.Snap(b, 120, 40);
        double share = ChangedShare(ga, gb);
        _out.WriteLine($"{name}: Min и Max различаются в {share:P0} ячеек");
        Assert.True(share > 0.05, $"{name}: Min и Max почти не различаются ({share:P1})");
    }

    [Fact]
    public void RiversStartHighAndOnlyGoDown()
    {
        foreach (int seed in new[] { 1, 2, 3, 4, 5 })
        {
            var s = Make(160, 50, seed);
            var paths = s.RiverPaths;
            _out.WriteLine($"seed {seed}: рек {paths.Count}");
            Assert.InRange(paths.Count, 2, 4);
            foreach (var path in paths)
            {
                Assert.True(path.Length > 10, "река слишком короткая");
                for (int i = 1; i < path.Length; i++)
                {
                    float prev = s.TexelHeight(path[i - 1]), cur = s.TexelHeight(path[i]);
                    Assert.True(cur <= prev, $"seed {seed}: река поднимается на шаге {i}: {prev:F1} → {cur:F1}");
                }
            }
        }
    }

    [Fact]
    public void PeaksAreProminentAndSpaced()
    {
        foreach (int seed in new[] { 1, 2, 3 })
        {
            var s = Make(160, 50, seed);
            int n = s.PeakCount;
            _out.WriteLine($"seed {seed}: вершин {n}");
            for (int i = 0; i < n; i++)
            {
                var (xi, yi) = s.PeakAt(i);
                float prom = s.PeakProminenceOf(i);
                Assert.True(prom >= 2f * s.Interval, $"seed {seed}: вершина {i} превышает кольцо лишь на {prom:F0} м");
                for (int j = i + 1; j < n; j++)
                {
                    var (xj, yj) = s.PeakAt(j);
                    float dx = MathF.Abs(xi - xj), dy = MathF.Abs(yi - yj);
                    dx = MathF.Min(dx, 512 - dx); dy = MathF.Min(dy, 512 - dy);
                    Assert.True(MathF.Sqrt(dx * dx + dy * dy) >= 60f, $"seed {seed}: вершины {i} и {j} ближе 60 текселей");
                }
            }
        }
    }

    [Fact]
    public void CoordinateLabelsAreAllDifferent()
    {
        var s = Make(120, 40, 71);
        SceneContract.Run(s, 300);
        var g = new CharGrid(120, 40);
        s.Draw(g);
        // Широта: левый край, строки через 10 (без строки заголовка и подвала).
        var lat = new List<string>();
        for (int y = 2; y < 40 - 2; y += 10)
            lat.Add(string.Concat(Enumerable.Range(0, 7).Select(x => g[x, y].Ch)));
        // Долгота: подвал, колонки через 24.
        var lon = new List<string>();
        for (int x = 9; x + 7 < 120; x += 24)
            lon.Add(string.Concat(Enumerable.Range(x, 7).Select(xx => g[xx, 39].Ch)));
        _out.WriteLine("широта: " + string.Join(" | ", lat) + "  долгота: " + string.Join(" | ", lon));
        Assert.Equal(lat.Count, lat.Distinct().Count());
        Assert.Equal(lon.Count, lon.Distinct().Count());
    }

    [Fact]
    public void ThinLinesDoNotTurnIntoRipple()
    {
        // На крутых склонах тонкие изолинии пропускаются: доля ячеек-линий на экране не должна быть сплошной.
        var s = Make(160, 50, 81);
        SceneContract.Run(s, 300);
        int lines = 0;
        for (int y = 0; y < 50; y++)
        for (int x = 0; x < 160; x++)
            if (s.CellKind(x, y) is 1 or 2) lines++;
        double share = lines / (double)(160 * 50);
        _out.WriteLine($"доля ячеек-линий: {share:P0}");
        Assert.True(share < 0.6, $"линии заняли {share:P0} экрана — муар");
    }

    /// <summary>Подъём по самому крутому соседу в субпиксельной сетке.</summary>
    private static List<float> Ascend(TopoScene s, int sx, int sy, int maxSteps)
    {
        var seq = new List<float>();
        float cur = s.HeightAtSub(sx, sy);
        seq.Add(cur);
        for (int n = 0; n < maxSteps; n++)
        {
            int bx = -1, by = -1;
            float best = cur;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = sx + dx, ny = sy + dy;
                if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= s.SubWidth || ny >= s.SubHeight) continue;
                float h = s.HeightAtSub(nx, ny);
                if (h > best) { best = h; bx = nx; by = ny; }
            }
            if (bx < 0) break;
            sx = bx; sy = by; cur = best;
            seq.Add(cur);
        }
        return seq;
    }

    private static int ContourCells(TopoScene s) => Enumerable.Range(0, 120 * 40)
        .Count(i => s.CellKind(i % 120, i / 120) is 1 or 2);

    private static double ChangedShare(CharGrid a, CharGrid b)
    {
        int n = 0;
        for (int i = 0; i < a.Cells.Length; i++)
            if (a.Cells[i].Ch != b.Cells[i].Ch || a.Cells[i].Fg != b.Cells[i].Fg || a.Cells[i].Bg != b.Cells[i].Bg) n++;
        return n / (double)a.Cells.Length;
    }
}
