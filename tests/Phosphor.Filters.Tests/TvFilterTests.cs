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
        Assert.Equal(new[] { "mode", "amount", "hue", "sharpness", "saturation", "fires", "signal", "edge" },
            ps.Select(p => p.Name).ToArray());
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
        using var gpu = new GpuHarness(W, H);
        int edge = W / 2;
        gpu.SetInput((x, y) => x < edge ? (1f, 0f, 0f) : (0.5f, 0.5f, 0.5f));
        using var f = gpu.Create<TvFilter>();
        var img = gpu.Run(f, 0, Mode(ModeSecam));
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
        var g = gpu.Run(f, 0, Mode(ModeSecam));
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

    private static Dictionary<string, float> Secam(float signal = 1f, float fires = 0f, float edge = 0f) =>
        new() { ["mode"] = ModeSecam, ["amount"] = 1f, ["signal"] = signal, ["fires"] = fires, ["edge"] = edge };

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

        var weak = gpu.Run(f, 0, Secam(signal: 0.3f));
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
    public void Secam_EdgeBand_GreenAtLeftEdge()
    {
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => (0f, 0f, 0f));
        using var f = gpu.Create<TvFilter>();
        int band = (int)(0.008 * W);

        float[] MeanRgb(float[,,] img)
        {
            var m = new float[3];
            int n = 0;
            for (int y = 8; y < H - 8; y++)
            for (int x = 0; x < band; x++)
            {
                m[0] += img[y, x, 0]; m[1] += img[y, x, 1]; m[2] += img[y, x, 2]; n++;
            }
            return new[] { m[0] / n, m[1] / n, m[2] / n };
        }

        var on = MeanRgb(gpu.Run(f, 0, Secam(edge: 1f)));
        Assert.True(on[1] - MathF.Max(on[0], on[2]) >= 0.05f, $"G {on[1]:F3}, R {on[0]:F3}, B {on[2]:F3}");

        var off = MeanRgb(gpu.Run(f, 0, Secam(edge: 0f)));
        Assert.True(off[1] - MathF.Max(off[0], off[2]) < 0.02f, $"без кромки G {off[1]:F3}");
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
