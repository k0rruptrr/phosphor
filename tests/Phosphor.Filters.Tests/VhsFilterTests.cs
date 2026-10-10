using Phosphor.Filters;
using Phosphor.Gfx;

namespace Phosphor.Filters.Tests;

public class VhsFilterTests
{
    private static readonly string[] ParamNames =
    {
        "jitter", "tracking", "bleed", "noise", "rgbShift",
        "bottomSkew", "wobble", "saturation", "dropouts",
        "badTracking", "bandSpeed", "iqShift", "chromaClip", "lumaRes", "chromaRes",
    };

    private static readonly Dictionary<string, float> Defaults = new()
    {
        ["jitter"] = 0.25f,
        ["tracking"] = 0.2f,
        ["bleed"] = 0.5f,
        ["noise"] = 0.12f,
        ["rgbShift"] = 1.0f,
        ["bottomSkew"] = 0.5f,
        ["wobble"] = 0.3f,
        ["saturation"] = 0.85f,
        ["dropouts"] = 0.2f,
        ["badTracking"] = 0f,
        ["bandSpeed"] = 1f,
        ["iqShift"] = 1.5f,
        ["chromaClip"] = 0.4f,
        ["lumaRes"] = 1920f,
        ["chromaRes"] = 400f,
    };

    private const string ShaderName = "vhs.frag";

    [Fact]
    public void Id_IsVhs()
    {
        Assert.Equal("vhs", new VhsFilter().Id);
    }

    [Fact]
    public void Params_HasExactlyTheExpectedNames()
    {
        var names = new VhsFilter().Params.Select(p => p.Name).ToArray();
        Assert.Equal(ParamNames, names);
    }

    [Fact]
    public void Params_ContainsTrackingAndRgbShift()
    {
        var names = new VhsFilter().Params.Select(p => p.Name).ToArray();
        Assert.Contains("tracking", names);
        Assert.Contains("rgbShift", names);
    }

    [Fact]
    public void Params_MinLessOrEqualDefaultLessOrEqualMax()
    {
        foreach (var p in new VhsFilter().Params)
        {
            Assert.True(p.Min <= p.Default, $"{p.Name}: Min {p.Min} > Default {p.Default}");
            Assert.True(p.Default <= p.Max, $"{p.Name}: Default {p.Default} > Max {p.Max}");
        }
    }

    [Fact]
    public void Params_DefaultsMatchSpec()
    {
        foreach (var p in new VhsFilter().Params)
            Assert.Equal(Defaults[p.Name], p.Default);
    }

    [Fact]
    public void ShaderResource_Exists()
    {
        Assert.NotNull(typeof(VhsFilter).Assembly.GetManifestResourceStream("Phosphor.Filters.Shaders." + ShaderName));
    }

    [Fact]
    public void Shader_PassesStaticSyntaxCheck()
    {
        var src = ShaderStatic.Load("Phosphor.Filters.Shaders." + ShaderName);
        ShaderStatic.CheckSyntax(src, ShaderName);
    }

    [Fact]
    public void Shader_DeclaresExactlyTheFilterParams()
    {
        var src = ShaderStatic.Load("Phosphor.Filters.Shaders." + ShaderName);
        ShaderStatic.CheckParams(new VhsFilter(), src);
    }

    [Fact]
    public void Shader_ClampsOutputToNonNegative()
    {
        var src = ShaderStatic.Load("Phosphor.Filters.Shaders." + ShaderName);
        Assert.Contains("col = max(col, 0.0);", src);
    }

    [Fact]
    public void Shader_HasRequiredInterface()
    {
        var src = ShaderStatic.Load("Phosphor.Filters.Shaders." + ShaderName);
        Assert.Contains("in vec2 vUv", src);
        Assert.Contains("out vec4 fragColor", src);
        Assert.Contains("uniform sampler2D uInput", src);
    }

    // ---- Полоса трекинга на реальном GPU: вход — ровное серое поле 0.5, только полоса ----

    private static readonly Dictionary<string, float> BandOnly = new()
    {
        ["jitter"] = 0f,
        ["noise"] = 0f,
        ["wobble"] = 0f,
        ["tracking"] = 1f,
    };

    /// <summary>Средняя яркость каждой строки минус 0.5. Строка 0 — верх.</summary>
    private static float[] RowMeanDev(float[,,] img)
    {
        int h = img.GetLength(0), w = img.GetLength(1);
        var res = new float[h];
        for (int y = 0; y < h; y++)
        {
            double s = 0;
            for (int x = 0; x < w; x++) s += GpuHarness.Luma(img, x, y) - 0.5;
            res[y] = (float)(s / w);
        }
        return res;
    }

