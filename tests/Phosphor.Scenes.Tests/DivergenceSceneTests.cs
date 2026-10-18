using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class DivergenceSceneTests
{
    private readonly ITestOutputHelper _out;

    public DivergenceSceneTests(ITestOutputHelper output) => _out = output;

    private static SceneOptions Opts(string json) => new() { Params = JsonDocument.Parse(json).RootElement };

    private static DivergenceScene Make(int cols, int rows, int seed, string json = "{}") =>
        (DivergenceScene)SceneContract.Make(() => new DivergenceScene(), cols, rows, seed, Opts(json));

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new DivergenceScene(), "divergence");

    [Fact]
    public void NoMusicIsAlive()
    {
        var s = Make(120, 40, 81);
        SceneContract.Run(s, 60);
        var a = SceneContract.Snap(s, 120, 40);
        SceneContract.Run(s, 300);                       // 5 с без музыки
        var b = SceneContract.Snap(s, 120, 40);
        double share = ChangedShare(a, b);
        _out.WriteLine($"без музыки за 5 с изменилось {share:P0} ячеек, сдвигов {s.ShiftCount}");
        // Прибор по замыслу почти неподвижен: мерцание ±5% и редкий сдвиг. Порог — видимое изменение, а не «все ячейки».
        Assert.True(share > 0.1, $"за 5 с изменилось лишь {share:P0} ячеек");
    }

    [Fact]
    public void UpdateAndDrawAre_Fast_On192x60()
    {
        var s = SceneContract.Make(() => new DivergenceScene(), 192, 60, 31, Opts("{}"));
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
    public void AtRestEightLampsReadTheShownValueWithDotInSecondLamp()
    {
        var s = Make(192, 60, 41, "{\"interval\":120}");
        var g = SceneContract.Snap(s, 192, 60);
        Assert.Equal("1.048596", s.Shown);

        var read = Recognized(s, g);
        _out.WriteLine($"распознано по сетке: {new string(read)}, горит: {s.Shown}");
        for (int i = 0; i < 8; i++)
            if (i != 1) Assert.Equal(s.Shown[i], read[i]);

        var (dx, dy, dw, dh) = s.DotRect(1);
        int dot = LitMask(g, dx, dy, dw, dh).Sum(v => v);
        _out.WriteLine($"лит-субпикселей в точке второй лампы: {dot} из {dw * dh}");
        Assert.True(dot >= 3, $"точка во второй лампе не горит: {dot}");
    }

    [Fact]
    public void ValueFormatIsDDotDDDDDDAfterShifts()
    {
        var s = Make(120, 40, 51, "{\"interval\":5}");
        var pattern = new Regex(@"^[01]\.\d{6}$");
        int checkedShifts = 0;
        for (int f = 0; f < 3600 && checkedShifts < 5; f++)
        {
            SceneContract.Run(s, 1);
            if (!s.Shifting && s.ShiftCount > checkedShifts)
            {
                checkedShifts = s.ShiftCount;
                Assert.Matches(pattern, s.Shown);
            }
        }
        _out.WriteLine($"проверено значений после сдвигов: {checkedShifts}, последнее: {s.Shown}");
        Assert.True(checkedShifts >= 3, $"сдвигов было {checkedShifts}");
    }

    [Fact]
    public void LampsStopLeftToRight()
    {
        var s = Make(120, 40, 61, "{\"interval\":5}");
        for (int f = 0; f < 3600; f++)
        {
            SceneContract.Run(s, 1);
            if (s.ShiftCount > 0 && !s.Shifting) break;
        }
        var t = s.SettleTimes;
        _out.WriteLine("моменты остановки ламп: " + string.Join(" ", t.Select(v => v.ToString("F2"))));
        for (int i = 0; i < 7; i++)
        {
            Assert.True(t[i] <= t[i + 1], $"лампа {i} встала позже лампы {i + 1}");
            Assert.True(t[i + 1] - t[i] >= 0.14f, $"интервал между лампами {i} и {i + 1} меньше 0.15 с");
        }
    }

    [Fact]
    public void ShiftsHappenFasterWithShortInterval()
    {
        var fast = Make(120, 40, 71, "{\"interval\":5}");
        var slow = Make(120, 40, 71, "{\"interval\":120}");
        SceneContract.Run(fast, 3600);
        SceneContract.Run(slow, 3600);
        _out.WriteLine($"сдвигов за 60 с: interval=5 → {fast.ShiftCount}, interval=120 → {slow.ShiftCount}");
        Assert.True(fast.ShiftCount > slow.ShiftCount, $"{fast.ShiftCount} не больше {slow.ShiftCount}");
    }

    [Fact]
    public void StrongBeatAfterSilenceStartsShiftOutOfTurn()
    {
        var s = Make(120, 40, 81, "{\"interval\":120}");
        // Дождаться покоя (возможный плановый сдвиг при interval=120 не должен мешать замеру), затем 5 с тишины.
        for (int f = 0; f < 6000 && (s.Shifting || s.ShiftCount == 0); f++) SceneContract.Run(s, 1);
        SceneContract.Run(s, 300);
        Assert.False(s.Shifting);
        int before = s.ShiftCount;
        _out.WriteLine($"сдвигов до удара: {before}");
        ((IAudioReactive)s).OnAudio(new AudioFrame(Active: true, Level: 0.9f, Bass: 0.9f, Mid: 0.5f, High: 0.3f,
            Beat: true, BeatPulse: 1f, Bpm: 120f));
        Assert.Equal(before + 1, s.ShiftCount);
        Assert.True(s.Shifting);
    }

    [Fact]
    public void BeatWithoutSilenceDoesNotStartShift()
    {
        var s = Make(120, 40, 91, "{\"interval\":120}");
        var audio = (IAudioReactive)s;
        for (int f = 0; f < 240; f++)                     // биты каждые 30 кадров = 0.5 с
        {
            bool beat = f % 30 == 0;
            float pulse = beat ? 1f : 0f;
            audio.OnAudio(new AudioFrame(Active: true, Level: 0.5f, Bass: 0.6f, Mid: 0f, High: 0f,
                Beat: beat, BeatPulse: pulse, Bpm: 120f));
            s.Update(SceneContract.Dt);
        }
        _out.WriteLine($"сдвигов при частых ударах: {s.ShiftCount}");
        Assert.Equal(0, s.ShiftCount);
    }

    [Fact]
    public void BeatReactionIsLockedToTheBeat()
    {
        const int cols = 160, rows = 50, beatEvery = 30; // 0.5 с при 60 кадрах/с
        var s = Make(cols, rows, 61, "{\"interval\":120,\"glow\":1}");
        SceneContract.Run(s, 120);
        var audio = (IAudioReactive)s;
        var cur = new CharGrid(cols, rows);
        double early = 0, late = 0;
        int nEarly = 0, nLate = 0;

        for (int f = 0; f < 10 * beatEvery; f++)
        {
            int phase = f % beatEvery;
            float sinceMs = phase * 1000f / 60f;
            float pulse = MathF.Exp(-sinceMs / 150f);
            audio.OnAudio(new AudioFrame(Active: true, Level: 0.5f, Bass: 0f, Mid: 0f, High: 0f,
                Beat: phase == 0, BeatPulse: pulse, Bpm: 120f));
            s.Update(SceneContract.Dt);
            s.Draw(cur);
            if (sinceMs < 150f) { early += BgSum(cur); nEarly++; }
            else if (sinceMs >= 250f && sinceMs < 400f) { late += BgSum(cur); nLate++; }
        }

        double e = early / nEarly, l = late / nLate;
        _out.WriteLine($"сумма яркости фона: 0–150 мс после удара {e:F0}, 250–400 мс {l:F0}, отношение {e / l:F3}");
        Assert.True(s.ShiftCount == 0, "сдвиг мешает замеру реакции на удар");
        Assert.True(e >= 1.03 * l, $"реакция ореола не привязана к удару: {e:F0} против {l:F0}");
    }

    [Fact]
    public void IntervalMinAndMaxDiffer() =>
        AssertParamDiffers(11, "{\"interval\":5}", "{\"interval\":120}");

    [Fact]
    public void GhostsMinAndMaxDiffer() =>
        AssertParamDiffers(12, "{\"ghosts\":false}", "{\"ghosts\":true}");

    [Fact]
    public void GlowMinAndMaxDiffer() =>
        AssertParamDiffers(13, "{\"glow\":0}", "{\"glow\":1}");

    /// <summary>Min и Max параметра дают разную картинку через 120 кадров на 120×40.</summary>
    private static void AssertParamDiffers(int seed, string min, string max)
    {
        var a = Make(120, 40, seed, min);
        var b = Make(120, 40, seed, max);
        SceneContract.Run(a, 120);
        SceneContract.Run(b, 120);
        Assert.NotEqual(SceneContract.Snap(a, 120, 40).Cells, SceneContract.Snap(b, 120, 40).Cells);
    }

    /// <summary>Осмотр цифр: два значения покрывают 0–9. Лит-маски ламп пишутся текстом в %TEMP%.</summary>
    [Fact]
    public void DigitSheetForInspection()
    {
        foreach (var (name, value) in new[] { ("A", "0.123456"), ("B", "0.789012") })
        {
            var s = Make(192, 60, 5, "{\"glow\":0}");
            s.ForceValueForTest(value);
            var g = SceneContract.Snap(s, 192, 60);
            var rects = Enumerable.Range(0, 8).Select(i => s.LampRect(i)).ToArray();
            int hs = rects[0].H, ws = rects[0].W;
            var masks = rects.Select((r, i) => LitMask(g, r.X, r.Y, r.W, r.H)).ToArray();
            for (int i = 0; i < 8; i++)
                if (i != 1) Assert.True(masks[i].Sum(v => v) > 20, $"лампа {i} почти пустая");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"значение {value}, сетка 192x60, маска лампы {ws}x{hs} субпикселей");
            for (int y = 0; y < hs; y++)
            {
                for (int i = 0; i < 8; i++)
                {
                    for (int x = 0; x < ws; x++) sb.Append(masks[i][y * ws + x] != 0 ? '#' : '.');
                    sb.Append("  ");
                }
                sb.AppendLine();
            }
            var path = Path.Combine(Path.GetTempPath(), $"divergence-digits-{name}.txt");
            File.WriteAllText(path, sb.ToString());
            _out.WriteLine($"снимок цифр {value}: {path}");
        }
    }

    // ---- помощники ----

    private static double ChangedShare(CharGrid a, CharGrid b)
    {
        int n = 0;
        for (int i = 0; i < a.Cells.Length; i++)
            if (a.Cells[i].Fg != b.Cells[i].Fg || a.Cells[i].Bg != b.Cells[i].Bg || a.Cells[i].Ch != b.Cells[i].Ch) n++;
        return n / (double)a.Cells.Length;
    }

    private static double BgSum(CharGrid g)
    {
        double s = 0;
        foreach (var c in g.Cells) s += c.Bg.R;
        return s;
    }

    /// <summary>Лит-субпиксели в прямоугольнике (субпиксельные координаты): 1 — горит яркий секстель.</summary>
    private static byte[] LitMask(CharGrid g, int x0, int y0, int w, int h)
    {
        var m = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sx = x0 + x, sy = y0 + y;
                if (sx < 0 || sy < 0) continue;
                int cx = sx >> 1, cy = sy / 3;
                if (cx >= g.Cols || cy >= g.Rows) continue;
                var c = g[cx, cy];
                int bit = (sx & 1) | ((sy % 3) << 1);
                if (GlyphSet.IsSextant(c.Ch) && (((c.Ch - 0xE000) >> bit) & 1) == 1 && c.Fg.R > 150)
                    m[y * w + x] = 1;
            }
        }
        return m;
    }

    /// <summary>Распознавание цифр по сетке: сравнение лит-маски с эталонными масками (IoU). Точка (лампа 1) не распознаётся.</summary>
    private static char[] Recognized(DivergenceScene s, CharGrid g)
    {
        var r = new char[8];
        for (int i = 0; i < 8; i++)
        {
            if (i == 1) { r[i] = '.'; continue; }
            var (x0, y0, w, h) = s.LampRect(i);
            var lit = LitMask(g, x0, y0, w, h);
            int best = -1;
            double bestScore = -1;
            for (int d = 0; d < 10; d++)
            {
                var t = DivergenceScene.DigitMask(d, w, h);
                int inter = 0, uni = 0;
                for (int k = 0; k < lit.Length; k++)
                {
                    bool a = lit[k] != 0, b = t[k] != 0;
                    if (a && b) inter++;
                    if (a || b) uni++;
                }
                double iou = uni == 0 ? 0 : (double)inter / uni;
                if (iou > bestScore) { bestScore = iou; best = d; }
            }
            r[i] = (char)('0' + best);
        }
        return r;
    }
}
