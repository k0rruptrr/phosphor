using System.Diagnostics;
using System.Text.Json;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class SortingSceneTests
{
    private readonly ITestOutputHelper _out;

    public SortingSceneTests(ITestOutputHelper output) => _out = output;

    private static SceneOptions Opts(string json) => new() { Params = JsonDocument.Parse(json).RootElement };

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new SortingScene(), "sorting");

    [Fact]
    public void NoMusicIsAlive()
    {
        var s = SceneContract.Make(() => new SortingScene(), 120, 40, 81);
        SceneContract.Run(s, 60);
        var a = SceneContract.Snap(s, 120, 40);
        SceneContract.Run(s, 180);                       // 3 с без музыки
        var b = SceneContract.Snap(s, 120, 40);
        double share = ChangedShare(a, b);
        _out.WriteLine($"без музыки за 3 с изменилось {share:P1} ячеек");
        Assert.True(share > 0.05, $"за 3 с изменилось лишь {share:P1} ячеек");
    }

    [Fact]
    public void Fast_On192x60()
    {
        var s = SceneContract.Make(() => new SortingScene(), 192, 60, 31);
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
    public void BeatSpeedsUpStepsLockedToTheBeat()
    {
        const int cols = 160, rows = 50, beatEvery = 30; // 0.5 с при 60 кадрах/с
        var s = (SortingScene)SceneContract.Make(() => new SortingScene(), cols, rows, 61);
        SceneContract.Run(s, 120);                         // 2 с: перемешивание прошло, идёт сортировка
        var audio = (IAudioReactive)s;
        double early = 0, late = 0;
        int nEarly = 0, nLate = 0;

        for (int f = 0; f < 10 * beatEvery; f++)
        {
            int phase = f % beatEvery;
            float sinceMs = phase * 1000f / 60f;
            float pulse = MathF.Exp(-sinceMs / 150f);
            audio.OnAudio(new AudioFrame(Active: true, Level: 0.5f, Bass: 0.7f * pulse, Mid: 0f, High: 0f,
                Beat: phase == 0, BeatPulse: pulse, Bpm: 120f));
            bool sorting = s.Sorting;
            int before = s.Steps;
            s.Update(SceneContract.Dt);
            if (!sorting || !s.Sorting) continue;          // считаем только кадры внутри сортировки
            double steps = s.Steps - before;
            if (sinceMs < 150f) { early += steps; nEarly++; }
            else if (sinceMs >= 250f && sinceMs < 400f) { late += steps; nLate++; }
        }

        double e = early / nEarly, l = late / nLate;
        _out.WriteLine($"шагов за кадр: 0–150 мс после удара {e:F2}, 250–400 мс {l:F2}");
        Assert.True(nEarly > 0 && nLate > 0, "нет кадров внутри сортировки");
        Assert.True(e >= 1.5 * l, $"темп не привязан к удару: {e:F2} против {l:F2}");
    }

    [Fact]
    public void EveryAlgorithmSortsTheArray()
    {
        var s = (SortingScene)SceneContract.Make(() => new SortingScene(), 120, 40, 7);
        foreach (SortingScene.Algo a in Enum.GetValues(typeof(SortingScene.Algo)))
        {
            int n = a == SortingScene.Algo.Bogo ? 6 : 128;
            Assert.True(s.RunAlgorithmToEnd(a, n), $"{a} не отсортировал {n} элементов");
            Assert.True(s.RunAlgorithmToEnd(a, n == 6 ? 6 : 37), $"{a} не отсортировал 37 элементов");
        }
    }

    [Fact]
    public void ColumnsAreMonotoneAfterSort()
    {
        // 128 элементов на 192 колонках: слот в 1 колонку, поле по центру (левый отступ 32).
        const int cols = 192, rows = 40, n = 128;
        var s = (SortingScene)SceneContract.Make(() => new SortingScene(), cols, rows, 9);
        Assert.True(s.RunAlgorithmToEnd(SortingScene.Algo.Quick, n));
        var g = SceneContract.Snap(s, cols, rows);
        int left = (cols - n) / 2;

        var heights = new int[n];
        for (int e = 0; e < n; e++)
            for (int y = 3; y <= rows - 3; y++)
                if (g[left + e, y].Ch != ' ') heights[e]++;

        _out.WriteLine($"высота первого столбика {heights[0]}, последнего {heights[n - 1]}, поле с x={left}");
        for (int e = 1; e < n; e++)
            Assert.True(heights[e] >= heights[e - 1], $"столбик {e} ниже предыдущего: {heights[e]} < {heights[e - 1]}");
        Assert.True(heights[n - 1] - heights[0] >= (rows - 5) / 2, "после сортировки нет заметного возрастания столбиков");
    }

    [Fact]
    public void BarsHaveGapsAndAreCentred()
    {
        // 32 элемента на 192 колонках: слот 6, столбик 5 колонок и зазор 1; поле занимает всю ширину.
        const int cols = 192, rows = 40, n = 32;
        var s = (SortingScene)SceneContract.Make(() => new SortingScene(), cols, rows, 10);
        Assert.True(s.RunAlgorithmToEnd(SortingScene.Algo.Insertion, n));
        var g = SceneContract.Snap(s, cols, rows);
        int slot = cols / n, left = (cols - n * slot) / 2;

        bool Filled(int x) { for (int y = 3; y <= rows - 3; y++) if (g[x, y].Ch != ' ') return true; return false; }
        for (int e = 0; e < n; e++)
        {
            int x0 = left + e * slot;
            Assert.True(Filled(x0), $"столбик {e} пустой в колонке {x0}");
            Assert.False(Filled(x0 + slot - 1), $"у столбика {e} нет зазора в колонке {x0 + slot - 1}");
        }
        _out.WriteLine($"слот {slot}, поле с x={left} по {left + n * slot - 1}, зазоры есть");
        int right = cols - (left + n * slot);
        Assert.True(Math.Abs(left - right) <= 1, $"поле не по центру: слева {left}, справа {right}");
    }

    [Fact]
    public void CountersInHeaderMatchAndGrow()
    {
        const int cols = 160, rows = 40;
        var s = SceneContract.Make(() => new SortingScene(), cols, rows, 13);
        var sorting = (SortingScene)s;
        SceneContract.Run(s, 60);
        int c1 = ReadHeaderValue(SceneContract.Snap(s, cols, rows), "COMPARES ");
        int w1 = ReadHeaderValue(SceneContract.Snap(s, cols, rows), "SWAPS ");
        SceneContract.Run(s, 240);
        var g = SceneContract.Snap(s, cols, rows);
        int c2 = ReadHeaderValue(g, "COMPARES ");
        int w2 = ReadHeaderValue(g, "SWAPS ");
        _out.WriteLine($"COMPARES {c1} -> {c2}, SWAPS {w1} -> {w2}");
        Assert.Equal(sorting.Compares, c2);
        Assert.Equal(sorting.Swaps, w2);
        Assert.True(c2 > c1 && w2 > w1, "счётчики не растут");
    }

    [Fact]
    public void QuickSortOf128TakesAboutTenSeconds()
    {
        var s = SceneContract.Make(() => new SortingScene(), 120, 40, 17, Opts("{\"algorithms\":\"fast\",\"size\":128}"));
        var sorting = (SortingScene)s;
        int guard = 0;
        while (!sorting.Sorting && guard++ < 3600) s.Update(SceneContract.Dt);   // перемешивание
        Assert.True(sorting.Sorting, "сортировка не началась");
        Assert.Equal(SortingScene.Algo.Quick, sorting.CurrentAlgo);

        int frames = 0;
        while (sorting.Sorting && frames < 60 * 60) { s.Update(SceneContract.Dt); frames++; }
        float sec = frames / 60f;
        _out.WriteLine($"быстрая сортировка 128: {sec:F1} с");
        Assert.InRange(sec, 10f, 15f);
    }

    [Theory]
    [InlineData("{\"size\":32}", "{\"size\":256}")]
    [InlineData("{\"speed\":0.2}", "{\"speed\":5}")]
    [InlineData("{\"algorithms\":\"all\"}", "{\"algorithms\":\"slow\"}")]
    public void MinAndMaxParamsChangePicture(string min, string max)
    {
        var a = Render(min, 120, 40, 120);
        var b = Render(max, 120, 40, 120);
        double share = ChangedShare(a, b);
        _out.WriteLine($"{min} против {max}: различаются {share:P1} ячеек");
        Assert.True(share > 0.02, $"картинки почти одинаковы: {share:P1}");
    }

    private static CharGrid Render(string json, int cols, int rows, int frames)
    {
        var s = SceneContract.Make(() => new SortingScene(), cols, rows, 22, Opts(json));
        SceneContract.Run(s, frames);
        return SceneContract.Snap(s, cols, rows);
    }

    /// <summary>Читает число после подписи в строке 0 (ячейка за ячейкой, без парсинга картинки целиком).</summary>
    private static int ReadHeaderValue(CharGrid g, string label)
    {
        var row = new string(Enumerable.Range(0, g.Cols).Select(x => g[x, 0].Ch).ToArray());
        int at = row.IndexOf(label, StringComparison.Ordinal);
        Assert.True(at >= 0, $"в строке нет подписи «{label}»: {row.TrimEnd()}");
        int i = at + label.Length, v = 0;
        while (i < row.Length && row[i] is >= '0' and <= '9') v = v * 10 + (row[i++] - '0');
        return v;
    }

    private static double ChangedShare(CharGrid a, CharGrid b)
    {
        int n = 0;
        for (int i = 0; i < a.Cells.Length; i++)
            if (a.Cells[i].Ch != b.Cells[i].Ch || a.Cells[i].Fg != b.Cells[i].Fg || a.Cells[i].Bg != b.Cells[i].Bg) n++;
        return n / (double)a.Cells.Length;
    }
}