    /// <summary>Скользящее среднее профиля окном width (нечётное), края — по доступным строкам.</summary>
    private static float[] Smooth(float[] src, int width)
    {
        int r = width / 2;
        var res = new float[src.Length];
        for (int y = 0; y < src.Length; y++)
        {
            int a = Math.Max(0, y - r), b = Math.Min(src.Length - 1, y + r);
            double s = 0;
            for (int i = a; i <= b; i++) s += src[i];
            res[y] = (float)(s / (b - a + 1));
        }
        return res;
    }

    [Fact]
    public void Band_ProfileIsSoft_AndVisible()
    {
        using var gpu = new GpuHarness(640, 360);
        gpu.SetInput((x, y) => (0.5f, 0.5f, 0.5f));
        using var f = gpu.Create<VhsFilter>();
        // Полоса есть только в эпизодах автотрекинга (0..20 с): берём первые кадры с заметной полосой.
        int checkedFrames = 0;
        for (long frame = 0; frame <= 1200 && checkedFrames < 3; frame += 6)
        {
            // Скользящее окно 7 строк: штрихи — шум строки, а не жёсткость края; проверяем огибающую полосы.
            var prof = Smooth(RowMeanDev(gpu.Run(f, frame, BandOnly)), 7);
            float maxJump = 0f, peak = 0f;
            for (int y = 1; y < prof.Length; y++) maxJump = Math.Max(maxJump, Math.Abs(prof[y] - prof[y - 1]));
            foreach (var v in prof) peak = Math.Max(peak, Math.Abs(v));
            if (peak <= 0.08f) continue;
            Assert.True(maxJump < 0.12f, $"кадр {frame}: скачок между соседними строками {maxJump:F3}");
            checkedFrames++;
        }
        Assert.Equal(3, checkedFrames);
    }

    [Fact]
    public void Band_HasHorizontalStreaks_MoreThanVerticalCorrelation()
    {
        using var gpu = new GpuHarness(640, 360);
        gpu.SetInput((x, y) => (0.5f, 0.5f, 0.5f));
        using var f = gpu.Create<VhsFilter>();
        // Кадр с самой сильной полосой в окне 0..20 с (полоса есть только в эпизодах).
        long bestFrame = 0;
        float bestPeak = -1f;
        for (long frame = 0; frame <= 1200; frame += 6)
        {
            float pk = 0f;
            foreach (var v in RowMeanDev(gpu.Run(f, frame, BandOnly))) pk = Math.Max(pk, Math.Abs(v));
            if (pk > bestPeak) { bestPeak = pk; bestFrame = frame; }
        }
        var img = gpu.Run(f, bestFrame, BandOnly);
        int h = img.GetLength(0), w = img.GetLength(1);
        var prof = RowMeanDev(img);
        float peak = 0f;
        foreach (var v in prof) peak = Math.Max(peak, Math.Abs(v));
        // строки полосы: профиль выше половины пика
        var inBand = new bool[h];
        for (int y = 0; y < h; y++) inBand[y] = Math.Abs(prof[y]) > 0.5f * peak;

        // горизонтальная автокорреляция на лаге 4 px, отклонение от среднего строки
        double hNum = 0, hDen = 0;
        for (int y = 0; y < h; y++)
        {
            if (!inBand[y]) continue;
            double m = prof[y];
            for (int x = 0; x < w; x++)
            {
                double a = GpuHarness.Luma(img, x, y) - 0.5 - m;
                hDen += a * a;
                if (x + 4 < w) hNum += a * (GpuHarness.Luma(img, x + 4, y) - 0.5 - m);
            }
        }
        // вертикальная автокорреляция на лаге 1 строка, отклонение от среднего полосы
        double mean = 0; int n = 0;
        for (int y = 0; y < h; y++)
        {
            if (!inBand[y]) continue;
            for (int x = 0; x < w; x++) { mean += GpuHarness.Luma(img, x, y) - 0.5; n++; }
        }
        mean /= n;
        double vNum = 0, vDen = 0;
        for (int y = 0; y < h; y++)
        {
            if (!inBand[y]) continue;
            for (int x = 0; x < w; x++)
            {
                double a = GpuHarness.Luma(img, x, y) - 0.5 - mean;
                vDen += a * a;
                if (y + 1 < h && inBand[y + 1]) vNum += a * (GpuHarness.Luma(img, x, y + 1) - 0.5 - mean);
            }
        }
        double hCorr = hNum / hDen, vCorr = vNum / vDen;
        Assert.True(hCorr > 0.5, $"горизонтальная автокорреляция на лаге 4 px {hCorr:F3}");
        Assert.True(hCorr > vCorr, $"горизонталь {hCorr:F3} должна быть больше вертикали {vCorr:F3}");
    }

