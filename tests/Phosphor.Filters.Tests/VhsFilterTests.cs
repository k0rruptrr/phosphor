using Phosphor.Filters;
using Phosphor.Gfx;

namespace Phosphor.Filters.Tests;

public class VhsFilterTests
{
    private static readonly string[] ParamNames =
    {
        "jitter", "tracking", "bleed", "noise", "rgbShift",
        "bottomSkew", "wobble", "saturation", "dropouts",
        "badTracking", "bandSpeed", "iqShift", "chromaClip", "lumaRes", "chromaRes", "tbc", "bandClone",
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
        ["tbc"] = 0f,
        ["bandClone"] = 0.35f,
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
    public void WeakBand_TrackingLow_ShiftStaysSmall_600Frames()
    {
        // Мёртвой зоны больше нет: при tracking=0.1 эпизоды есть, но сила ∝ tracking (до ~3 px)
        const int W = 320, H = 180;
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (Stripe(x), Stripe(x), Stripe(x)));
        using var f = gpu.Create<VhsFilter>();
        var o = StripeOnly(0.1f, 0.5f);
        for (long frame = 0; frame < 600; frame++)
        {
            var img = gpu.Run(f, frame, o);
            // Средние 90% высоты (строки 5%..95%) не сдвинуты больше чем на 4 px
            for (int y = H / 20; y <= H - H / 20; y += 3)
            {
                int s = BestShift(img, y, 6);
                Assert.True(Math.Abs(s) <= 4, $"кадр {frame}, строка {y}: сдвиг {s} px при tracking=0.1");
            }
        }
    }

    [Fact]
    public void Tracking_Zero_NoEpisodes_600Frames()
    {
        const int W = 320, H = 180;
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (Stripe(x), Stripe(x), Stripe(x)));
        using var f = gpu.Create<VhsFilter>();
        var o = StripeOnly(0f, 0.5f);
        for (long frame = 0; frame < 600; frame++)
        {
            var img = gpu.Run(f, frame, o);
            for (int y = H / 20; y <= H - H / 20; y += 3)
            {
                int s = BestShift(img, y, 6);
                Assert.True(Math.Abs(s) <= 2, $"кадр {frame}, строка {y}: сдвиг {s} px при tracking=0");
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
            ["rgbShift"] = 0f, ["dropouts"] = 0f, ["bottomSkew"] = 0f, ["bleed"] = 0f, ["bandClone"] = 0f,
            ["saturation"] = 1f, ["iqShift"] = 0f, ["chromaClip"] = 0f, ["badTracking"] = 0f,
        };
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    /// <summary>Строки полосы плохого трекинга: средняя яркость строки выше медианы строк кадра на 0.03.</summary>
    private static bool[] BandRows(float[,,] img)
    {
        int h = img.GetLength(0), w = img.GetLength(1);
        var mean = new double[h];
        for (int y = 0; y < h; y++)
        {
            double s = 0;
            for (int x = 0; x < w; x++) s += GpuHarness.Luma(img, x, y);
            mean[y] = s / w;
        }
        var sorted = (double[])mean.Clone();
        Array.Sort(sorted);
        double med = sorted[h / 2];
        var rows = new bool[h];
        for (int y = 0; y < h; y++) rows[y] = mean[y] > med + 0.03;
        return rows;
    }

    [Fact]
    public void BadTracking_Streaks_SmearRightWithConstantSegments()
    {
        // Тонкие белые полосы через каждые 64 px на сером. В полосе отрезки строки тянут цвет источника из начала
        // отрезка: есть строки, где засвеченный участок у полосы ≥ 20 px, и внутри него цвет постоянен.
        const int W = 1920, H = 540;
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => { float v = x % 64 < 4 ? 1f : 0.5f; return (v, v, v); });
        using var f = gpu.Create<VhsFilter>();
        var img = gpu.Run(f, 0, Neutral(("badTracking", 1f)));
        var band = BandRows(img);

        // Отрезок — участок засвеченных пикселей без скачка яркости > 0.002 между соседями (внутри отрезка яркость точно постоянна) (граница отрезка).
        bool found = false;
        double worstSd = 0;
        for (int y = 0; y < H; y++)
        {
            if (!band[y]) continue;
            int x = 0;
            while (x < W)
            {
                if (GpuHarness.Luma(img, x, y) <= 0.8f) { x++; continue; }
                int x0 = x;
                double s = 0, s2 = 0;
                double prev = GpuHarness.Luma(img, x, y);
                while (x < W)
                {
                    double v = GpuHarness.Luma(img, x, y);
                    if (v <= 0.8 || Math.Abs(v - prev) > 0.002) break;
                    s += v; s2 += v * v; prev = v; x++;
                }
                int n = x - x0;
                if (n < 20) continue;
                found = true;
                double m = s / n;
                worstSd = Math.Max(worstSd, Math.Sqrt(Math.Max(0, s2 / n - m * m)));
            }
        }
        Assert.True(found, "нет строк с засвеченным участком у полосы ≥ 20 px (шлейф не размазывает контур вправо)");
        Assert.True(worstSd < 0.02, $"цвет внутри засвеченного отрезка не постоянен: std {worstSd:F3}, нужно < 0.02");
    }

