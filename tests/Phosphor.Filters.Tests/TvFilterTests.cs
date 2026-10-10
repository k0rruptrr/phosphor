using Phosphor.Filters;
using Phosphor.Gfx;

namespace Phosphor.Filters.Tests;

public class TvFilterTests
{
    private const int W = 1920;
    private const int H = 64;
    private const int ModePal = 0, ModeSecam = 1, ModeNtsc = 2;

    private static readonly (float r, float g, float b)[] Bars =
    {
        (1, 1, 1), (1, 1, 0), (0, 1, 1), (0, 1, 0), (1, 0, 1), (1, 0, 0), (0, 0, 1), (0, 0, 0),
    };

    private static Dictionary<string, float> Mode(int mode, float amount = 1f) =>
        new() { ["mode"] = mode, ["amount"] = amount };

    private static (float u, float v) Uv(float r, float g, float b)
    {
        float y = 0.299f * r + 0.587f * g + 0.114f * b;
        return (0.492f * (b - y), 0.877f * (r - y));
    }

    private static float AngleDiffDeg(float a, float b)
    {
        float d = MathF.Abs(a - b) % (2 * MathF.PI);
        if (d > MathF.PI) d = 2 * MathF.PI - d;
        return d * 180f / MathF.PI;
    }

    // ---------- Статика ----------

    [Fact]
    public void Id_IsTv()
    {
        Assert.Equal("tv", new TvFilter().Id);
    }

    [Fact]
    public void Params_NamesDefaultsAndOptions()
    {
        var ps = new TvFilter().Params;
        Assert.Equal(new[] { "mode", "amount", "hue", "sharpness", "saturation", "fires", "fireSize", "signal", "edge", "bottles", "ghost", "ghostDelay", "fibers" },
            ps.Select(p => p.Name).ToArray());
        Assert.Equal(0.25f, ps[10].Default);   // T5: двоение (эфир), по умолчанию 0.25
        Assert.Equal(0.02f, ps[11].Default);   // T5: сдвиг двоения, по умолчанию 0.02
        Assert.Equal(1f, ps[12].Default);      // волокна: 1 = как на кассете
        Assert.Equal(0.5f, ps[8].Default);   // T4: edge = края гашения, по умолчанию 0.5
        Assert.Equal(0f, ps[9].Default);     // T4: бутылки сверху выключены по умолчанию
        Assert.Equal(1f, ps[6].Default);
        Assert.Equal(0.3f, ps[6].Min);
        Assert.Equal(2.5f, ps[6].Max);
        var mode = ps[0];
        Assert.Equal(new[] { "pal", "secam", "ntsc" }, mode.Options);
        Assert.Equal(0f, mode.Default);
        Assert.Equal(1f, ps[1].Default);
        Assert.Equal(0f, ps[2].Default);
        Assert.Equal(-30f, ps[2].Min);
        Assert.Equal(30f, ps[2].Max);
        Assert.Equal(0.3f, ps[3].Default);
        foreach (var p in ps) Assert.True(p.Min <= p.Default && p.Default <= p.Max, p.Name);
    }

    [Fact]
    public void Shaders_PassStaticChecks()
    {
        var enc = ShaderStatic.Load("Phosphor.Filters.Shaders.tv_encode.frag");
        var dec = ShaderStatic.Load("Phosphor.Filters.Shaders.tv_decode.frag");
        ShaderStatic.CheckSyntax(enc, "tv_encode.frag");
        ShaderStatic.CheckSyntax(dec, "tv_decode.frag");
        ShaderStatic.CheckParams(new TvFilter(), dec);
        Assert.Contains("uniform float p_mode;", enc);
    }

    [Fact]
    public void Shader_ClampsOutputToNonNegative()
    {
        var dec = ShaderStatic.Load("Phosphor.Filters.Shaders.tv_decode.frag");
        Assert.Contains("col = max(col, vec3(0.0));", dec);
        Assert.Contains("max(outc, vec3(0.0))", dec);
    }

    // ---------- GPU ----------

    private static void FillBars(GpuHarness gpu)
    {
        gpu.SetInput((x, y) => Bars[Math.Min(7, x * 8 / W)]);
    }