    // ---- Шов головок, покой и автотрекинг: вход — вертикальные полосы (сдвиг строк виден по корреляции) ----

    /// <summary>Яркость входа по x: полосы по 32 px светлых/тёмных, период 64 px. Одинаковы в каждой строке.</summary>
    private static float Stripe(int x) => (((x % 64) + 64) % 64) < 32 ? 0.2f : 0.8f;

    /// <summary>
    /// Сдвиг строки y (вправо — плюс) по лучшему совпадению с полосами, поиск в [-range, range].
    /// Сравниваются только x в [margin, w - margin).
    /// </summary>
    private static int BestShift(float[,,] img, int y, int range)
    {
        int w = img.GetLength(1);
        return BestShiftWindow(img, y, 40, w - 40, range);
    }

    /// <summary>То же, но только для x в [x0, x1).</summary>
    private static int BestShiftWindow(float[,,] img, int y, int x0, int x1, int range)
    {
        int best = 0;
        double bestErr = double.MaxValue;
        for (int s = -range; s <= range; s++)
        {
            double err = 0;
            for (int x = x0; x < x1; x++) err += Math.Abs(GpuHarness.Luma(img, x, y) - Stripe(x - s));
            if (err < bestErr) { bestErr = err; best = s; }
        }
        return best;
    }

    /// <summary>Максимум |сдвига| по строкам с шагом step в диапазоне [yFrom, yTo].</summary>
    private static int MaxRowShift(float[,,] img, int range, int yFrom, int yTo, int step)
    {
        int m = 0;
        for (int y = yFrom; y <= yTo; y += step) m = Math.Max(m, Math.Abs(BestShift(img, y, range)));
        return m;
    }

    private static Dictionary<string, float> StripeOnly(float tracking, float bottomSkew) => new()
    {
        ["jitter"] = 0f,
        ["noise"] = 0f,
        ["wobble"] = 0f,
        ["tracking"] = tracking,
        ["rgbShift"] = 0f,
        ["dropouts"] = 0f,
        ["bottomSkew"] = bottomSkew,
    };

    [Fact]
    public void Seam_BottomRowsShifted_UpperRowsNot()
    {
        const int W = 640, H = 360;
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (Stripe(x), Stripe(x), Stripe(x)));
        using var f = gpu.Create<VhsFilter>();
        var img = gpu.Run(f, 300, StripeOnly(0f, 0.6f));

        // Нижние 2.5% высоты (≈9 строк): по окнам 128 px сдвиг каждой строки в среднем >= 5 px,
        // и у части строк сдвиг меняется между соседними окнами (надлом строки)
        const int win = 128;
        var all = new List<int>();
        int rowsBroken = 0;
        for (int y = H - 9; y < H; y++)
        {
            var shifts = new List<int>();
            for (int x0 = 40; x0 + win <= W - 40; x0 += win) shifts.Add(BestShiftWindow(img, y, x0, x0 + win, 30));
            all.AddRange(shifts.Select(Math.Abs));
            if (shifts.Max() - shifts.Min() >= 5) rowsBroken++;
        }
        double mean = all.Average();
        Assert.True(mean >= 5.0, $"средний сдвиг строк шва {mean:F2}");
        Assert.True(rowsBroken >= 3, $"строк с надломом (сдвиг меняется между окнами) {rowsBroken}");