    [Fact]
    public void BadTracking_Edges_AreStraightAcrossWidth()
    {
        // Однородный серый вход: краю полосы не должно быть «ступеньки» по x. 9-строчное сглаживание по столбцам.
        const int W = 1920, H = 540;
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (0.5f, 0.5f, 0.5f));
        using var f = gpu.Create<VhsFilter>();
        var img = gpu.Run(f, 0, Neutral(("badTracking", 1f)));

        int? refTop = null, refBot = null;
        foreach (int x in new[] { 32, 480, 960, 1440, 1888 })
        {
            // Сглаженная по 9 строкам яркость столбца; край — где она переходит половину подъёма от базы (0.5) к пику.
            var sm = new double[H];
            for (int y = 4; y < H - 4; y++)
            {
                double s = 0;
                for (int k = -4; k <= 4; k++) s += GpuHarness.Luma(img, x, y + k);
                sm[y] = s / 9;
            }
            double peak = 0;
            for (int y = 4; y < H - 4; y++) peak = Math.Max(peak, sm[y]);
            double thr = 0.5 + 0.5 * (peak - 0.5);
            var rows = new List<int>();
            for (int y = 4; y < H - 4; y++)
                if (sm[y] > thr) rows.Add(y);
            Assert.True(peak > 0.55 && rows.Count > 0, $"в столбце {x} полосы нет (пик {peak:F3})");
            int top = rows.Max(), bot = rows.Min();
            refTop ??= top;
            refBot ??= bot;
            Assert.True(Math.Abs(top - refTop.Value) <= 1, $"верхний край в столбце {x}: строка {top}, в столбце 32: {refTop}");
            Assert.True(Math.Abs(bot - refBot.Value) <= 1, $"нижний край в столбце {x}: строка {bot}, в столбце 32: {refBot}");
        }
    }

    [Fact]
    public void BadTracking_Band_MovesUpAndDown_AndChangesThickness()
    {
        // 600 кадров при 60 fps (10 с): центр полосы побывал в верхней и нижней половине, толщина меняется ≥ 2×.
        const int W = 640, H = 540;
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (0.5f, 0.5f, 0.5f));
        using var f = gpu.Create<VhsFilter>();
        var o = Neutral(("badTracking", 1f));

        bool upper = false, lower = false;
        int minThick = int.MaxValue, maxThick = 0;
        for (long fr = 0; fr < 600; fr += 3)
        {
            var rows = BandRows(gpu.Run(f, fr, o));
            int cnt = 0;
            double sum = 0;
            for (int y = 0; y < H; y++)
                if (rows[y]) { cnt++; sum += y; }
            if (cnt == 0) continue;
            double center = sum / cnt;
            if (center > H / 2.0) upper = true; else lower = true;
            minThick = Math.Min(minThick, cnt);
            maxThick = Math.Max(maxThick, cnt);
        }
        Assert.True(upper && lower, $"центр полосы не бывал в обеих половинах кадра (верх: {upper}, низ: {lower})");
        Assert.True(minThick > 0 && maxThick >= 2 * minThick,
            $"толщина полосы от {minThick} до {maxThick} строк, нужно отличие ≥ 2×");
    }

    /// <summary>Центроид ярких пикселей (яркость > 0.5) строки y; NaN, если ярких мало.</summary>
    private static double LineCentroid(float[,,] img, int y)
    {
        int w = img.GetLength(1);
        double s = 0;
        int n = 0;
        for (int x = 0; x < w; x++)
            if (GpuHarness.Luma(img, x, y) > 0.5f) { s += x; n++; }
        return n < 3 ? double.NaN : s / n;
    }

    /// <summary>Ширина зелёной кромки слева в строке y: число пикселей от x=0 с G > R+0.15 и G > B+0.15 подряд.</summary>
    private static int GreenEdgeWidth(float[,,] img, int y)
    {
        int x = 0;
        while (x < img.GetLength(1) && img[y, x, 1] > img[y, x, 0] + 0.15f && img[y, x, 1] > img[y, x, 2] + 0.15f) x++;
        return x;
    }

    [Fact]
    public void BandClone_BandColumnsCopyTheRowAboveTheBand()
    {
        // Вход: вертикальный градиент по y (g = y/H) и цветные столбцы по x (b чередуется каждые 16 px).
        // Режим клона: строки полосы копируют строку сразу над полосой, столбец в столбец, без осветления.
        const int W = 640, H = 540;
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (x / (float)W, y / (float)H, (x / 16) % 2 == 0 ? 1f : 0f));
        using var f = gpu.Create<VhsFilter>();
        var img = gpu.Run(f, 0, Neutral(("badTracking", 1f), ("bandClone", 1f)));

        // Строки клона: выход по зелёному совпадает с одним и тем же значением (строка-источник), а вход там другой.
        // Значение источника — самое частое среди строк, где выход отличается от входа (края полосы смешаны, их отбрасываем).
        var changed = new List<int>();
        for (int y = 0; y < H; y++)
        {
            double d = 0;
            for (int x = 0; x < W; x++) d += Math.Abs(img[y, x, 1] - y / (float)H);
            if (d / W > 0.01) changed.Add(y);
        }
        Assert.True(changed.Count >= 5, $"полоса клона не найдена: строк {changed.Count}");
        var counts = changed.GroupBy(y => (int)Math.Round(img[y, 0, 1] * 1000)).OrderByDescending(gp => gp.Count()).First();
        int src = (int)Math.Round(counts.Key / 1000.0 * H);   // строка-источник: её зелёный = y/H входа
        var band = changed.Where(y => Math.Abs(img[y, 0, 1] - src / (float)H) < 0.005f).ToList();
        Assert.True(band.Count >= 5, $"строк клона с цветом источника: {band.Count}");

        for (int x = 0; x < W; x += 37)
        {
            float er = x / (float)W, eg = src / (float)H, eb = (x / 16) % 2 == 0 ? 1f : 0f;
            float mr = 0, mg = 0, mb = 0, sr = 0, sg = 0, sb = 0;
            foreach (int y in band) { mr += img[y, x, 0]; mg += img[y, x, 1]; mb += img[y, x, 2]; }
            mr /= band.Count; mg /= band.Count; mb /= band.Count;
            foreach (int y in band)
            {
                sr += (img[y, x, 0] - mr) * (img[y, x, 0] - mr);
                sg += (img[y, x, 1] - mg) * (img[y, x, 1] - mg);
                sb += (img[y, x, 2] - mb) * (img[y, x, 2] - mb);
            }
            Assert.True(Math.Sqrt(sr / band.Count) < 0.01 && Math.Sqrt(sg / band.Count) < 0.01 && Math.Sqrt(sb / band.Count) < 0.01,
                $"столбец {x}: цвет по y в полосе не постоянен");
            Assert.True(Math.Abs(mr - er) <= 0.03 && Math.Abs(mg - eg) <= 0.03 && Math.Abs(mb - eb) <= 0.03,
                $"столбец {x}: цвет полосы ({mr:F3},{mg:F3},{mb:F3}) не равен строке над полосой ({er:F3},{eg:F3},{eb:F3})");
        }
    }

    [Fact]
    public void Tbc_On_LineStaysStraightOnAllRows_Off_LineWobbles()
    {
        // Белая линия 4 px на сером, jitter 1: при tbc строки выровнены — центроид линии одинаков на всех строках.
        const int W = 640, H = 180, x0 = 318, x1 = 322;
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => { float v = x >= x0 && x < x1 ? 1f : 0.3f; return (v, v, v); });
        using var f = gpu.Create<VhsFilter>();

        static (double min, double max) Range(float[,,] img, int h)
        {
            double mn = double.MaxValue, mx = double.MinValue;
            for (int y = 0; y < h; y++)
            {
                double c = LineCentroid(img, y);
                if (double.IsNaN(c)) continue;
                mn = Math.Min(mn, c); mx = Math.Max(mx, c);
            }
            return (mn, mx);
        }

        var on = Range(gpu.Run(f, 0, Neutral(("jitter", 1f), ("tbc", 1f))), H);
        Assert.True(on.max - on.min <= 1.0, $"при tbc=1 центроид линии гуляет на {on.max - on.min:F2} px, нужно ≤ 1");

        var off = Range(gpu.Run(f, 0, Neutral(("jitter", 1f), ("tbc", 0f))), H);
        Assert.True(off.max - off.min > 1.0, $"при tbc=0 jitter должен двигать линию, размах {off.max - off.min:F2} px");
    }

    [Fact]
    public void Tbc_On_LeftGreenEdge_WidthVariesByRow()
    {
        // Зелёная кромка слева: ширина = сдвиг, который компенсировал TBC. По строкам непостоянна (jitter даёт группы по 2 строки).
        const int W = 640, H = 180;
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (0.3f, 0.3f, 0.3f));
        using var f = gpu.Create<VhsFilter>();
        var img = gpu.Run(f, 0, Neutral(("jitter", 1f), ("tbc", 1f)));

        var widths = new List<double>();
        for (int y = 0; y < H; y++) widths.Add(GreenEdgeWidth(img, y));
        double mean = widths.Average();
        double sd = Math.Sqrt(widths.Sum(w => (w - mean) * (w - mean)) / widths.Count);
        Assert.True(widths.All(w => w > 0), "при tbc=1 зелёная кромка должна быть в каждой строке");
        Assert.True(sd >= 0.5, $"std ширины кромки по строкам {sd:F2} px, нужно ≥ 0.5");
    }

    [Fact]
    public void Tbc_Off_NoGreenEdge()
    {
        const int W = 640, H = 180;
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (0.3f, 0.3f, 0.3f));
        using var f = gpu.Create<VhsFilter>();
        var img = gpu.Run(f, 0, Neutral(("jitter", 1f), ("tbc", 0f)));
        for (int y = 0; y < H; y++)
            Assert.True(GreenEdgeWidth(img, y) == 0, $"при tbc=0 зелёная кромка в строке {y}");
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

    [Fact]
    public void NoFullWidthSeamLine_InBottomZone_OnDarkBackground()
    {
        // Светлой линии во всю ширину в нижних 6% кадра быть не должно: средняя яркость строки по всей ширине
        // не выше соседних (±3 строки) более чем на 0.03. Параметры — kassette, тёмный однородный вход.
        const int W = 640, H = 1080;
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (0.05f, 0.05f, 0.05f));
        using var f = gpu.Create<VhsFilter>();
        // Полоса плохого трекинга выключена: её белые чёрточки — честный эффект, а здесь проверяется только шов головок.
        var o = new Dictionary<string, float>
        {
            ["badTracking"] = 0f, ["lumaRes"] = 520f, ["chromaRes"] = 60f,
        };
        foreach (long fr in new long[] { 0, 300, 600 })
        {
            var img = gpu.Run(f, fr, o);
            var mean = new double[H];
            for (int y = 0; y < H; y++)
            {
                double s = 0;
                for (int x = 0; x < W; x++) s += GpuHarness.Luma(img, x, y);
                mean[y] = s / W;
            }
            for (int y = (int)(H * 0.94); y < H - 3; y++)
            {
                double nb = (mean[y - 3] + mean[y + 3]) / 2;
                double d = mean[y] - nb;
                Assert.True(d <= 0.03, $"кадр {fr}, строка {y}: средняя яркость выше соседних на {d:F3}");
            }
        }
    }
}
