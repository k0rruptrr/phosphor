using System.Diagnostics;
using System.Text;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class CracktroSceneTests
{
    private readonly ITestOutputHelper _out;

    public CracktroSceneTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new CracktroScene(), "cracktro");

    [Fact]
    public void GreetingsPhraseScrollsInFromOutsideTheFirstFrame()
    {
        // Фраза стоит за пределами первых 120 символов буфера: на кадре 0 её нет, потом въезжает.
        const int cols = 120, rows = 40;
        const string phrase = "NO SPRITES WERE HARMED";
        var s = SceneContract.Make(() => new CracktroScene(), cols, rows, 41);
        var grid = new CharGrid(cols, rows);
        s.Draw(grid);
        Assert.DoesNotContain(phrase, ScrollerText(grid));

        int frame = -1;
        string last = "";
        for (int i = 1; i <= 300 && frame < 0; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(grid);
            last = ScrollerText(grid);
            if (last.Contains(phrase, StringComparison.Ordinal)) frame = i;
        }
        _out.WriteLine($"фраза въехала на кадре {frame} ({frame / 60.0:F2} с)");
        Assert.True(frame > 0, "за 5 с в нижней трети не въехала фраза «NO SPRITES WERE HARMED»");
    }

    [Fact]
    public void ScrollerWaveKeepsAdjacentLettersWithinOneRow()
    {
        const int cols = 120, rows = 40;
        var s = SceneContract.Make(() => new CracktroScene(), cols, rows, 45);
        SceneContract.Run(s, 300);
        var grid = SceneContract.Snap(s, cols, rows);
        var r = ScrollerRows(grid);

        int pairs = 0;
        for (int x = 0; x + 1 < cols; x++)
        {
            if (r[x] < 0 || r[x + 1] < 0) continue;
            pairs++;
            Assert.True(Math.Abs(r[x] - r[x + 1]) <= 1,
                $"соседние буквы в колонках {x} и {x + 1} на рядах {r[x]} и {r[x + 1]}: волна «лесенкой»");
        }
        _out.WriteLine($"соседних пар букв: {pairs}");
        Assert.True(pairs > 50, "в скроллере мало соседних букв для проверки волны");
    }

    [Fact]
    public void FakeNewsArrivesAfterOnDataAndScrollsIn()
    {
        const int cols = 120, rows = 40;
        const string title = "ТЕСТОВЫЙ ЗАГОЛОВОК 42";
        var s = SceneContract.Make(() => new CracktroScene(), cols, rows, 42);
        SceneContract.Run(s, 60); // 1 с без новостей

        var items = new[] { new NewsItem("TEST", title, null, "ru") };
        ((IDataScene)s).OnData("news", new NewsSnapshot(items, DateTimeOffset.UtcNow, false));

        var grid = new CharGrid(cols, rows);
        s.Draw(grid);
        Assert.DoesNotContain(title, ScrollerText(grid));

        int frame = -1;
        for (int i = 1; i <= 3600 && frame < 0; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(grid);
            if (ScrollerText(grid).Contains(title, StringComparison.Ordinal)) frame = i;
        }
        Assert.True(frame > 0, "фейковый заголовок не въехал в скроллер");
        float tAppear = (60 + frame) / 60f;
        _out.WriteLine($"заголовок въехал на кадре {frame} после OnData, сцена-время {tAppear:F2} с");
        Assert.True(tAppear > 1f, $"заголовок появился на {tAppear:F2} с, ожидалось позже 1 с");
    }

    [Fact]
    public void CopperBarsShowSeveralShadesAndQuantizedColors()
    {
        const int cols = 120, rows = 40;
        var s = SceneContract.Make(() => new CracktroScene(), cols, rows, 43);
        SceneContract.Run(s, 90);
        var g = SceneContract.Snap(s, cols, rows);

        var shades = new HashSet<Rgba>();
        for (int y = rows / 3; y < 2 * rows / 3; y++)
            for (int x = 0; x < cols; x++)
            {
                AddIfSaturated(shades, g[x, y].Fg);
                AddIfSaturated(shades, g[x, y].Bg);
            }
        _out.WriteLine($"оттенков в средней части: {shades.Count}");
        Assert.True(shades.Count >= 4, $"в средней части {shades.Count} оттенков полос, ожидалось ≥ 4");

        foreach (var c in g.Cells)
        {
            Assert.True(c.Fg.R % 17 == 0 && c.Fg.G % 17 == 0 && c.Fg.B % 17 == 0, $"Fg {c.Fg} не квантован под Amiga12");
            Assert.True(c.Bg.R % 17 == 0 && c.Bg.G % 17 == 0 && c.Bg.B % 17 == 0, $"Bg {c.Bg} не квантован под Amiga12");
        }
    }

    [Fact]
    public void BeatChangesMoreCellsRightAfterTheHitThanLater()
    {
        const int cols = 120, rows = 40;
        const float dt = 1f / 60f;
        var s = new CracktroScene();
        s.Init(cols, rows, new Random(44), new SceneOptions());

        var prev = new CharGrid(cols, rows);
        var cur = new CharGrid(cols, rows);
        var early = new List<double>();
        var late = new List<double>();

        // Удар каждые 0.5 с ровно в одном кадре (каждые 30 кадров при 60 Гц); BeatPulse затухает за ~150 мс.
        for (int i = 0; i < 60 * 8; i++)
        {
            int since = i % 30;
            float tsince = since * dt;
            float pulse = MathF.Exp(-tsince / 0.15f);
            bool beat = since == 0;
            s.OnAudio(new AudioFrame(true, 0.5f, 0.7f * pulse, 0.3f, 0.3f, beat, pulse, 120f));
            s.Update(dt);
            s.Draw(cur);

            if (i >= 60)
            {
                double frac = ChangedFraction(prev, cur);
                float ms = tsince * 1000f;
                if (ms < 150f) early.Add(frac);
                else if (ms >= 250f && ms < 400f) late.Add(frac);
            }
            (prev, cur) = (cur, prev);
        }

        double e = early.Average(), l = late.Average();
        _out.WriteLine($"доля изменившихся ячеек: 0–150 мс {e:P2}, 250–400 мс {l:P2}, отношение {e / l:F2}");
        Assert.True(e >= 1.3 * l, $"после удара изменений {e:P2}, позже {l:P2}: отношение {e / l:F2} < 1.3");
    }

    [Fact]
    public void SixHundredFramesOn192x60AreFast()
    {
        var s = SceneContract.Make(() => new CracktroScene(), 192, 60, 31);
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

    /// <summary>Строка скроллера: для каждой колонки первая непробельная ячейка в нижней трети (вертикальный сдвиг не важен).</summary>
    private static string ScrollerText(CharGrid g)
    {
        int zoneTop = g.Rows - Math.Max(1, g.Rows / 3);
        var sb = new StringBuilder(g.Cols);
        for (int x = 0; x < g.Cols; x++)
        {
            char ch = ' ';
            for (int y = zoneTop; y < g.Rows; y++)
            {
                if (g[x, y].Ch != ' ') { ch = g[x, y].Ch; break; }
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Ряд первой непробельной ячейки в каждой колонке нижней трети (-1 — пусто).</summary>
    private static int[] ScrollerRows(CharGrid g)
    {
        int zoneTop = g.Rows - Math.Max(1, g.Rows / 3);
        var rows = new int[g.Cols];
        for (int x = 0; x < g.Cols; x++)
        {
            rows[x] = -1;
            for (int y = zoneTop; y < g.Rows; y++)
            {
                if (g[x, y].Ch != ' ') { rows[x] = y; break; }
            }
        }
        return rows;
    }

    private static double ChangedFraction(CharGrid a, CharGrid b)
    {
        int changed = 0;
        for (int i = 0; i < a.Cells.Length; i++)
        {
            var p = a.Cells[i];
            var q = b.Cells[i];
            if (p.Ch != q.Ch || p.Fg != q.Fg || p.Bg != q.Bg) changed++;
        }
        return changed / (double)a.Cells.Length;
    }

    private static void AddIfSaturated(HashSet<Rgba> set, Rgba c)
    {
        if (c.R != c.G || c.G != c.B) set.Add(c);
    }
}
