using System.Text;
using Phosphor.Core;

namespace Phosphor.Scenes.Tests;

public sealed class C64LoadSceneTests
{
    private const int Cols = 120, Rows = 40;
    // Центральный экран 40×25 при сетке 120×40 (1 символ = 1 ячейка).
    private const int Sx = (Cols - 40) / 2, Sy = (Rows - 25) / 2;
    private static readonly Rgba BlueScreen = RetroPalettes.C64[6];

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new C64LoadScene(), "c64load");

    [Fact]
    public void ReadyAppearsWithinOneSecond()
    {
        var s = Make(21);
        SceneContract.Run(s, 60);
        var g = SceneContract.Snap(s, Cols, Rows);
        Assert.Contains("READY.", Text(g));
    }

    [Fact]
    public void LoadCommandAndLoadingAppearWithin20Seconds()
    {
        var s = Make(22);
        var g = new CharGrid(Cols, Rows);
        bool cmd = false, loading = false;
        for (int i = 0; i < 20 * 60 && !(cmd && loading); i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
            string t = Text(g);
            cmd |= t.Contains("LOAD\"*\",8,1");
            loading |= t.Contains("LOADING");
        }
        Assert.True(cmd, "за 20 с не появилась команда LOAD\"*\",8,1");
        Assert.True(loading, "за 20 с не появилась надпись LOADING");
    }

    [Fact]
    public void BorderColorsChangeBetweenFramesWhileLoading()
    {
        var s = Make(23);
        var g = new CharGrid(Cols, Rows);
        WaitForLoading(s, g);

        var seen = new HashSet<Rgba>();
        Rgba prev = default;
        int changes = 0;
        for (int i = 0; i < 90; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
            var c = g[0, 0].Bg;
            seen.Add(c);
            if (i > 0 && c != prev) changes++;
            prev = c;
        }
        Assert.True(changes >= 5, $"цвет рамки менялся только {changes} раз за 90 кадров");
        Assert.True(seen.Count >= 3, $"в рамке только {seen.Count} цвет(а/ов) во время загрузки");
    }

    [Fact]
    public void InnerScreenIsAlwaysC64Blue()
    {
        var s = Make(24);
        var g = new CharGrid(Cols, Rows);
        for (int i = 0; i < 20 * 60; i++)
        {
            s.Update(SceneContract.Dt);
            if (i % 7 != 0) continue;
            s.Draw(g);
            for (int y = Sy; y < Sy + 25; y++)
                for (int x = Sx; x < Sx + 40; x++)
                    Assert.True(g[x, y].Bg == BlueScreen, $"кадр {i}: ячейка ({x},{y}) фон {g[x, y].Bg}, ожидался синий C64");
        }
    }

    [Fact]
    public void BeatSyncedFlashesAreStrongerRightAfterTheBeat()
    {
        var s = Make(25);
        var g = new CharGrid(Cols, Rows);
        const double dt = 1.0 / 60, beat = 0.5;

        // Сначала дожидаемся загрузки (без проверок), звук уже идёт.
        int frame = 0;
        bool loading = false;
        for (int i = 0; i < 20 * 60 && !loading; i++, frame++)
        {
            Feed(s, frame, dt, beat);
            s.Update((float)dt);
            s.Draw(g);
            loading = Text(g).Contains("LOADING");
        }
        Assert.True(loading, "загрузка не началась за 20 с");

        // Затем измеряем долю изменившихся ячеек рамки по фазе относительно удара.
        var prev = Snapshot(g);
        double afterSum = 0, quietSum = 0;
        int afterN = 0, quietN = 0;
        for (int i = 0; i < 3 * 60; i++, frame++)
        {
            Feed(s, frame, dt, beat);
            s.Update((float)dt);
            s.Draw(g);
            var cur = Snapshot(g);
            double ratio = ChangedShare(prev, cur, g);
            prev = cur;

            double t = (frame + 1) * dt;
            double phase = t - Math.Floor(t / beat) * beat;
            if (phase < 0.15) { afterSum += ratio; afterN++; }
            else if (phase >= 0.25 && phase < 0.40) { quietSum += ratio; quietN++; }
        }
        double after = afterSum / Math.Max(1, afterN), quiet = quietSum / Math.Max(1, quietN);
        Assert.True(after >= 1.3 * quiet, $"доля изменений 0–150 мс {after:F3}, 250–400 мс {quiet:F3}");
    }

    [Fact]
    public void BorderFillsEveryCellOutsideScreenOnAnyGrid()
    {
        // Незаполненная ячейка сетки: Fg белый, Bg чёрный. Заполненная рамка: Fg == Bg и цвет из палитры C64
        // (чёрный из палитры — законный цвет полосы, поэтому проверяем не «не чёрный», а «заполнено»).
        foreach (var (cols, rows) in new[] { (192, 60), (99, 31), (99, 33), (120, 29) })
        {
            var s = new C64LoadScene();
            s.Init(cols, rows, new Random(28), new SceneOptions());
            var g = new CharGrid(cols, rows);
            int sx = (cols - 40) / 2, sy = (rows - 25) / 2;
            Assert.True(sy >= 0 && sy + 25 <= rows && sx >= 0 && sx + 40 <= cols, $"экран не влезает в {cols}×{rows}");
            int frame = 0;
            foreach (int target in new[] { 60, 900, 1200 })
            {
                for (; frame < target; frame++) s.Update(SceneContract.Dt);
                s.Draw(g);
                for (int y = 0; y < rows; y++)
                    for (int x = 0; x < cols; x++)
                    {
                        var c = g[x, y];
                        bool inside = x >= sx && x < sx + 40 && y >= sy && y < sy + 25;
                        if (inside)
                        {
                            Assert.True(c.Bg == BlueScreen, $"{cols}×{rows}, кадр {target}: экран ({x},{y}) не синий");
                        }
                        else
                        {
                            Assert.True(c.Fg == c.Bg && Array.IndexOf(RetroPalettes.C64, c.Bg) >= 0,
                                $"{cols}×{rows}, кадр {target}: ячейка рамки ({x},{y}) не заполнена: fg {c.Fg}, bg {c.Bg}");
                        }
                    }
            }
        }
    }

    [Fact]
    public void TimeBudgetOn192x60()
    {
        var s = new C64LoadScene();
        s.Init(192, 60, new Random(26), new SceneOptions());
        var g = new CharGrid(192, 60);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 600; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
        }
        sw.Stop();
        Assert.True(sw.Elapsed.TotalSeconds < 1.5, $"600 кадров заняли {sw.Elapsed.TotalSeconds:F2} с");
    }

    [Fact]
    public void SameSeedGivesSameScreens()
    {
        var a = Make(27);
        var b = Make(27);
        var ga = new CharGrid(Cols, Rows);
        var gb = new CharGrid(Cols, Rows);
        for (int i = 0; i < 900; i++)
        {
            a.Update(SceneContract.Dt);
            b.Update(SceneContract.Dt);
            if (i % 30 != 0) continue;
            a.Draw(ga);
            b.Draw(gb);
            Assert.Equal(ga.Cells, gb.Cells);
        }
    }

    private static C64LoadScene Make(int seed)
    {
        var s = new C64LoadScene();
        s.Init(Cols, Rows, new Random(seed), new SceneOptions());
        return s;
    }

    /// <summary>Гонит кадры, пока на экране не появится LOADING (не более 20 с).</summary>
    private static void WaitForLoading(C64LoadScene s, CharGrid g)
    {
        for (int i = 0; i < 20 * 60; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
            if (Text(g).Contains("LOADING")) return;
        }
        Assert.Fail("загрузка не началась за 20 с");
    }

    /// <summary>Синтетический звук: Active, Bpm 120, удар раз в 0.5 с ровно в одном кадре.</summary>
    private static void Feed(C64LoadScene s, int frame, double dt, double beat)
    {
        double t0 = frame * dt, t1 = (frame + 1) * dt;
        bool isBeat = Math.Floor(t1 / beat) > Math.Floor(t0 / beat);
        s.OnAudio(new AudioFrame(true, 0.6f, 0.8f, 0.5f, 0.3f, isBeat, isBeat ? 1f : 0f, 120f));
    }

    private static Rgba[] Snapshot(CharGrid g)
    {
        var r = new Rgba[g.Cells.Length];
        for (int i = 0; i < r.Length; i++) r[i] = g.Cells[i].Bg;
        return r;
    }

    /// <summary>Доля ячеек вне внутреннего экрана, у которых фон изменился между кадрами.</summary>
    private static double ChangedShare(Rgba[] a, Rgba[] b, CharGrid g)
    {
        int changed = 0, total = 0;
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Cols; x++)
            {
                if (x >= Sx && x < Sx + 40 && y >= Sy && y < Sy + 25) continue;
                int i = y * g.Cols + x;
                total++;
                if (a[i] != b[i]) changed++;
            }
        return changed / (double)total;
    }

    /// <summary>Текст сетки построчно, символы как есть.</summary>
    private static string Text(CharGrid g)
    {
        var sb = new StringBuilder();
        for (int y = 0; y < g.Rows; y++)
        {
            for (int x = 0; x < g.Cols; x++) sb.Append(g[x, y].Ch);
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
