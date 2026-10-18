using System.Diagnostics;
using System.Text.Json;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class EkgSceneTests
{
    private readonly ITestOutputHelper _out;

    public EkgSceneTests(ITestOutputHelper output) => _out = output;

    private static readonly Rgba EcgCol = Rgba.Hex(0x39FF6A);
    private static readonly Rgba PlethCol = Rgba.Hex(0x3AD8FF);

    private static SceneOptions Opts(string json) => new() { Params = JsonDocument.Parse(json).RootElement.Clone() };

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new EkgScene(), "ekg");

    [Fact]
    public void NoMusicIsAlive()
    {
        var s = SceneContract.Make(() => new EkgScene(), 120, 40, 81);
        SceneContract.Run(s, 60);
        var a = SceneContract.Snap(s, 120, 40);
        SceneContract.Run(s, 180);                       // 3 с без музыки
        var b = SceneContract.Snap(s, 120, 40);
        double share = ChangedShare(a, b);
        _out.WriteLine($"без музыки за 3 с изменилось {share:P1} ячеек");
        Assert.True(share > 0.02, $"за 3 с изменилось лишь {share:P1} ячеек");
    }

    [Fact]
    public void UpdateAndDrawAre_Fast_On192x60()
    {
        var s = SceneContract.Make(() => new EkgScene(), 192, 60, 31);
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
    public void RPeakCountOnScreenMatchesHeartRate()
    {
        var s = SceneContract.Make(() => new EkgScene(), 192, 60, 101, Opts("{\"events\":0}"));
        SceneContract.Run(s, 1000);                      // 16 с: экран (6 с при 25 мм/с) полностью заполнен
        var g = SceneContract.Snap(s, 192, 60);
        var lay = Layout(192, 60);
        var t = Read(g, EcgCol, lay.LabelW, lay.Wpx, lay.EcgTop, lay.EcgRows);
        int peaks = RunStarts(t, EcgThr(lay)).Count;
        int hr = ((EkgScene)s).ShownHr;
        double expected = hr * 6.0 / 60.0;               // экран 150 мм при 25 мм/с = 6 с
        _out.WriteLine($"HR на экране {hr}, R-пиков на экране {peaks}, ожидалось {expected:F1}");
        Assert.True(Math.Abs(peaks - expected) <= 1.0, $"R-пиков {peaks}, ожидалось {expected:F1} ±1");
    }

    [Fact]
    public void PlethPeakComesAfterREachBeat()
    {
        var s = SceneContract.Make(() => new EkgScene(), 192, 60, 111, Opts("{\"events\":0}"));
        SceneContract.Run(s, 1000);
        var g = SceneContract.Snap(s, 192, 60);
        var lay = Layout(192, 60);
        var ecg = Read(g, EcgCol, lay.LabelW, lay.Wpx, lay.EcgTop, lay.EcgRows);
        var pleth = Read(g, PlethCol, lay.LabelW, lay.Wpx, lay.PlethTop, lay.PlethRows);
        var rRuns = RunsOf(ecg, EcgThr(lay));
        var pRuns = RunsOf(pleth, PlethThr(lay));
        double secPerPx = 150.0 / 25.0 / lay.Wpx;        // экран 150 мм при 25 мм/с

        var delays = new List<double>();
        foreach (var (ps, pe) in pRuns)
        {
            double pm = (ps + pe) / 2.0;
            (int rs, int re) best = (-1, -1);
            foreach (var r in rRuns)
            {
                double rm = (r.s + r.e) / 2.0;
                if (rm < pm && pm - rm <= 0.6 / secPerPx) best = r;
            }
            if (best.rs >= 0) delays.Add((pm - (best.rs + best.re) / 2.0) * secPerPx);
        }
        double mean = delays.Count == 0 ? 0 : delays.Average();
        _out.WriteLine($"пар R→PLETH {delays.Count}, средняя задержка {mean:F3} с");
        Assert.True(delays.Count >= 5, $"найдено только {delays.Count} пар R→PLETH");
        Assert.InRange(mean, 0.18, 0.32);
    }

    [Fact]
    public void LeadsOffGivesFlatEcgLine()
    {
        var s = SceneContract.Make(() => new EkgScene(), 192, 60, 121, Opts("{\"events\":0}"));
        SceneContract.Run(s, 600);
        var lay = Layout(192, 60);
        var before = Read(SceneContract.Snap(s, 192, 60), EcgCol, lay.LabelW, lay.Wpx, lay.EcgTop, lay.EcgRows);
        int peaksBefore = RunStarts(before, EcgThr(lay)).Count;
        _out.WriteLine($"до LEADS OFF R-пиков на экране: {peaksBefore}");
        Assert.True(peaksBefore >= 5, "контроль: до эпизода на экране должны быть R-пики");

        ((EkgScene)s).ForceLeadsOff(15f);
        SceneContract.Run(s, 750);                       // 12.5 с: весь экран (6 с) записан при LEADS OFF
        var after = Read(SceneContract.Snap(s, 192, 60), EcgCol, lay.LabelW, lay.Wpx, lay.EcgTop, lay.EcgRows);
        int peaksAfter = RunStarts(after, EcgThr(lay)).Count;
        int tallest = 0;
        for (int px = 0; px < after.Top.Length; px++)
            if (after.Top[px] >= 0) tallest = Math.Max(tallest, after.Bot[px] - after.Top[px]);
        _out.WriteLine($"при LEADS OFF R-пиков {peaksAfter}, макс. высота штриха {tallest} px");
        Assert.Equal(0, peaksAfter);
        Assert.True(tallest <= 1, $"линия не ровная: штрих высотой {tallest} px");
    }

    [Fact]
    public void RPeaksAreLockedToMusicBeats()
    {
        var s = SceneContract.Make(() => new EkgScene(), 192, 60, 131, Opts("{\"events\":0}"));
        var audio = (IAudioReactive)s;
        var lay = Layout(192, 60);
        var g = new CharGrid(192, 60);
        SceneContract.Run(s, 300);                       // 5 с без музыки: ритм свободный

        const int beatEvery = 30;                        // 0.5 с при 60 кадрах/с, 120 BPM
        int checkedBeats = 0, worst = 0;
        void Feed(bool beat)
        {
            audio.OnAudio(new AudioFrame(Active: true, Level: 0.5f, Bass: 0.5f, Mid: 0f, High: 0f,
                Beat: beat, BeatPulse: beat ? 1f : 0f, Bpm: 120f));
            s.Update(SceneContract.Dt);
        }
        for (int k = 0; k < 10; k++)
        {
            Feed(true);
            Feed(false);                                 // два кадра (33 мс): R-пик на этот момент уже нарисован на головке
            Feed(false);
            s.Draw(g);
            var t = Read(g, EcgCol, lay.LabelW, lay.Wpx, lay.EcgTop, lay.EcgRows);
            int head = HeadOf(t);
            int dist = int.MaxValue;
            foreach (var (rs, re) in RunsOf(t, EcgThr(lay)))
            {
                int midPx = (head + 4 + (rs + re) / 2) % t.Wpx;   // отрезок идёт от старого к новому, переводим в колонку экрана
                int d = Math.Abs(midPx - head);
                d = Math.Min(d, t.Wpx - d);
                dist = Math.Min(dist, d);
            }
            worst = Math.Max(worst, dist);
            checkedBeats++;
            for (int f = 3; f < beatEvery; f++) Feed(false);
        }
        int shown = ((EkgScene)s).ShownHr;
        _out.WriteLine($"ударов проверено {checkedBeats}, худшее отклонение R от головки луча {worst} px, HR {shown}");
        Assert.Equal(10, checkedBeats);
        Assert.True(worst <= 2, $"R-пик не на ударе: отклонение {worst} px");
        Assert.InRange(shown, 118, 122);
    }

    [Theory]
    [InlineData("sweep", "{\"sweep\":12.5}", "{\"sweep\":50}")]
    [InlineData("events", "{\"events\":0}", "{\"events\":1}")]
    [InlineData("grid", "{\"grid\":false}", "{\"grid\":true}")]
    public void ParamMinAndMaxChangePicture(string name, string min, string max)
    {
        var a = SceneContract.Make(() => new EkgScene(), 120, 40, 141, Opts(min));
        var b = SceneContract.Make(() => new EkgScene(), 120, 40, 141, Opts(max));
        SceneContract.Run(a, 120);
        SceneContract.Run(b, 120);
        double diff = ChangedShare(SceneContract.Snap(a, 120, 40), SceneContract.Snap(b, 120, 40));
        _out.WriteLine($"{name}: Min и Max различаются в {diff:P1} ячеек через 120 кадров");
        Assert.True(diff > 0, $"{name}: Min и Max дают одинаковую картинку");
    }

    // ---- Разбор сетки ----

    private readonly record struct Lay(int LabelW, int Wpx, int EcgTop, int EcgRows, int PlethTop, int PlethRows);

    /// <summary>Раскладка как в сцене: подписи слева, панель справа, три полосы по высоте.</summary>
    private static Lay Layout(int cols, int rows)
    {
        int labelW = cols >= 60 ? 7 : 0;
        int panelW = cols >= 60 ? Math.Min(24, cols / 4) : 0;
        int waveCols = Math.Max(1, cols - labelW - (panelW > 0 ? panelW + 1 : 0));
        int h = Math.Max(3, rows - 2);
        int sep = h >= 30 ? 3 : h >= 12 ? 1 : 0;
        int ecgRows = Math.Max(1, (int)(h * 0.45));
        int plethRows = Math.Max(1, (int)(h * 0.25));
        return new Lay(labelW, 2 * waveCols, 2, ecgRows, 2 + ecgRows + sep, plethRows);
    }

    private static int EcgThr(Lay l) => (int)(0.4 * l.EcgRows * 3);
    private static int PlethThr(Lay l) => (int)(0.5 * l.PlethRows * 3);

    /// <summary>Полоса: верхняя и нижняя строка пикселей в каждой колонке экрана (-1 — пусто).</summary>
    private sealed record Trace(int Wpx, int[] Top, int[] Bot, int Head);

    private static Trace Read(CharGrid g, Rgba fg, int labelW, int wpx, int bandTop, int bandRows)
    {
        var top = new int[wpx];
        var bot = new int[wpx];
        Array.Fill(top, -1);
        Array.Fill(bot, -1);
        for (int cy = 0; cy < bandRows; cy++)
        for (int cx = 0; cx < wpx / 2; cx++)
        {
            if (bandTop + cy >= g.Rows || labelW + cx >= g.Cols) continue;
            var c = g[labelW + cx, bandTop + cy];
            if (!GlyphSet.IsSextant(c.Ch) || c.Fg != fg) continue;
            int bits = c.Ch - 0xE000;
            for (int dy = 0; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                if ((bits & (1 << (dy * 2 + dx))) == 0) continue;
                int px = cx * 2 + dx, y = cy * 3 + dy;
                top[px] = top[px] < 0 ? y : Math.Min(top[px], y);
                bot[px] = Math.Max(bot[px], y);
            }
        }
        return new Trace(wpx, top, bot, HeadOf(top, wpx));
    }

    private static int HeadOf(Trace t) => t.Head;

    /// <summary>Головка луча — последняя колонка перед тремя пустыми колонками зазора.</summary>
    private static int HeadOf(int[] top, int w)
    {
        for (int g = 0; g < w; g++)
            if (top[g % w] < 0 && top[(g + 1) % w] < 0 && top[(g + 2) % w] < 0)
                return (g - 1 + w) % w;
        return w - 1;
    }

    /// <summary>Отрезки колонок, где штрих поднимается выше порога (пики), по порядку от старого к новому.</summary>
    private static List<(int s, int e)> RunsOf(Trace t, int thr)
    {
        var res = new List<(int, int)>();
        int start = -1;
        for (int k = 0; k <= t.Wpx - 4; k++)
        {
            int px = (t.Head + 4 + k) % t.Wpx;
            bool high = t.Top[px] >= 0 && t.Top[px] < thr;
            if (high && start < 0) start = k;
            if (!high && start >= 0)
            {
                res.Add((start, k - 1));
                start = -1;
            }
        }
        if (start >= 0) res.Add((start, t.Wpx - 4));
        return res;
    }

    private static List<(int s, int e)> RunStarts(Trace t, int thr) => RunsOf(t, thr);

    private static double ChangedShare(CharGrid a, CharGrid b)
    {
        int n = 0;
        for (int i = 0; i < a.Cells.Length; i++)
            if (a.Cells[i].Fg != b.Cells[i].Fg || a.Cells[i].Bg != b.Cells[i].Bg) n++;
        return n / (double)a.Cells.Length;
    }
}
