using System.Diagnostics;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class MandelbrotSceneTests
{
    private readonly ITestOutputHelper _out;

    public MandelbrotSceneTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new MandelbrotScene(), "mandelbrot");

    [Fact]
    public void FrameIsNotFlat_OnRouteMoments()
    {
        var s = SceneContract.Make(() => new MandelbrotScene(), 60, 20, 11);
        double t = 0;
        foreach (var moment in new[] { 2.0, 60.0, 150.0 })
        {
            RunTo(s, ref t, moment);
            var g = SceneContract.Snap(s, 60, 20);
            var (black, colored) = CountPixels(g);
            _out.WriteLine($"t={moment:F0} с: чёрных пикселей {black}, цветных {colored}");
            Assert.True(colored > 0, $"на t={moment} с нет цветных пикселей");
            Assert.True(black > 0, $"на t={moment} с нет чёрной внутренности");
        }
    }

    /// <summary>
    /// Каждая точка маршрута на середине зума и за 10 с до его конца — кадр не однотонный (автопилот нашёл границу).
    /// Шаг 0.1 с симуляции (реальный темп), весь маршрут — несколько тысяч шагов на сетке 60×20.
    /// Порог — MandelbrotScene.FlatLumaStd (std яркости R+G+B, 0..765; однотонное поле даёт 0).
    /// Если автопилот сбрасывает точку раньше (однотонность дольше 1.5 с) — тест падает: точка не дошла до глубины.
    /// </summary>
    [Fact]
    public void EveryRoutePoint_IsNotFlat_MidAndEndOfZoom()
    {
        const int cols = 60, rows = 20, maxSteps = 3000;
        var s = (MandelbrotScene)SceneContract.Make(() => new MandelbrotScene(), cols, rows, 31,
            new SceneOptions { Speed = 1f });
        float endT = MandelbrotScene.ZoomDurationSeconds;

        for (int k = 0; k < MandelbrotScene.RouteLength; k++)
        {
            bool mid = AdvanceUntil(s, () => s.PointIndex == k && s.ZoomTime >= endT / 2, maxSteps);
            Assert.True(mid, $"точка {k}: до середины зума не дошли (сейчас точка {s.PointIndex}, t={s.ZoomTime:F1})");
            double stdMid = LumaStd(SceneContract.Snap(s, cols, rows));

            bool end = AdvanceUntil(s, () => s.PointIndex == k && s.ZoomTime >= endT - 10, maxSteps);
            Assert.True(end, $"точка {k}: до конца зума −10 с не дошли (сейчас точка {s.PointIndex}, t={s.ZoomTime:F1})");
            double stdEnd = LumaStd(SceneContract.Snap(s, cols, rows));

            double speckEnd = s.SpeckleShare();
            _out.WriteLine($"точка {k}: std середина {stdMid:F1}, конец−10 с {stdEnd:F1}; крапинки конец−10 с {speckEnd:F4}");
            Assert.True(stdMid >= MandelbrotScene.FlatLumaStd, $"точка {k}: середина зума однотонна (std {stdMid:F1})");
            Assert.True(stdEnd >= MandelbrotScene.FlatLumaStd, $"точка {k}: конец зума однотонен (std {stdEnd:F1})");
            Assert.True(speckEnd < SpeckleLimit, $"точка {k}: конец зума в крапинках (доля {speckEnd:F4})");
        }
    }

    private static bool AdvanceUntil(IScene s, Func<bool> done, int maxSteps)
    {
        for (int i = 0; i < maxSteps; i++)
        {
            if (done()) return true;
            s.Update(0.1f);
        }
        return done();
    }

    private static double LumaStd(CharGrid g)
    {
        double sum = 0, sq = 0;
        int n = 0;
        foreach (var c in g.Cells)
            foreach (var p in new[] { c.Fg, c.Bg })
            {
                double l = p.R + p.G + p.B;
                sum += l;
                sq += l * l;
                n++;
            }
        double mean = sum / n;
        return Math.Sqrt(Math.Max(0, sq / n - mean * mean));
    }

    /// <summary>
    /// Порог крапинок SpeckleLimit. Измерено (SpeckleReport, сетка 60×20, seed 4, точка 1  с и seahorse  с):
    /// «снег» — контрольная версия без DE (раскраска по итерациям, граница по соседству с внутренностью): 0.181;
    /// новая версия с DE-границами: максимум по 8 точкам на конце−10 с — 0.104 (точка 6, мини-брот, заметна крапчатость
    /// по краю), остальные 0.006–0.042; seahorse  с 0.0066, точка 1  с 0.0167. Порог 0.14 — между 0.104 и 0.181.
    /// </summary>
    public const double SpeckleLimit = 0.14;

    /// <summary>Измерение: крапинки на связном кадре и на точке с «снегом» (контроль без DE) и с DE.</summary>
    [Fact]
    public void SpeckleReport()
    {
        const int cols = 60, rows = 20, maxSteps = 6000;
        double seaDe = 0, elephantDe = 0, elephantNoDe = 0;
        try
        {
            MandelbrotScene.DistanceEdges = true;
            var s = (MandelbrotScene)SceneContract.Make(() => new MandelbrotScene(), cols, rows, 4, new SceneOptions { Speed = 1f });
            Assert.True(AdvanceUntil(s, () => s.PointIndex == 0 && s.ZoomTime >= 60, maxSteps), "точка 0 до 60 с не дошла");
            SceneContract.Snap(s, cols, rows);
            seaDe = s.SpeckleShare();

            MandelbrotScene.DistanceEdges = true;
            var e = (MandelbrotScene)SceneContract.Make(() => new MandelbrotScene(), cols, rows, 4, new SceneOptions { Speed = 1f });
            Assert.True(AdvanceUntil(e, () => e.PointIndex == 1 && e.ZoomTime >= 255, maxSteps), "точка 1 до 255 с не дошла");
            SceneContract.Snap(e, cols, rows);
            elephantDe = e.SpeckleShare();

            MandelbrotScene.DistanceEdges = false;
            var n = (MandelbrotScene)SceneContract.Make(() => new MandelbrotScene(), cols, rows, 4, new SceneOptions { Speed = 1f });
            Assert.True(AdvanceUntil(n, () => n.PointIndex == 1 && n.ZoomTime >= 255, maxSteps), "контроль: точка 1 до 255 с не дошла");
            SceneContract.Snap(n, cols, rows);
            elephantNoDe = n.SpeckleShare();
        }
        finally
        {
            MandelbrotScene.DistanceEdges = true;
        }
        _out.WriteLine($"крапинки: связный seahorse @60 с (DE) {seaDe:F4}; точка 1 @255 с с DE {elephantDe:F4}; "
            + $"точка 1 @255 с без DE (контроль-снег) {elephantNoDe:F4}; SpeckleLimit={SpeckleLimit}");
    }

    [Fact]
    public void ZoomGrowsOverTime()
    {
        var s = (MandelbrotScene)SceneContract.Make(() => new MandelbrotScene(), 60, 20, 12);
        double prev = s.Zoom, t = 0;
        foreach (var moment in new[] { 5.0, 10.0, 20.0, 40.0 })
        {
            RunTo(s, ref t, moment);
            Assert.True(s.Zoom > prev, $"зум не вырос к t={moment} с: {s.Zoom:G4} ≤ {prev:G4}");
            prev = s.Zoom;
        }
        _out.WriteLine($"зум на 40 с: ×{s.Zoom:G4}");
    }

    /// <summary>zoomSpeed=2 удваивает темп зума: log2(зума) за 5 с растёт вдвое быстрее, чем при 1 (±20 %).</summary>
    [Fact]
    public void ZoomSpeedParamScalesZoomRate()
    {
        static double Log2Zoom(float zoomSpeed)
        {
            var o = new SceneOptions { Params = System.Text.Json.JsonSerializer.SerializeToElement(new { zoomSpeed }) };
            var s = (MandelbrotScene)SceneContract.Make(() => new MandelbrotScene(), 60, 20, 16, o);
            for (int i = 0; i < 50; i++) s.Update(0.1f);     // 5 с реального времени
            return Math.Log2(s.Zoom);
        }

        double one = Log2Zoom(1f), two = Log2Zoom(2f);
        _out.WriteLine($"log2(зума) за 5 с: zoomSpeed=1 → {one:F3}, zoomSpeed=2 → {two:F3}, ratio {two / one:F3}");
        Assert.True(one > 0, "при zoomSpeed=1 зум не вырос");
        Assert.InRange(two / one, 1.6, 2.4);
    }

    [Fact]
    public void RouteAdvancesToNextPoint()
    {
        // Speed 40: одна секунда кадра = 40 секунд симуляции, маршрут проходит за разумное число шагов.
        var o = new SceneOptions { Speed = 40f };
        var s = (MandelbrotScene)SceneContract.Make(() => new MandelbrotScene(), 40, 12, 13, o);
        double maxZoomBefore = 0;
        int changedAt = -1;
        for (int i = 0; i < 200 && changedAt < 0; i++)
        {
            s.Update(0.1f);
            if (s.PointIndex == 0) maxZoomBefore = Math.Max(maxZoomBefore, s.Zoom);
            else changedAt = i;
        }
        _out.WriteLine($"переход к точке 1 на шаге {changedAt}, зум перед переходом ×{maxZoomBefore:G4}");
        Assert.True(changedAt > 0, "переход к следующей точке не произошёл");
        Assert.True(maxZoomBefore > 1e10, $"перед переходом зум лишь ×{maxZoomBefore:G4}, до предела не дошли");

        for (int i = 0; i < 5; i++) s.Update(0.1f);
        Assert.True(s.Zoom < 1000, $"после перехода зум ×{s.Zoom:G4} — новая точка не начала зум заново");
    }

    [Fact]
    public void BeatFlashesTheFrame()
    {
        const int cols = 80, rows = 30, beatAt = 60;
        var beat = SceneContract.Make(() => new MandelbrotScene(), cols, rows, 14);
        var quiet = SceneContract.Make(() => new MandelbrotScene(), cols, rows, 14);
        var a = (IAudioReactive)beat;
        var after = new CharGrid(cols, rows);
        var qg = new CharGrid(cols, rows);
        double lumaBeat = 0, lumaQuiet = 0;
        bool differs = false;

        for (int f = 0; f <= beatAt + 1; f++)
        {
            if (f == beatAt)
            {
                a.OnAudio(new AudioFrame(Active: true, Level: 0.5f, Bass: 0.7f, Mid: 0f, High: 0f,
                    Beat: true, BeatPulse: 1f, Bpm: 120f));
            }
            beat.Update(SceneContract.Dt);
            quiet.Update(SceneContract.Dt);
            if (f == beatAt + 1)
            {
                beat.Draw(after);
                quiet.Draw(qg);
                lumaBeat = MeanLuma(after);
                lumaQuiet = MeanLuma(qg);
                differs = !after.Cells.SequenceEqual(qg.Cells);
            }
        }
        _out.WriteLine($"средняя яркость после удара {lumaBeat:F1}, без удара {lumaQuiet:F1}");
        Assert.True(differs, "удар не изменил кадр");
        Assert.True(lumaBeat >= 1.2 * lumaQuiet, $"вспышки нет: {lumaBeat:F1} против {lumaQuiet:F1}");
    }

    [Fact]
    public void NoBeatNoFlash_AndInactiveAudioIsSilence()
    {
        const int cols = 80, rows = 30;
        var none = SceneContract.Make(() => new MandelbrotScene(), cols, rows, 15);
        var activeNoBeat = SceneContract.Make(() => new MandelbrotScene(), cols, rows, 15);
        var inactiveBeat = SceneContract.Make(() => new MandelbrotScene(), cols, rows, 15);
        var an = (IAudioReactive)activeNoBeat;
        var ib = (IAudioReactive)inactiveBeat;

        for (int f = 0; f < 180; f++)
        {
            an.OnAudio(new AudioFrame(Active: true, Level: 0.6f, Bass: 0.5f, Mid: 0.3f, High: 0.2f,
                Beat: false, BeatPulse: 0f, Bpm: 120f));
            ib.OnAudio(new AudioFrame(Active: false, Level: 0f, Bass: 0f, Mid: 0f, High: 0f,
                Beat: f % 30 == 0, BeatPulse: 1f, Bpm: 0f));
            none.Update(SceneContract.Dt);
            activeNoBeat.Update(SceneContract.Dt);
            inactiveBeat.Update(SceneContract.Dt);
            if (f % 30 == 29)
            {
                var n = SceneContract.Snap(none, cols, rows);
                Assert.Equal(n.Cells, SceneContract.Snap(activeNoBeat, cols, rows).Cells);
                Assert.Equal(n.Cells, SceneContract.Snap(inactiveBeat, cols, rows).Cells);
            }
        }
    }

    [Fact]
    [Trait("Category", "Diagnostics")]
    public void UpdateAndDraw_Timing_On192x60()
    {
        const int cols = 192, rows = 60;
        var s = SceneContract.Make(() => new MandelbrotScene(), cols, rows, 21);
        var g = new CharGrid(cols, rows);

        // Начало зума: первая минута (3600 кадров по 1/60 с)
        double early = Measure(s, g, 3600);
        _out.WriteLine($"Update+Draw 192x60, первые 60 с зума: {early:F2} мс/кадр в среднем");

        // Глубокий зум: прогон до 150 с, затем 600 кадров
        var deep = SceneContract.Make(() => new MandelbrotScene(), cols, rows, 21);
        for (int i = 0; i < 1500; i++) deep.Update(0.1f);
        double late = Measure(deep, g, 600);
        _out.WriteLine($"Update+Draw 192x60, зум 150–160 с: {late:F2} мс/кадр в среднем");

        Assert.True(early <= 4.0, $"среднее {early:F2} мс/кадр > 4 мс (первая минута зума)");
        Assert.True(late <= 4.0, $"среднее {late:F2} мс/кадр > 4 мс (глубокий зум)");
    }

    private static double Measure(IScene s, CharGrid g, int frames)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
        }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / frames;
    }

    /// <summary>Прогон до момента t (с) шагами по 0.1 с (Update сам обрезает dt до 0.1).</summary>
    private static void RunTo(IScene s, ref double t, double target)
    {
        while (t < target - 1e-9)
        {
            s.Update(0.1f);
            t += 0.1;
        }
    }

    private static (int Black, int Colored) CountPixels(CharGrid g)
    {
        int black = 0, colored = 0;
        foreach (var c in g.Cells)
        {
            foreach (var p in new[] { c.Fg, c.Bg })
            {
                int l = p.R + p.G + p.B;
                if (l == 0) black++;
                else if (l > 60) colored++;
            }
        }
        return (black, colored);
    }

    private static double MeanLuma(CharGrid g)
    {
        double sum = 0;
        foreach (var c in g.Cells) sum += c.Fg.R + c.Fg.G + c.Fg.B + c.Bg.R + c.Bg.G + c.Bg.B;
        return sum / (g.Cells.Length * 2.0);
    }
}