    [Fact]
    public void Gray_StaysGray_InAllModes()
    {
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (0.5f, 0.5f, 0.5f));
        using var f = gpu.Create<TvFilter>();
        foreach (var mode in new[] { ModePal, ModeSecam, ModeNtsc })
        {
            var img = gpu.Run(f, 0, Mode(mode));
            double chroma = 0, luma = 0;
            int n = 0;
            for (int y = 0; y < H; y++)
            for (int x = 64; x < W - 64; x++)
            {
                chroma += Math.Abs(img[y, x, 0] - img[y, x, 1]) + Math.Abs(img[y, x, 1] - img[y, x, 2]);
                luma += GpuHarness.Luma(img, x, y);
                n++;
            }
            Assert.True(chroma / n / 2 < 0.03, $"mode {mode}: средний цвет не серый");
            Assert.True(Math.Abs(luma / n - 0.5) < 0.05, $"mode {mode}: яркость {luma / n}");
        }
    }

    [Fact]
    public void Amount0_IsNearIdentity()
    {
        using var gpu = new GpuHarness(W, H);
        FillBars(gpu);
        using var f = gpu.Create<TvFilter>();
        var img = gpu.Run(f, 0, Mode(ModeNtsc, 0f));
        double mae = 0;
        int n = 0;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            var (r, g, b) = Bars[Math.Min(7, x * 8 / W)];
            mae += Math.Abs(img[y, x, 0] - r) + Math.Abs(img[y, x, 1] - g) + Math.Abs(img[y, x, 2] - b);
            n += 3;
        }
        Assert.True(mae / n < 0.02, $"MAE {mae / n}");
    }

    [Fact]
    public void Ntsc_CombFilter_CrawlOnlyAtColorTransitions()
    {
        // Верх жёлтый, низ синий: горизонтальная граница цвета. Гребёнка гасит цветность внутри однородных областей,
        // на граничных строках остаётся несущая (ползущие точки), которая меняет знак между кадрами.
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => y < H / 2 ? (1f, 1f, 0f) : (0f, 0f, 1f));
        using var f = gpu.Create<TvFilter>();
        var a = gpu.Run(f, 0, Mode(ModeNtsc));
        var b = gpu.Run(f, 1, Mode(ModeNtsc));

        static float Amp(float[,,] img, int y)
        {
            float min = 1, max = 0;
            for (int x = 280; x < 440; x++)
            {
                float l = GpuHarness.Luma(img, x, y);
                min = Math.Min(min, l); max = Math.Max(max, l);
            }
            return (max - min) / 2;
        }

        float edge = MathF.Max(Amp(a, H / 2 - 1), Amp(a, H / 2));
        Assert.True(edge > 0.01f, $"на граничных строках нет ползущих точек: {edge}");

        float interior = Amp(a, 5);
        Assert.True(interior < 0.01f, $"внутри однородной полосы есть колебания: {interior}");

        double diff = 0;
        for (int x = 280; x < 440; x++) diff += Math.Abs(GpuHarness.Luma(a, x, H / 2 - 1) - GpuHarness.Luma(b, x, H / 2 - 1));
        Assert.True(diff / 160 > 0.005, "картина колебаний не меняется между кадрами");

        gpu.SetInput((x, yy) => (0.5f, 0.5f, 0.5f));
        var g = gpu.Run(f, 0, Mode(ModeNtsc));
        Assert.True(Amp(g, H / 2) < 0.01f, "на сером есть колебания");
    }

    [Fact]
    public void Sharpness_ThinWhiteLine_AllModes()
    {
        // Белая линия 2 px на чёрном: в центре яркое, на расстоянии 4 px от краёв тёмное.
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => x == 960 || x == 961 ? (1f, 1f, 1f) : (0f, 0f, 0f));
        using var f = gpu.Create<TvFilter>();
        foreach (var mode in new[] { ModePal, ModeSecam, ModeNtsc })
        {
            var img = gpu.Run(f, 0, Mode(mode));
            float center = (GpuHarness.Luma(img, 960, H / 2) + GpuHarness.Luma(img, 961, H / 2)) / 2;
            float left = GpuHarness.Luma(img, 956, H / 2);
            float right = GpuHarness.Luma(img, 965, H / 2);
            // SECAM: режекторный фильтр в приёмниках реально мылил картинку, поэтому для него порог 0.5.
            float need = mode == ModeSecam ? 0.5f : 0.6f;
            Assert.True(center >= need, $"mode {mode}: центр линии {center} (нужно {need})");
            Assert.True(left <= 0.2f && right <= 0.2f, $"mode {mode}: 4 px от края {left} / {right}");
        }
    }

    [Fact]
    public void Sharpness_TextLikeStripes_MeanBrightnessKept_AllModes()
    {
        // Штрихи по 6 px белых и 4 px чёрных (период 10 px): средняя яркость не должна падать больше чем на 20%.
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => x % 10 < 6 ? (1f, 1f, 1f) : (0f, 0f, 0f));
        using var f = gpu.Create<TvFilter>();
        double inMean = 0;
        int inN = 0;
        for (int x = 100; x < 1800; x++) { inMean += x % 10 < 6 ? 1 : 0; inN++; }
        inMean /= inN;
        foreach (var mode in new[] { ModePal, ModeSecam, ModeNtsc })
        {
            var img = gpu.Run(f, 0, Mode(mode));
            double outMean = 0;
            int n = 0;
            for (int y = 8; y < H - 8; y++)
            for (int x = 100; x < 1800; x++) { outMean += GpuHarness.Luma(img, x, y); n++; }
            outMean /= n;
            Assert.True(outMean >= 0.8 * inMean, $"mode {mode}: средняя яркость {outMean:F3} при входе {inMean:F3}");
        }
    }

    [Fact]
    public void Pal_HueOfBarsPreserved()
    {
        using var gpu = new GpuHarness(W, H);
        FillBars(gpu);
        using var f = gpu.Create<TvFilter>();
        var img = gpu.Run(f, 0, Mode(ModePal));
        for (int bar = 1; bar <= 6; bar++)
        {
            var (r, g, b) = Bars[bar];
            var (ui, vi) = Uv(r, g, b);
            float ai = MathF.Atan2(vi, ui);
            float sr = 0, sg = 0, sb = 0;
            int n = 0;
            int x0 = bar * W / 8 + 40, x1 = (bar + 1) * W / 8 - 40;
            for (int y = 0; y < H; y++)
            for (int x = x0; x < x1; x++)
            {
                sr += img[y, x, 0]; sg += img[y, x, 1]; sb += img[y, x, 2]; n++;
            }
            var (uo, vo) = Uv(sr / n, sg / n, sb / n);
            float ao = MathF.Atan2(vo, uo);
            Assert.True(AngleDiffDeg(ai, ao) < 10f, $"полоса {bar}: угол {AngleDiffDeg(ai, ao):F1}°");
        }
    }

    [Fact]
    public void Pal_VerticalEdgeIsBlurredOverTwoRows()
    {
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => y < H / 2 ? (1f, 0f, 0f) : (0f, 0f, 1f));
        using var f = gpu.Create<TvFilter>();
        var img = gpu.Run(f, 0, Mode(ModePal));
        int x = W / 2;
        int mixed = 0;
        for (int y = 0; y < H; y++)
        {
            float d = img[y, x, 0] - img[y, x, 2];
            if (MathF.Abs(d) < 0.8f) mixed++;
        }
        Assert.True(mixed >= 2, $"смешанных строк {mixed}");
    }

    [Fact]
    public void Secam_AlternatingLinesAreVerticallyAveraged()
    {
        // Цвет = цветность (UV), яркость не считается: разница цвета соседних строк < 30% разницы входных цветов.
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => y % 2 == 0 ? (1f, 0f, 0f) : (0f, 0f, 1f));
        using var f = gpu.Create<TvFilter>();
        var img = gpu.Run(f, 0, Mode(ModeSecam));
        var (ur, vr) = Uv(1f, 0f, 0f);
        var (ub, vb) = Uv(0f, 0f, 1f);
        float inDiff = MathF.Sqrt((ur - ub) * (ur - ub) + (vr - vb) * (vr - vb));
        double outDiff = 0;
        int n = 0;
        for (int y = 8; y < H - 8; y++)
        for (int x = 200; x < 1700; x++)
        {
            var (u0, v0) = Uv(img[y, x, 0], img[y, x, 1], img[y, x, 2]);
            var (u1, v1) = Uv(img[y + 1, x, 0], img[y + 1, x, 1], img[y + 1, x, 2]);
            outDiff += Math.Sqrt((u0 - u1) * (u0 - u1) + (v0 - v1) * (v0 - v1));
            n++;
        }
        outDiff /= n;
        Assert.True(outDiff < 0.3 * inDiff, $"разница цветности соседних строк {outDiff:F3} при входе {inDiff:F3}");
    }
    [Fact]
    public void Secam_FireStreaksOnlyOnColorEdge()
    {
        // Полоса сигналов опознавания проверяется в Secam_EdgeBand_GreenStripeAtTopEdge; здесь выключена, чтобы мерить только факелы.
        var noBand = new Dictionary<string, float>(Mode(ModeSecam)) { ["edge"] = 0f };
        using var gpu = new GpuHarness(W, H);
        int edge = W / 2;
        gpu.SetInput((x, y) => x < edge ? (1f, 0f, 0f) : (0.5f, 0.5f, 0.5f));
        using var f = gpu.Create<TvFilter>();
        var img = gpu.Run(f, 0, noBand);
        int longest = 0;
        for (int y = 0; y < H; y++)
        {
            int run = 0;
            for (int x = edge + 2; x < edge + 120; x++)
            {
                float sat = MathF.Max(img[y, x, 0], MathF.Max(img[y, x, 1], img[y, x, 2]))
                          - MathF.Min(img[y, x, 0], MathF.Min(img[y, x, 1], img[y, x, 2]));
                if (sat > 0.05f) { run++; longest = Math.Max(longest, run); }
                else run = 0;
            }
        }
        Assert.True(longest > 8, $"самый длинный цветной отрезок {longest}");

        gpu.SetInput((x, y) => (0.5f, 0.5f, 0.5f));
        var g = gpu.Run(f, 0, noBand);
        int grayRun = 0;
        for (int y = 0; y < H; y++)
        {
            int run = 0;
            for (int x = edge + 2; x < edge + 120; x++)
            {
                float sat = MathF.Max(g[y, x, 0], MathF.Max(g[y, x, 1], g[y, x, 2]))
                          - MathF.Min(g[y, x, 0], MathF.Min(g[y, x, 1], g[y, x, 2]));
                if (sat > 0.05f) { run++; grayRun = Math.Max(grayRun, run); }
                else run = 0;
            }
        }
        Assert.True(grayRun <= 8, $"на сером входе есть цветной отрезок {grayRun} px");
    }


    [Fact]
    public void Secam_GrayRowLumaHasNoCarrierStripes()
    {
        // Несущая SECAM на сером — слабая мелкая сетка (так было и в жизни: FM-несущая идёт всегда, её видно даже
        // на ч/б телевизорах). Полная режекция убивает чёткость глифов (период несущей ≈ толщина штриха), поэтому
        // допускаем остаток до 0.02 — заметно в упор, не мешает. Решение координатора, см. отчёт волны 4.
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (0.5f, 0.5f, 0.5f));
        using var f = gpu.Create<TvFilter>();
        var img = gpu.Run(f, 0, Mode(ModeSecam));
        double sumStd = 0;
        for (int y = 8; y < H - 8; y++)
        {
            double mean = 0, sq = 0;
            int n = 0;
            for (int x = 200; x < 1700; x++)
            {
                double l = GpuHarness.Luma(img, x, y);
                mean += l; sq += l * l; n++;
            }
            mean /= n;
            sumStd += Math.Sqrt(Math.Max(0, sq / n - mean * mean));
        }
        double std = sumStd / (H - 16);
        Assert.True(std < 0.02, $"стандартное отклонение яркости по строке {std:F4}");
    }

    [Fact]
    public void Secam_ThinPinkStripes_KeepHue()
    {
        // Тонкий розовый текст 0xFF66CC на чёрном (штрихи 6 px через 10 px): средний цвет в UV в пределах 45°.
        using var gpu = new GpuHarness(W, H);
        var pink = (1f, 0.4f, 0.8f);
        gpu.SetInput((x, y) => x % 10 < 6 ? pink : (0f, 0f, 0f));
        using var f = gpu.Create<TvFilter>();
        var img = gpu.Run(f, 0, Mode(ModeSecam));
        float sr = 0, sg = 0, sb = 0;
        int n = 0;
        for (int y = 8; y < H - 8; y++)
        for (int x = 200; x < 1700; x++)
        {
            sr += img[y, x, 0]; sg += img[y, x, 1]; sb += img[y, x, 2]; n++;
        }
        var (ui, vi) = Uv(pink.Item1, pink.Item2, pink.Item3);
        var (uo, vo) = Uv(sr / n, sg / n, sb / n);
        float diff = AngleDiffDeg(MathF.Atan2(vi, ui), MathF.Atan2(vo, uo));
        Assert.True(diff < 45f, $"угол оттенка розового {diff:F1}°");
    }

    // ---------- SECAM как в жизни (T1) ----------

    private static (float cb, float cr) Ycc(float r, float g, float b) =>
        (-0.1687f * r - 0.3313f * g + 0.5f * b, 0.5f * r - 0.4187f * g - 0.0813f * b);

    private static float Chroma(float[,,] img, int x, int y)
    {
        var (cb, cr) = Ycc(img[y, x, 0], img[y, x, 1], img[y, x, 2]);
        return MathF.Sqrt(cb * cb + cr * cr);
    }

    private static Dictionary<string, float> Secam(float signal = 1f, float fires = 0f, float edge = 0f, float bottles = 0f) =>
        new() { ["mode"] = ModeSecam, ["amount"] = 1f, ["signal"] = signal, ["fires"] = fires, ["edge"] = edge, ["bottles"] = bottles };

    [Fact]
    public void Secam_AchromaticGlyphs_NoRainbow()
    {
        // Белые глифы 6x12 в сетке 16 px на чёрном (ASCII-сцена): цветности почти не должно быть.
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (x % 16 < 6 && y % 16 < 12) ? (1f, 1f, 1f) : (0f, 0f, 0f));
        using var f = gpu.Create<TvFilter>();
        var img = gpu.Run(f, 0, Secam());
        int colored = 0, n = 0;
        for (int y = 8; y < H - 8; y++)
        for (int x = 64; x < W - 64; x++)
        {
            if (Chroma(img, x, y) > 0.08f) colored++;
            n++;
        }
        double frac = (double)colored / n;
        Assert.True(frac <= 0.01, $"доля цветных пикселей {frac:P2} (нужно ≤ 1%)");
    }

    [Fact]
    public void Secam_Bars75_PastelAndHueKept()
    {
        using var gpu = new GpuHarness(W, H);
        var bars75 = new (float r, float g, float b)[]
        {
            (.75f, .75f, .75f), (.75f, .75f, 0), (0, .75f, .75f), (0, .75f, 0),
            (.75f, 0, .75f), (.75f, 0, 0), (0, 0, .75f), (0, 0, 0),
        };
        gpu.SetInput((x, y) => bars75[Math.Min(7, x * 8 / W)]);
        using var f = gpu.Create<TvFilter>();
        var img = gpu.Run(f, 0, Secam());
        for (int bar = 1; bar <= 6; bar++)
        {
            var (r, g, b) = bars75[bar];
            var (cbi, cri) = Ycc(r, g, b);
            float satIn = MathF.Sqrt(cbi * cbi + cri * cri);
            float ai = MathF.Atan2(cri, cbi);
            double satOut = 0;
            float sr = 0, sg = 0, sb = 0;
            int n = 0;
            int x0 = bar * W / 8 + 40, x1 = (bar + 1) * W / 8 - 40;
            for (int y = 8; y < H - 8; y++)
            for (int x = x0; x < x1; x++)
            {
                satOut += Chroma(img, x, y);
                sr += img[y, x, 0]; sg += img[y, x, 1]; sb += img[y, x, 2]; n++;
            }
            satOut /= n;
            var (cbo, cro) = Ycc(sr / n, sg / n, sb / n);
            float ao = MathF.Atan2(cro, cbo);
            double ratio = satOut / satIn;
            Assert.True(ratio >= 0.45 && ratio <= 0.75, $"полоса {bar}: насыщенность {ratio:F3}");
            Assert.True(AngleDiffDeg(ai, ao) <= 20f, $"полоса {bar}: оттенок {AngleDiffDeg(ai, ao):F1}°");
        }
    }

    [Fact]
    public void Secam_VerticalRedBlueEdge_ChromaSofterThanLuma()
    {
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => x < W / 2 ? (1f, 0f, 0f) : (0f, 0f, 1f));
        using var f = gpu.Create<TvFilter>();
        var img = gpu.Run(f, 0, Secam());
        int row = H / 2;
        var yv = new float[W];
        var crv = new float[W];
        for (int x = 0; x < W; x++)
        {
            yv[x] = GpuHarness.Luma(img, x, row);
            crv[x] = Ycc(img[row, x, 0], img[row, x, 1], img[row, x, 2]).cr;
        }
        float wY = TransitionWidth(yv, W / 2);
        float wC = TransitionWidth(crv, W / 2);
        Assert.True(wC >= 3f * wY, $"ширина перехода Cr {wC} px, Y {wY} px");
    }

    // Ширина перехода 10%→90% между средними уровнями слева и справа от центра.
    private static float TransitionWidth(float[] v, int center)
    {
        float lo = 0, hi = 0;
        for (int i = center - 200; i < center - 150; i++) lo += v[i];
        for (int i = center + 150; i < center + 200; i++) hi += v[i];
        lo /= 50; hi /= 50;
        float t10 = float.NaN, t90 = float.NaN;
        for (int x = center - 150; x < center + 150; x++)
        {
            float t = (v[x] - lo) / (hi - lo);
            if (float.IsNaN(t10) && t >= 0.1f) t10 = x;
            if (float.IsNaN(t90) && t >= 0.9f) { t90 = x; break; }
        }
        return t90 - t10;
    }

    [Fact]
    public void Secam_Fires_RedStreaksOnDarkContours()
    {
        using var gpu = new GpuHarness(W, H);
        // Чёрный кадр с белыми прямоугольниками: контуры дают перепады яркости.
        gpu.SetInput((x, y) => ((x / 220) % 2 == 1 && (x % 220) < 60 && y > 16 && y < 48) ? (1f, 1f, 1f) : (0f, 0f, 0f));
        using var f = gpu.Create<TvFilter>();

        var on = gpu.Run(f, 0, Secam(fires: 1f));
        int longest = 0, red = 0, blue = 0;
        for (int y = 8; y < H - 8; y++)
        {
            int run = 0;
            for (int x = 0; x < W; x++)
            {
                if (Chroma(on, x, y) > 0.05f) { run++; longest = Math.Max(longest, run); }
                else run = 0;
            }
        }
        for (int y = 8; y < H - 8; y++)
        for (int x = 0; x < W; x++)
        {
            var (cb, cr) = Ycc(on[y, x, 0], on[y, x, 1], on[y, x, 2]);
            if (cr > 0.1f) red++;
            if (cb > 0.1f) blue++;
        }
        Assert.True(longest >= 8, $"самый длинный цветной отрезок {longest}");
        Assert.True(red > blue, $"красных {red}, синих {blue}");

        var off = gpu.Run(f, 0, Secam(fires: 0f));
        int offLongest = 0;
        for (int y = 8; y < H - 8; y++)
        {
            int run = 0;
            for (int x = 0; x < W; x++)
            {
                if (Chroma(off, x, y) > 0.05f) { run++; offLongest = Math.Max(offLongest, run); }
                else run = 0;
            }
        }
        Assert.True(offLongest < 8, $"при fires=0 есть отрезок {offLongest} px");
    }

    [Fact]
    public void Secam_WeakSignal_ColoredStreaksAlternateByRow()
    {
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (0.5f, 0.5f, 0.5f));
        using var f = gpu.Create<TvFilter>();

        // Пользователь: «даже на минимуме факелов их слишком много» — шум слабого сигнала тоже под ползунком «факелы».
        var weak = gpu.Run(f, 0, Secam(signal: 0.3f, fires: 1f));
        int colored = 0, n = 0;
        for (int y = 8; y < H - 8; y++)
        for (int x = 200; x < 1700; x++)
        {
            if (Chroma(weak, x, y) > 0.08f) colored++;
            n++;
        }
        double coloredFrac = (double)colored / n;

        // Корреляция Cr−Cb соседних ЛИНИЙ должна быть отрицательной: черточки чередуются по линиям.
        // Почему по линиям, а не по пиксельным строкам: SECAM несёт цветность линией из двух пиксельных строк
        // (2L и 2L+1), у них общая цветность. Корреляция пиксельных строк внутри линии была бы +1 и скрывала
        // чередование между линиями. Порог < 0 не меняется; меняется только определение «соседних строк».
        double corrSum = 0;
        int pairs = 0;
        for (int line = 4; line < H / 2 - 4; line++)
        {
            double ma = 0, mb = 0;
            int m = 0;
            for (int x = 200; x < 1700; x++) { ma += LineDiff(weak, x, line); mb += LineDiff(weak, x, line + 1); m++; }
            ma /= m; mb /= m;
            double sab = 0, saa = 0, sbb = 0;
            for (int x = 200; x < 1700; x++)
            {
                double a = LineDiff(weak, x, line) - ma, b = LineDiff(weak, x, line + 1) - mb;
                sab += a * b; saa += a * a; sbb += b * b;
            }
            corrSum += sab / Math.Sqrt(saa * sbb + 1e-12);
            pairs++;
        }
        double corr = corrSum / pairs;
        Assert.True(coloredFrac >= 0.10, $"цветных {coloredFrac:P1} (нужно ≥ 10%), корреляция соседних линий {corr:F3}");
        Assert.True(corr < 0, $"корреляция соседних линий {corr:F3} (нужно < 0), цветных {coloredFrac:P1}");

        var clean = gpu.Run(f, 0, Secam(signal: 1f));
        int cleanColored = 0;
        for (int y = 8; y < H - 8; y++)
        for (int x = 200; x < 1700; x++)
            if (Chroma(clean, x, y) > 0.08f) cleanColored++;
        Assert.True((double)cleanColored / n < 0.01, $"при signal=1 цветных {(double)cleanColored / n:P2}");
    }

    private static float Diff(float[,,] img, int x, int y)
    {
        var (cb, cr) = Ycc(img[y, x, 0], img[y, x, 1], img[y, x, 2]);
        return cr - cb;
    }

    // Цвет линии SECAM = среднее двух её пиксельных строк (2L и 2L+1).
    private static float LineDiff(float[,,] img, int x, int line) =>
        0.5f * (Diff(img, x, 2 * line) + Diff(img, x, 2 * line + 1));

    [Fact]
    public void Secam_EdgeBand_GreenStripeAtTopEdge()
    {
        // ИЗМЕНЕНИЕ ТРЕБОВАНИЯ (T2): зелёная полоса перенесена с левого края на ВЕРХНИЙ край кадра
        // (сигналы опознавания, «бутылки», из кадрового гасящего импульса). Прежний тест
        // Secam_EdgeBand_GreenAtLeftEdge (вертикальная кромка слева) удалён как отражающий старое требование.
        // Полоса 2.8% высоты (T3: было 1.4%): на общем харнессе H = 64 это меньше строки, поэтому тест берёт кадр 1920x1080.
        const int HB = 1080;
        using var gpu = new GpuHarness(W, HB);
        gpu.SetInput((x, y) => (0f, 0f, 0f));
        using var f = gpu.Create<TvFilter>();
        int bandRows = (int)(0.028 * HB);

        float[] MeanRgb(float[,,] img, int y0, int y1, int x0, int x1)
        {
            var m = new float[3];
            int n = 0;
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                m[0] += img[y, x, 0]; m[1] += img[y, x, 1]; m[2] += img[y, x, 2]; n++;
            }
            return new[] { m[0] / n, m[1] / n, m[2] / n };
        }

        // Полоса: верхние 2.8% высоты кадра (строка 0 — верх). T3: высота полосы 1.4% → 2.8%, bandRows 15 → 30.
        // T4 (решение пользователя): полоса теперь «бутылки» (bottles=1, по умолчанию выкл). Чёрное поле стало
        // зеленоватым по требованию, поэтому проверку «слева под полосой нет зелёного» снята; «без полосы» — сравнение
        // с bottles=0 на том же кадре: полоса должна добавлять зелёный заметно (> 0.05).
        var on = gpu.Run(f, 0, Secam(bottles: 1f));
        var top = MeanRgb(on, 0, bandRows, 0, W);
        Assert.True(top[1] - MathF.Max(top[0], top[2]) >= 0.05f,
            $"полоса сверху: G {top[1]:F3}, R {top[0]:F3}, B {top[2]:F3}");

        var off = MeanRgb(gpu.Run(f, 0, Secam(bottles: 0f)), 0, bandRows, 0, W);
        Assert.True((top[1] - MathF.Max(top[0], top[2])) - (off[1] - MathF.Max(off[0], off[2])) > 0.05f,
            $"полоса не добавляет зелёного: вкл G {top[1]:F3}/R {top[0]:F3}/B {top[2]:F3}, выкл G {off[1]:F3}/R {off[0]:F3}/B {off[2]:F3}");
    }

    // ---------- Бутылки сигналов опознавания (T3) ----------
    // Полоса на кадре 1920x1080, edge=1, чёрный вход. Зелёный канал G = яркость бутылки (у Db/Dr G одинаков).

    private static float[,,] BottleFrame(GpuHarness gpu, TvFilter f)
    {
        gpu.SetInput((x, y) => (0f, 0f, 0f));
        return gpu.Run(f, 0, Secam(bottles: 1f));
    }

    [Fact]
    public void Secam_EdgeBand_ThreeToFiveBottles()
    {
        // Средняя строка полосы: отдельные яркие участки (G > 0.1, длиннее 5% ширины) — от 3 до 5,
        // самый длинный ≥ 12% ширины. Раньше полоса была сплошной: один участок на всю ширину.
        const int HB = 1080;
        using var gpu = new GpuHarness(W, HB);
        using var f = gpu.Create<TvFilter>();
        var img = BottleFrame(gpu, f);
        int bandRows = (int)(0.028 * HB);
        int mid = bandRows / 2;

        var runs = new List<int>();
        int run = 0;
        for (int x = 0; x < W; x++)
        {
            if (img[mid, x, 1] > 0.1f) run++;
            else { if (run > 0) runs.Add(run); run = 0; }
        }
        if (run > 0) runs.Add(run);
        var bottles = runs.Where(r => r > 0.05 * W).ToList();

        Assert.True(bottles.Count >= 3 && bottles.Count <= 5, $"ярких участков {bottles.Count}: {string.Join(",", bottles)}");
        Assert.True(bottles.Max() >= 0.12 * W, $"самый длинный участок {bottles.Max()} px");
    }

    [Fact]
    public void Secam_EdgeBand_LeftEdgeIsNotRuledLine()
    {
        // Аналоговость: левый край первой бутылки по строкам полосы гуляет, стандартное отклонение ≥ 1 px.
        const int HB = 1080;
        using var gpu = new GpuHarness(W, HB);
        using var f = gpu.Create<TvFilter>();
        var img = BottleFrame(gpu, f);
        int bandRows = (int)(0.028 * HB);
        // Нижние строки полосы — мягкий край (маска гаснет у bandH), там бутылки слабее порога; берём полностью яркие строки.
        int litRows = (int)(0.6 * bandRows);

        var xs = new List<double>();
        for (int y = 0; y < litRows; y++)
        {
            int x0 = -1;
            for (int x = 0; x < W / 2; x++)
                if (img[y, x, 1] > 0.1f) { x0 = x; break; }
            Assert.True(x0 >= 0, $"в строке {y} нет первой бутылки");
            xs.Add(x0);
        }
        double mean = xs.Average();
        double sd = Math.Sqrt(xs.Sum(v => (v - mean) * (v - mean)) / xs.Count);
        Assert.True(sd >= 1.0, $"стандартное отклонение левого края {sd:F2} px");
    }

    [Fact]
    public void Secam_EdgeBand_NeckIsThinnerThanBody()
    {
        // Горлышко у правого конца бутылки: светящаяся высота меньше, чем в середине тела.
        const int HB = 1080;
        using var gpu = new GpuHarness(W, HB);
        using var f = gpu.Create<TvFilter>();
        var img = BottleFrame(gpu, f);
        int bandRows = (int)(0.028 * HB);

        // Первый яркий участок в верхней строке полосы (она лежит внутри полосы при любом дыхании/дрожании).
        int a = -1, b = -1;
        for (int x = 0; x < W; x++)
        {
            if (img[0, x, 1] > 0.1f) { if (a < 0) a = x; }
            else if (a >= 0) { b = x; break; }
        }
        Assert.True(a >= 0 && b > a && b - a >= 0.05 * W, $"нет бутылки: a={a}, b={b}");

        int litRows = (int)(0.6 * bandRows);
        int Height(int x)
        {
            int h = 0;
            for (int y = 0; y < litRows; y++) if (img[y, x, 1] > 0.1f) h++;
            return h;
        }
        int body = Height(a + (int)(0.4 * (b - a)));
        int neck = Height(a + (int)(0.9 * (b - a)));
        Assert.True(neck < body, $"высота горлышка {neck} px, высота тела {body} px");
    }

    // ---------- T4: SECAM как на референсе VHS-дубляжа (чёрный, штрихи, хвост, ровные поля) ----------

    private static double StdDev(IEnumerable<double> v)
    {
        var a = v.ToArray();
        double m = a.Average();
        return Math.Sqrt(a.Sum(t => (t - m) * (t - m)) / a.Length);
    }

    [Fact]
    public void Secam_Black_GreenishLiftAndFiberRows()
    {
        // Чёрное поле: зеленоватое, G в диапазоне 6..20/255; вдоль строки (200 px) шум слабее, чем поперёк (между строками).
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (0f, 0f, 0f));
        using var f = gpu.Create<TvFilter>();
        var img = gpu.Run(f, 0, Mode(ModeSecam));
        double r = 0, g = 0, b = 0;
        int n = 0;
        for (int y = 8; y < H - 8; y++)
        for (int x = 64; x < W - 64; x++) { r += img[y, x, 0]; g += img[y, x, 1]; b += img[y, x, 2]; n++; }
        r /= n; g /= n; b /= n;
        Assert.True(g > r && g > b, $"средний цвет чёрного R {r:F4} G {g:F4} B {b:F4}");
        Assert.True(g >= 6 / 255.0 && g <= 20 / 255.0, $"средний G чёрного {g * 255:F1}/255 (нужно 6..20)");

        var along = new List<double>();
        for (int y = 8; y < H - 8; y++)
        for (int x0 = 200; x0 + 200 <= 1700; x0 += 200)
        {
            var seg = new List<double>();
            for (int x = x0; x < x0 + 200; x++) seg.Add(GpuHarness.Luma(img, x, y));
            along.Add(StdDev(seg));
        }
        var across = new List<double>();
        for (int x = 200; x < 1700; x += 50)
        {
            var col = new List<double>();
            for (int y = 8; y < H - 8; y++) col.Add(GpuHarness.Luma(img, x, y));
            across.Add(StdDev(col));
        }
        double sAlong = along.Average(), sAcross = across.Average();
        Assert.True(sAlong < sAcross, $"шум вдоль строки {sAlong:F5}, поперёк {sAcross:F5}");
    }

    [Fact]
    public void Secam_FiresZero_NoStreaksEvenOnWeakSignal()
    {
        // Пользователь: «даже на минимуме факелов их слишком много». fires=0 — штрихов нет совсем, при любом качестве приёма.
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (x / 64) % 2 == 0 ? (0.05f, 0.05f, 0.05f) : (0.9f, 0.9f, 0.9f));
        using var f = gpu.Create<TvFilter>();
        var img = gpu.Run(f, 3, Secam(signal: 0.3f, fires: 0f));
        int longest = 0;
        for (int y = 8; y < H - 8; y++)
        {
            int run = 0;
            for (int x = 200; x < 1700; x++)
            {
                if (Chroma(img, x, y) > 0.08f) { run++; longest = Math.Max(longest, run); }
                else run = 0;
            }
        }
        Assert.True(longest < 8, $"при fires=0 и слабом сигнале есть цветной штрих {longest} px");
    }

    [Fact]
    public void Secam_FibersZero_FlatFieldIsSmooth()
    {
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (0.3f, 0.5f, 0.3f));
        using var f = gpu.Create<TvFilter>();
        var on = gpu.Run(f, 5, new Dictionary<string, float>(Secam()) { ["fibers"] = 2f });
        var off = gpu.Run(f, 5, new Dictionary<string, float>(Secam()) { ["fibers"] = 0f });
        double RowSpread(float[,,] img)
        {
            var means = new List<double>();
            for (int y = 8; y < H - 8; y++)
            {
                double s = 0;
                for (int x = 200; x < 1700; x++) s += img[y, x, 1];
                means.Add(s / 1500);
            }
            double m = means.Average();
            return Math.Sqrt(means.Sum(v => (v - m) * (v - m)) / means.Count);
        }
        double sOn = RowSpread(on), sOff = RowSpread(off);
        Assert.True(sOff < sOn * 0.25, $"волокна 0: разброс строк {sOff:F5}, волокна 2: {sOn:F5}");
    }

    [Fact]
    public void Secam_FiberNoise_ChangesEveryFrame()
    {
        // Аналоговый шум новый в каждом поле: соседние кадры без факелов и краёв (остаются только волокна) должны отличаться.
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (0.3f, 0.5f, 0.3f));
        using var f = gpu.Create<TvFilter>();
        var a = gpu.Run(f, 10, Secam());
        var b = gpu.Run(f, 11, Secam());
        double diff = 0;
        int n = 0;
        for (int y = 8; y < H - 8; y++)
        for (int x = 200; x < 1700; x++)
        {
            diff += Math.Abs(a[y, x, 1] - b[y, x, 1]);
            n++;
        }
        Assert.True(diff / n > 0.002, $"средняя разница соседних кадров {diff / n:F5} — шум стоит на месте");
    }

    [Fact]
    public void Secam_BlackFires_LongMultiColorStreaks()
    {
        // Чёрный вход, fires=1, 60 кадров: длинные (≥ 80 px) цветные штрихи, среди них с G-доминантой и с B-доминантой.
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (0f, 0f, 0f));
        using var f = gpu.Create<TvFilter>();
        int longRuns = 0, greenRuns = 0, blueRuns = 0;
        for (int frame = 0; frame < 60; frame++)
        {
            var img = gpu.Run(f, frame, Secam(fires: 1f));
            for (int y = 8; y < H - 8; y++)
            {
                int start = -1;
                for (int x = 0; x <= W; x++)
                {
                    bool on = x < W && Chroma(img, x, y) > 0.06f;
                    if (on && start < 0) start = x;
                    if (!on && start >= 0)
                    {
                        int len = x - start;
                        if (len >= 80)
                        {
                            longRuns++;
                            double sr = 0, sg = 0, sb = 0;
                            for (int k = start; k < x; k++) { sr += img[y, k, 0]; sg += img[y, k, 1]; sb += img[y, k, 2]; }
                            sr /= len; sg /= len; sb /= len;
                            if (sg - Math.Max(sr, sb) > 0.02) greenRuns++;
                            if (sb - Math.Max(sr, sg) > 0.02) blueRuns++;
                        }
                        start = -1;
                    }
                }
            }
        }
        Assert.True(longRuns > 0, "длинных штрихов нет");
        Assert.True(greenRuns > 0, $"штрихов с G-доминантой нет (всего {longRuns})");
        Assert.True(blueRuns > 0, $"штрихов с B-доминантой нет (всего {longRuns})");
    }

    [Fact]
    public void Secam_WhiteRect_GreenTailRightOnly()
    {
        // Белый прямоугольник 100 px на чёрном (fires=0, чтобы мерить только хвост): справа в пределах 3 ширин — зелёный хвост, слева — нет.
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (x >= 800 && x < 900 && y >= 20 && y < 44) ? (1f, 1f, 1f) : (0f, 0f, 0f));
        using var f = gpu.Create<TvFilter>();
        var img = gpu.Run(f, 0, Mode(ModeSecam));
        double rR = 0, rG = 0, rB = 0, lR = 0, lG = 0, lB = 0;
        int rn = 0, ln = 0;
        for (int y = 20; y < 44; y++)
        {
            for (int x = 905; x < 1190; x++) { rR += img[y, x, 0]; rG += img[y, x, 1]; rB += img[y, x, 2]; rn++; }
            for (int x = 610; x < 795; x++) { lR += img[y, x, 0]; lG += img[y, x, 1]; lB += img[y, x, 2]; ln++; }
        }
        rR /= rn; rG /= rn; rB /= rn; lR /= ln; lG /= ln; lB /= ln;
        Assert.True(rG > rR && rG > rB, $"справа от текста R {rR:F4} G {rG:F4} B {rB:F4}");
        // T4: чёрное поле уже зеленоватое по требованию (подъём чёрного), поэтому «слева нет хвоста» проверяем
        // относительно этого фона: справа зелёный избыток заметно больше, чем слева.
        double rExc = rG - Math.Max(rR, rB), lExc = lG - Math.Max(lR, lB);
        Assert.True(rExc > lExc, $"хвост справа {rExc:F4}, слева {lExc:F4}");
    }

    [Fact]
    public void Secam_EdgeLeftLine_RaggedBrightStripe()
    {
        // T4 (решение пользователя): edge=1 — светлая рваная вертикальная линия слева, зона 1.2–1.8% ширины.
        const int HB = 1080;
        using var gpu = new GpuHarness(W, HB);
        gpu.SetInput((x, y) => (0f, 0f, 0f));
        using var f = gpu.Create<TvFilter>();
        int x0 = (int)(0.012 * W), x1 = (int)(0.018 * W);

        var on = gpu.Run(f, 0, Secam(edge: 1f));
        double mean = 0;
        int n = 0;
        var argmax = new List<double>();
        for (int y = 8; y < HB - 8; y++)
        {
            int best = x0;
            float bv = -1f;
            for (int x = x0; x < x1; x++)
            {
                float l = GpuHarness.Luma(on, x, y);
                mean += l; n++;
                if (l > bv) { bv = l; best = x; }
            }
            argmax.Add(best);
        }
        mean /= n;
        Assert.True(mean > 0.3, $"средняя яркость зоны линии {mean:F3} (нужно > 0.3)");
        double sd = StdDev(argmax);
        Assert.True(sd >= 0.5, $"std x самого яркого пикселя по строкам {sd:F2} px (нужно ≥ 0.5)");

        var off = gpu.Run(f, 0, Secam(edge: 0f));
        double offMean = 0;
        for (int y = 8; y < HB - 8; y++)
            for (int x = x0; x < x1; x++) offMean += GpuHarness.Luma(off, x, y);
        offMean /= n;
        Assert.True(offMean < 0.1, $"при edge=0 линии нет, но зона яркая: {offMean:F3}");
    }

    [Fact]
    public void Secam_WhiteRect_TailIsBackgroundHueRotated()
    {
        // T4 (решение пользователя): хвост = цветность фона, повёрнутая по оттенку на −120°.
        // На красном фоне хвост сине-фиолетовый (B > G), на зелёном — красный (R > B).
        static (double dr, double dg, double db) Tail(float br, float bg, float bb)
        {
            using var gpu = new GpuHarness(W, H);
            gpu.SetInput((x, y) => (x >= 800 && x < 900 && y >= 20 && y < 44) ? (1f, 1f, 1f) : (br, bg, bb));
            using var f = gpu.Create<TvFilter>();
            var img = gpu.Run(f, 0, Secam(fires: 0.01f));
            double rR = 0, rG = 0, rB = 0, lR = 0, lG = 0, lB = 0;
            int rn = 0, ln = 0;
            for (int y = 20; y < 44; y++)
            {
                for (int x = 905; x < 1190; x++) { rR += img[y, x, 0]; rG += img[y, x, 1]; rB += img[y, x, 2]; rn++; }
                for (int x = 610; x < 795; x++) { lR += img[y, x, 0]; lG += img[y, x, 1]; lB += img[y, x, 2]; ln++; }
            }
            return (rR / rn - lR / ln, rG / rn - lG / ln, rB / rn - lB / ln);
        }

        var red = Tail(0.6f, 0.1f, 0.1f);
        Assert.True(red.db > red.dg, $"хвост на красном: dR {red.dr:F4} dG {red.dg:F4} dB {red.db:F4} (нужно B > G)");
        var green = Tail(0.1f, 0.6f, 0.1f);
        Assert.True(green.dr > green.db, $"хвост на зелёном: dR {green.dr:F4} dG {green.dg:F4} dB {green.db:F4} (нужно R > B)");
    }

    // ---------- T5: двоение эфира (ghost) ----------

    [Fact]
    public void Ghost_EchoRightOfStripe_DarkSecondEcho()
    {
        // Белая полоса 8 px на чёрном, PAL, ghost=1, ghostDelay=0.02: эхо справа на d, слева пусто, второе эхо на 2.3d тёмное.
        const int x0 = 960;
        int d = (int)Math.Round(0.02 * W);
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (x >= x0 && x < x0 + 8) ? (1f, 1f, 1f) : (0f, 0f, 0f));
        using var f = gpu.Create<TvFilter>();
        var img = gpu.Run(f, 0, new Dictionary<string, float>
        {
            ["mode"] = ModePal, ["amount"] = 1f, ["ghost"] = 1f, ["ghostDelay"] = 0.02f,
        });
        int row = H / 2;
        float MaxLuma(int from, int to)
        {
            float m = 0;
            for (int x = from; x <= to; x++) m = Math.Max(m, GpuHarness.Luma(img, x, row));
            return m;
        }
        float right = MaxLuma(x0 + d - 3, x0 + d + 3);
        float left = MaxLuma(x0 - d - 3, x0 - d + 3);
        float second = MaxLuma((int)(x0 + 2.3 * d) - 3, (int)(x0 + 2.3 * d) + 3);
        Assert.True(right >= 0.2f, $"эхо справа {right:F3} (нужно ≥ 0.2)");
        Assert.True(left < 0.03f, $"слева от полосы {left:F3} (нужно < 0.03)");
        Assert.True(second <= 0.01f, $"второе эхо {second:F3} (нужно ≤ фона + 0.01)");
    }

    [Fact]
    public void Ghost_Mean_StaysWithin004()
    {
        // Средняя яркость кадра при ghost=1 мало меняется (±0.04 от ghost=0).
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => { float v = ((x * 7 + y * 13) % 64) / 64f; return (v, v, v); });
        using var f = gpu.Create<TvFilter>();
        double Mean(float ghost)
        {
            var img = gpu.Run(f, 0, new Dictionary<string, float> { ["mode"] = ModePal, ["amount"] = 1f, ["ghost"] = ghost });
            double m = 0;
            int n = 0;
            for (int y = 8; y < H - 8; y++)
            for (int x = 64; x < W - 64; x++) { m += GpuHarness.Luma(img, x, y); n++; }
            return m / n;
        }
        double m0 = Mean(0f), m1 = Mean(1f);
        Assert.True(Math.Abs(m1 - m0) <= 0.04, $"средняя яркость ghost=0 {m0:F3}, ghost=1 {m1:F3}");
    }

    [Fact]
    public void Secam_FiberNoise_GreenStrongerThanBlue()
    {
        // Одинаковый строчный шум на ровных полях: на зелёном (×1.6) разброс яркости по строкам больше, чем на синем (×0.6).
        double Spread(float r, float g, float b)
        {
            using var gpu = new GpuHarness(W, H);
            gpu.SetInput((x, y) => (r, g, b));
            using var f = gpu.Create<TvFilter>();
            var img = gpu.Run(f, 0, Mode(ModeSecam));
            var sds = new List<double>();
            for (int y = 8; y < H - 8; y++)
            {
                var row = new List<double>();
                for (int x = 200; x < 1700; x++) row.Add(GpuHarness.Luma(img, x, y));
                sds.Add(StdDev(row));
            }
            return sds.Average();
        }
        double green = Spread(0.2f, 0.6f, 0.2f), blue = Spread(0.2f, 0.2f, 0.6f);
        Assert.True(green > blue, $"разброс по строкам: зелёный {green:F5}, синий {blue:F5}");
    }

    [Fact]
    public void Secam_FlatCyan_NotSmoothAndColorKept()
    {
        // Ровное поле (0.45, 0.6, 0.6): яркость по строкам не гладкая, среднее цвета в пределах ±0.03 от входа.
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (0.45f, 0.6f, 0.6f));
        using var f = gpu.Create<TvFilter>();
        var img = gpu.Run(f, 0, Mode(ModeSecam));
        var rowStd = new List<double>();
        double r = 0, g = 0, b = 0;
        int n = 0;
        for (int y = 8; y < H - 8; y++)
        {
            var row = new List<double>();
            for (int x = 200; x < 1700; x++)
            {
                row.Add(GpuHarness.Luma(img, x, y));
                r += img[y, x, 0]; g += img[y, x, 1]; b += img[y, x, 2]; n++;
            }
            rowStd.Add(StdDev(row));
        }
        double sd = rowStd.Average();
        Assert.True(sd > 0.01, $"отклонение яркости по строкам {sd:F4}");
        r /= n; g /= n; b /= n;
        // T4 (решение пользователя): насыщенность по умолчанию не трогаем (0.6 даёт пастель), поэтому цвет проверяем
        // как сохранение оттенка: порядок каналов G≈B > R, оттенок в пределах ±10° от входа, яркость ±0.05.
        Assert.True(g > r && b > r && Math.Abs(g - b) < 0.05, $"порядок каналов R {r:F3} G {g:F3} B {b:F3}");
        double Hue(double rr, double gg, double bb) => Math.Atan2(0.713 * (rr - (0.299 * rr + 0.587 * gg + 0.114 * bb)),
                                                                  0.564 * (bb - (0.299 * rr + 0.587 * gg + 0.114 * bb))) * 180 / Math.PI;
        double hIn = Hue(0.45, 0.6, 0.6), hOut = Hue(r, g, b);
        double dh = Math.Abs(hIn - hOut);
        if (dh > 180) dh = 360 - dh;
        Assert.True(dh <= 10, $"оттенок {hOut:F1}° при входе {hIn:F1}° (допуск ±10°)");
        double lumIn = 0.299 * 0.45 + 0.587 * 0.6 + 0.114 * 0.6;
        double lumOut = 0.299 * r + 0.587 * g + 0.114 * b;
        Assert.True(Math.Abs(lumOut - lumIn) <= 0.05, $"яркость {lumOut:F3} при входе {lumIn:F3}");
    }

    [Fact]
    public void Secam_MatrixLikeInput_DefaultFiresAreSparse()
    {
        // Дозировка факелов на сцене с большим числом краёв (matrix/ASCII): ахроматический вход, дефолтные параметры
        // (signal=1, fires=0.5). Цветность здесь только от факелов: доля пикселей с цветностью > 0.08 должна быть мала,
        // как в secam-fires-film.jpg (редкие мазки), а не сетка по всему кадру. Край x < 64 не считаем: там кромка.
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (x % 16 < 6 && y % 16 < 12) ? (1f, 1f, 1f) : (0f, 0f, 0f));
        using var f = gpu.Create<TvFilter>();
        var img = gpu.Run(f, 0, Mode(ModeSecam));
        int colored = 0, n = 0;
        for (int y = 8; y < H - 8; y++)
        for (int x = 64; x < W - 64; x++)
        {
            if (Chroma(img, x, y) > 0.08f) colored++;
            n++;
        }
        double frac = (double)colored / n;
        Assert.True(frac <= 0.015, $"доля пикселей с факелом {frac:P2} (нужно ≤ 1.5%)");
    }

    [Fact]
    public void Secam_ChromaFlowsRightOfLumaEdge()
    {
        // Граница красный|синий: цвет растекается ВПРАВО (хвост ФНЧ цвета слева от пикселя), поэтому 50%-точка
        // перехода по Cr должна лежать правее 50%-точки перехода по Y. Направление, не только ширина.
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => x < W / 2 ? (1f, 0f, 0f) : (0f, 0f, 1f));
        using var f = gpu.Create<TvFilter>();
        var img = gpu.Run(f, 0, Secam());
        int row = H / 2;
        var yv = new float[W];
        var crv = new float[W];
        for (int x = 0; x < W; x++)
        {
            yv[x] = GpuHarness.Luma(img, x, row);
            crv[x] = Ycc(img[row, x, 0], img[row, x, 1], img[row, x, 2]).cr;
        }
        float cy = HalfCrossing(yv, W / 2);
        float cc = HalfCrossing(crv, W / 2);
        Assert.True(cc > cy, $"центр перехода Cr {cc:F1} не правее центра Y {cy:F1}");
    }

    // Средняя длина подряд идущих пикселей с цветностью выше порога по всем строкам кадра.
    private static double MeanChromaRun(float[,,] img, int h, int w, float thr)
    {
        double total = 0;
        int runs = 0;
        for (int y = 8; y < h - 8; y++)
        {
            int run = 0;
            for (int x = 64; x < w - 64; x++)
            {
                if (Chroma(img, x, y) > thr) run++;
                else if (run > 0) { total += run; runs++; run = 0; }
            }
            if (run > 0) { total += run; runs++; }
        }
        return runs == 0 ? 0 : total / runs;
    }

    [Fact]
    public void Secam_FireSize25_FlaresAreLongerThanAt1()
    {
        // Сетка глифов (много краёв), signal=1, fires=1: факелы от каждого сегмента с краем. Длина факела ∝ fireSize.
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (x % 16 < 6 && y % 16 < 12) ? (1f, 1f, 1f) : (0f, 0f, 0f));
        using var f = gpu.Create<TvFilter>();
        var base1 = new Dictionary<string, float>(Secam(fires: 1f)) { ["fireSize"] = 1f };
        var big = new Dictionary<string, float>(Secam(fires: 1f)) { ["fireSize"] = 2.5f };

        double len1 = MeanChromaRun(gpu.Run(f, 0, base1), H, W, 0.05f);
        double len25 = MeanChromaRun(gpu.Run(f, 0, big), H, W, 0.05f);
        Assert.True(len1 > 0, "при fireSize=1 нет факелов");
        Assert.True(len25 >= 1.5 * len1, $"средняя длина факела: при 2.5 {len25:F1} px, при 1 {len1:F1} px");
    }

    [Fact]
    public void Secam_EdgeZero_NoGreenInTop3Percent()
    {
        // Серый вход, edge=0: верхние 3% кадра без зелёного оттенка (обратная проверка к Secam_EdgeBand_GreenStripeAtTopEdge).
        const int HB = 1080;
        using var gpu = new GpuHarness(W, HB);
        gpu.SetInput((x, y) => (0.5f, 0.5f, 0.5f));
        using var f = gpu.Create<TvFilter>();
        var img = gpu.Run(f, 0, Secam(edge: 0f));
        int rows = (int)(0.03 * HB);
        double g = 0, rb = 0;
        int n = 0;
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < W; x++)
        {
            g += img[y, x, 1];
            rb += Math.Max(img[y, x, 0], img[y, x, 2]);
            n++;
        }
        double excess = g / n - rb / n;
        Assert.True(excess < 0.02, $"верхние 3% кадра: G − max(R,B) = {excess:F4} при edge=0");
    }

    // Положение 50%-точки перехода между средними уровнями слева и справа от центра (дробный пиксель).
    private static float HalfCrossing(float[] v, int center)
    {
        float lo = 0, hi = 0;
        for (int i = center - 200; i < center - 150; i++) lo += v[i];
        for (int i = center + 150; i < center + 200; i++) hi += v[i];
        lo /= 50; hi /= 50;
        for (int x = center - 150; x < center + 150; x++)
        {
            float t0 = (v[x] - lo) / (hi - lo), t1 = (v[x + 1] - lo) / (hi - lo);
            if (t0 < 0.5f && t1 >= 0.5f) return x + (0.5f - t0) / (t1 - t0);
        }
        return float.NaN;
    }
}