        // Строки выше 4% от низа не сдвинуты
        for (int y = 0; y <= H - 16; y += 7)
        {
            int s = BestShift(img, y, 30);
            Assert.True(Math.Abs(s) <= 0, $"строка {y} выше шва сдвинута на {s} px");
        }
    }

    [Fact]
    public void Seam_ZoneRowsAreStretchedCopyOfSourceRow()
    {
        // Вход зависит от y: амплитуда полос меняется по строкам, поэтому среднее строки — отпечаток строки.
        const int W = 640, H = 360;
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => { float v = Stripe(x) * (0.5f + 0.5f * MathF.Sin(y * 1.7f)); return (v, v, v); });
        using var f = gpu.Create<VhsFilter>();
        var img = gpu.Run(f, 300, StripeOnly(0f, 0.6f));

        // Средняя яркость строки по x в [40, W-40)
        float RowMean(int y)
        {
            double s = 0;
            for (int x = 40; x < W - 40; x++) s += GpuHarness.Luma(img, x, y);
            return (float)(s / (W - 80));
        }

        // Строки зоны (нижние 7 строк, выше линии-стыка) почти одинаковы: копия одной строки
        var zone = new List<float>();
        for (int y = H - 7; y < H; y++) zone.Add(RowMean(y));
        float lo = zone.Min(), hi = zone.Max();
        Assert.True(hi - lo < 0.02f, $"строки зоны не совпадают между собой: {lo:F3}..{hi:F3}");

        // Строка выше зоны другая: зона не является исходной картинкой
        float maxDiff = 0f;
        for (int y = H - 16; y <= H - 12; y++) maxDiff = Math.Max(maxDiff, Math.Abs(RowMean(y) - zone[0]));
        Assert.True(maxDiff > 0.05f, $"строки над зоной не отличаются от зоны: максимум разницы {maxDiff:F3}");
    }

    [Fact]
    public void NoBand_AtRest_TrackingBelowThreshold_600Frames()
    {
        const int W = 320, H = 180;
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (Stripe(x), Stripe(x), Stripe(x)));
        using var f = gpu.Create<VhsFilter>();
        var o = StripeOnly(0.1f, 0.5f);
        for (long frame = 0; frame < 600; frame++)
        {
            var img = gpu.Run(f, frame, o);
            // Средние 90% высоты (строки 5%..95%) не сдвинуты больше чем на 2 px
            for (int y = H / 20; y <= H - H / 20; y += 3)
            {
                int s = BestShift(img, y, 6);
                Assert.True(Math.Abs(s) <= 2, $"кадр {frame}, строка {y}: сдвиг {s} px при tracking=0.1");
            }
        }
    }

    [Fact]
    public void Episode_TrackingOne_AppearsAndFadesWithin20Seconds()
    {
        const int W = 320, H = 180;
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (Stripe(x), Stripe(x), Stripe(x)));
        using var f = gpu.Create<VhsFilter>();
        var o = StripeOnly(1f, 0.5f);

        // Максимум сдвига строк по кадрам, шаг 6 кадров (0.1 с), окно 0..20 с
        var m = new List<int>();
        for (long frame = 0; frame <= 1200; frame += 6)
            m.Add(MaxRowShift(gpu.Run(f, frame, o), 30, H / 10, H * 9 / 10, 6));

        int first = m.FindIndex(v => v > 8);
        Assert.True(first >= 0, "в окне 0..20 с нет кадров со сдвигом строк > 8 px");

        // Границы найденного эпизода: соседние отсчёты со сдвигом > 2 px
        int start = first;
        while (start > 0 && m[start - 1] > 2) start--;
        int end = first;
        while (end + 1 < m.Count && m[end + 1] > 2) end++;
        int len = end - start + 1;
        Assert.True(len >= 6, $"эпизод слишком короткий: {len} отсчётов");

        int third = len / 3;
        int maxFirst = m.Skip(start).Take(third).Max();
        int maxLast = m.Skip(end - third + 1).Take(third).Max();
        Assert.True(maxFirst > 8, $"пик эпизода {maxFirst} px");
        Assert.True(maxLast < maxFirst, $"затухание: в последней трети {maxLast} px, в первой {maxFirst} px");
    }

    [Fact]
    public void Pause_TrackingQuarter_AtLeast20SecondsClean()
    {
        const int W = 320, H = 180;
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (Stripe(x), Stripe(x), Stripe(x)));
        using var f = gpu.Create<VhsFilter>();
        var o = StripeOnly(0.25f, 0.5f);

        // Моменты (с) с заметной полосой, шаг 0.2 с, окно 0..120 с
        var active = new List<double>();
        for (long frame = 0; frame <= 7200; frame += 12)
            if (MaxRowShift(gpu.Run(f, frame, o), 15, H / 10, H * 9 / 10, 9) > 1) active.Add(frame / 60.0);

        Assert.True(active.Count > 0, "при tracking=0.25 за 120 с не найдено ни одного эпизода");
        // Между эпизодами — не меньше 20 с чистых кадров
        int gaps = 0;
        for (int i = 0; i + 1 < active.Count; i++)
        {
            double gap = active[i + 1] - active[i];
            if (gap <= 2.0) continue;   // внутри одного эпизода
            gaps++;
            Assert.True(gap >= 20.0, $"пауза между эпизодами {gap:F1} с (с {active[i]:F1} с)");
        }
        Assert.True(gaps >= 2, $"найдено пауз между эпизодами: {gaps}");
    }

    // ---- Плохой трекинг, разъезд I/Q, обрезка цветности: все прочие эффекты выключены ----

    private static Dictionary<string, float> Neutral(params (string k, float v)[] kv)
    {
        var d = new Dictionary<string, float>
        {
            ["jitter"] = 0f, ["noise"] = 0f, ["wobble"] = 0f, ["tracking"] = 0f,
            ["rgbShift"] = 0f, ["dropouts"] = 0f, ["bottomSkew"] = 0f, ["bleed"] = 0f,
            ["saturation"] = 1f, ["iqShift"] = 0f, ["chromaClip"] = 0f, ["badTracking"] = 0f,
        };
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    /// <summary>Сдвиг строки: центроид белых пикселей (яркость > 0.5) минус базовая линия. 0, если белых мало.</summary>
    private static float WhiteShift(float[,,] img, int y, float baseline)
    {
        int w = img.GetLength(1);
        double s = 0;
        int n = 0;
        for (int x = 0; x < w; x++)
            if (GpuHarness.Luma(img, x, y) > 0.5f) { s += x; n++; }
        return n < 200 ? 0f : (float)(s / n - baseline);
    }

    /// <summary>Строка с максимальным |сдвигом| и сам сдвиг.</summary>
    private static (int row, float shift) MaxWhiteShift(float[,,] img, float baseline)
    {
        int best = 0;
        float bestS = 0f;
        for (int y = 0; y < img.GetLength(0); y++)
        {
            float s = WhiteShift(img, y, baseline);
            if (Math.Abs(s) > Math.Abs(bestS)) { bestS = s; best = y; }
        }
        return (best, bestS);
    }

    [Fact]
    public void BadTracking_WhiteBar_ShiftsRowsSideways_AndRollsDown()
    {
        const int W = 1920, H = 180;
        const int x0 = 760, x1 = 1160;               // белая полоса 400 px на чёрном
        const float baseline = (x0 + x1 - 1) / 2f;
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => { float v = x >= x0 && x < x1 ? 1f : 0f; return (v, v, v); });
        using var f = gpu.Create<VhsFilter>();
        var o = Neutral(("badTracking", 1f));

        var (r0, s0) = MaxWhiteShift(gpu.Run(f, 0, o), baseline);
        Assert.True(Math.Abs(s0) > 20f, $"максимальный сдвиг полосы {s0:F1} px на строке {r0}, нужно > 20");

        // 240 кадров при bandSpeed 1 и 60 fps: полоса проходит половину кадра, строка максимума уходит вниз
        var (r1, _) = MaxWhiteShift(gpu.Run(f, 240, o), baseline);
        int down = ((r1 - r0) % H + H) % H;
        Assert.True(down >= 0.3 * H, $"строка максимума сместилась вниз на {down} из {H} строк (нужно ≥ 30%)");
    }

    [Fact]
    public void IqShift_TintDiffersAcrossGrayBarEdges()
    {
        const int W = 640, H = 1080;                 // H = 1080: сдвиг I/Q ровно iqShift px
        const int y = 540;
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, yy) => x >= 200 && x < 440 ? (0.5f, 0.5f, 0.5f) : (0f, 0f, 1f));
        using var f = gpu.Create<VhsFilter>();

        // Цветность (R−G, B−G) внутри серой полосы у левого (x=201) и правого (x=437) края
        (float, float) Chroma(float[,,] img, int x) =>
            (img[y, x, 0] - img[y, x, 1], img[y, x, 2] - img[y, x, 1]);
        static float Diff((float a, float b) p, (float a, float b) q) =>
            MathF.Max(MathF.Abs(p.a - q.a), MathF.Abs(p.b - q.b));

        var none = gpu.Run(f, 0, Neutral(("iqShift", 0f)));
        var shifted = gpu.Run(f, 0, Neutral(("iqShift", 3f)));

        float d0 = Diff(Chroma(none, 201), Chroma(none, 437));
        float d3 = Diff(Chroma(shifted, 201), Chroma(shifted, 437));
        Assert.True(d0 <= 0.01f, $"при iqShift=0 края серой полосы различаются на {d0:F3}");
        Assert.True(d3 > 0.15f, $"при iqShift=3 разность цветности краёв {d3:F3}, нужно > 0.15");
    }

    [Fact]
    public void ChromaClip_ReducesSaturatedRed_KeepsGray()
    {
        const int W = 320, H = 180;
        using var gpu = new GpuHarness(W, H);
        using var f = gpu.Create<VhsFilter>();

        static float Sat(float[,,] img)
        {
            float r = img[90, 160, 0], g = img[90, 160, 1], b = img[90, 160, 2];
            return MathF.Max(r, MathF.Max(g, b)) - MathF.Min(r, MathF.Min(g, b));
        }

        gpu.SetInput((x, y) => (1f, 0f, 0f));
        float satNone = Sat(gpu.Run(f, 0, Neutral(("chromaClip", 0f))));
        float satClip = Sat(gpu.Run(f, 0, Neutral(("chromaClip", 1f))));
        Assert.True(satNone > 0.9f, $"чистый красный без обрезки: насыщенность {satNone:F3}");
        Assert.True(satClip < satNone - 0.3f, $"обрезка цветности: {satClip:F3} при {satNone:F3} без обрезки");

        gpu.SetInput((x, y) => (0.5f, 0.5f, 0.5f));
        float satGray = Sat(gpu.Run(f, 0, Neutral(("chromaClip", 1f))));
        Assert.True(satGray < 0.01f, $"серый не должен менять цвет при обрезке: насыщенность {satGray:F3}");
    }

    /// <summary>Ширина перехода яркости по строке y от 10% до 90% (первое пересечение вверх), в пикселях.</summary>
    private static int EdgeWidth(float[,,] img, int y)
    {
        int w = img.GetLength(1);
        int x10 = -1, x90 = -1;
        for (int x = 0; x < w; x++)
        {
            float l = GpuHarness.Luma(img, x, y);
            if (x10 < 0 && l >= 0.1f) x10 = x;
            if (x90 < 0 && l >= 0.9f) { x90 = x; break; }
        }
        return x90 - x10;
    }

    [Fact]
    public void ChromaRes40_RednessSpreadsOver30PxAroundRedBar()
    {
        const int W = 1920, H = 240;
        const int bx0 = 958, bx1 = 962;              // красная вертикальная полоса 4 px на сером
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => x >= bx0 && x < bx1 ? (1f, 0.5f, 0.5f) : (0.5f, 0.5f, 0.5f));
        using var f = gpu.Create<VhsFilter>();
        var img = gpu.Run(f, 0, Neutral(("chromaRes", 40f)));

        // Цветность по оси I (YIQ): не зависит от яркости, на сером равна 0, у красного положительна
        const int y = 120;
        float Red(int x) => 0.596f * img[y, x, 0] - 0.274f * img[y, x, 1] - 0.322f * img[y, x, 2];
        float peak = 0f;
        for (int x = 0; x < W; x++) peak = Math.Max(peak, Red(x));
        float level = 0.1f * peak;                        // ширина на уровне 10% пика
        int left = 0, right = 0;
        for (int x = bx0 - 1; x >= 0 && Red(x) >= level; x--) left++;
        for (int x = bx1; x < W && Red(x) >= level; x++) right++;
        Assert.True(left > 30, $"красноватость слева шире {left} px, нужно > 30");
        Assert.True(right > 30, $"красноватость справа шире {right} px, нужно > 30");
    }

    [Fact]
    public void LumaResDefault_EdgeStaysSharp()
    {
        const int W = 1920, H = 180;
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => x < 960 ? (0f, 0f, 0f) : (1f, 1f, 1f));
        using var f = gpu.Create<VhsFilter>();
        var img = gpu.Run(f, 0, Neutral(("lumaRes", 1920f), ("chromaRes", 400f)));
        int width = EdgeWidth(img, 90);
        Assert.True(width <= 3, $"ширина перехода яркости при lumaRes 1920: {width} px, нужно ≤ 3");
    }

    [Fact]
    public void LumaRes520_EdgeIsBlurred()
    {
        const int W = 1920, H = 180;
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => x < 960 ? (0f, 0f, 0f) : (1f, 1f, 1f));
        using var f = gpu.Create<VhsFilter>();
        var img = gpu.Run(f, 0, Neutral(("lumaRes", 520f)));
        int width = EdgeWidth(img, 90);
        Assert.True(width > 3, $"ширина перехода яркости при lumaRes 520: {width} px, нужно > 3");
    }
}
