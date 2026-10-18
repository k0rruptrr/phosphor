using System.Diagnostics;
using Phosphor.Core;
using Phosphor.Filters;
using Phosphor.Gfx;

namespace Phosphor.Filters.Tests;

public sealed class DvbFilterTests
{
    private const int W = 320, H = 384;          // кратно 16 и 8: сетка блоков без обрезки; 24 строки блоков
    // Срезы случайны по времени: тесты с ними смотрят несколько моментов (эпохи срезов 0.3–1.5 с)
    private static readonly long[] Moments = { 37, 97, 157, 217, 277 };
    private const double ZeroTol = 2.0 / 255.0;
    private const float Frame = 1f / 60f;

    private const string ShaderMain = "dvb.frag";
    private const string ShaderSlice = "dvb_slice.frag";

    private static readonly Dictionary<string, float> Defaults = new()
    {
        ["severity"] = 0.6f,
        ["rate"] = 0.8f,
        ["block"] = 16f,
        ["freeze"] = 1f,
    };

    private static Dictionary<string, float> D(params (string K, float V)[] kv)
    {
        var d = new Dictionary<string, float>();
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    /// <summary>Плавный серый вход: синусоида по x и y, без цветовой доминанты.</summary>
    private static float SmoothV(int x, int y) => 0.5f + 0.3f * MathF.Sin(x / 37f) * MathF.Cos(y / 29f);

    /// <summary>Другой плавный вход (для заморозки): фаза и частоты иные, почти все блоки меняются.</summary>
    private static float OtherV(int x, int y) => 0.5f + 0.3f * MathF.Sin((x + 100) / 23f) * MathF.Cos(y / 17f);

    private static void SetGray(GpuHarness gpu, Func<int, int, float> v) =>
        gpu.SetInput((x, y) => (v(x, y), v(x, y), v(x, y)));

    private static float[] Flatten(float[,,] img)
    {
        int h = img.GetLength(0), w = img.GetLength(1);
        var r = new float[h * w * 3];
        int i = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            r[i++] = img[y, x, 0]; r[i++] = img[y, x, 1]; r[i++] = img[y, x, 2];
        }
        return r;
    }

    private static float[] GrayInput(Func<int, int, float> v)
    {
        var r = new float[H * W * 3];
        int i = 0;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            float g = v(x, y);
            r[i++] = g; r[i++] = g; r[i++] = g;
        }
        return r;
    }

    private static double MaxAbs(float[] a, float[] b)
    {
        double mx = 0;
        for (int i = 0; i < a.Length; i++) mx = Math.Max(mx, Math.Abs(a[i] - b[i]));
        return mx;
    }

    /// <summary>Максимум по плиткам 64×16 средней |Δ| по каналам (шкала 0..1).</summary>
    private static double TileMax(float[] a, float[] b)
    {
        double best = 0;
        for (int ty = 0; ty < H; ty += 16)
        for (int tx = 0; tx < W; tx += 64)
        {
            double sum = 0;
            int n = 0;
            for (int y = ty; y < Math.Min(ty + 16, H); y++)
            for (int x = tx; x < Math.Min(tx + 64, W); x++)
            {
                int i = (y * W + x) * 3;
                for (int c = 0; c < 3; c++) { sum += Math.Abs(a[i + c] - b[i + c]); n++; }
            }
            best = Math.Max(best, sum / n);
        }
        return best;
    }

    /// <summary>Блок 16×16 «испорчен», если средняя |Δ| по всем каналам выше порога.</summary>
    private static bool[,] ChangedBlocks(float[] a, float[] b, double thr)
    {
        var res = new bool[H / 16, W / 16];
        for (int by = 0; by < H / 16; by++)
        for (int bx = 0; bx < W / 16; bx++)
        {
            double sum = 0;
            for (int y = by * 16; y < by * 16 + 16; y++)
            for (int x = bx * 16; x < bx * 16 + 16; x++)
            {
                int i = (y * W + x) * 3;
                for (int c = 0; c < 3; c++) sum += Math.Abs(a[i + c] - b[i + c]);
            }
            res[by, bx] = sum / (256 * 3) > thr;
        }
        return res;
    }

    /// <summary>Ровный горизонтальный градиент: сдвиг выборки виден в каждом блоке, без периодичности.</summary>
    private static float RampV(int x, int y) => 0.15f + 0.7f * x / W;

    // ---- Контракт и статика ----

    [Fact]
    public void Id_IsDvb()
    {
        Assert.Equal("dvb", new DvbFilter().Id);
    }

    [Fact]
    public void Params_HasExactlyTheExpectedNames()
    {
        var names = new DvbFilter().Params.Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "severity", "rate", "block", "freeze" }, names);
    }

    [Fact]
    public void Params_MinLessOrEqualDefaultLessOrEqualMax()
    {
        foreach (var p in new DvbFilter().Params)
        {
            Assert.True(p.Min <= p.Default, $"{p.Name}: Min {p.Min} > Default {p.Default}");
            Assert.True(p.Default <= p.Max, $"{p.Name}: Default {p.Default} > Max {p.Max}");
        }
    }

    [Fact]
    public void Params_DefaultsMatchSpec()
    {
        foreach (var p in new DvbFilter().Params)
            Assert.Equal(Defaults[p.Name], p.Default);
    }

    [Fact]
    public void Params_SeverityIsArtifactAndTextOff()
    {
        var sev = new DvbFilter().Params.Single(p => p.Name == "severity");
        Assert.Equal(0f, sev.Clean);
        Assert.True(sev.TextOff);
    }

    [Fact]
    public void ShaderResources_Exist()
    {
        var asm = typeof(DvbFilter).Assembly;
        Assert.NotNull(asm.GetManifestResourceStream("Phosphor.Filters.Shaders." + ShaderMain));
        Assert.NotNull(asm.GetManifestResourceStream("Phosphor.Filters.Shaders." + ShaderSlice));
    }

    [Fact]
    public void Shaders_PassStaticSyntaxCheck()
    {
        ShaderStatic.CheckSyntax(ShaderStatic.Load("Phosphor.Filters.Shaders." + ShaderMain), ShaderMain);
        ShaderStatic.CheckSyntax(ShaderStatic.Load("Phosphor.Filters.Shaders." + ShaderSlice), ShaderSlice);
    }

    [Fact]
    public void Shader_DeclaresExactlyTheFilterParams()
    {
        var src = ShaderStatic.Load("Phosphor.Filters.Shaders." + ShaderMain);
        ShaderStatic.CheckParams(new DvbFilter(), src);
    }

    // ---- Расписание ----

    [Fact]
    public void Schedule_RateZero_HasNoEpisodes()
    {
        for (int f = 0; f < 36000; f++)
            Assert.False(DvbFilter.Find(f / 60f, 0f, out _), $"эпизод на кадре {f}");
    }

    [Fact]
    public void Schedule_DefaultRate_HasEpisodesShorterAndRarerThanAll()
    {
        int active = 0, starts = 0;
        float maxDur = 0, minDur = float.MaxValue;
        bool prev = false;
        const int total = 36000;   // 10 минут при 60 кадрах/с
        for (int f = 0; f < total; f++)
        {
            bool on = DvbFilter.Find(f / 60f, 0.8f, out var ep);
            if (on)
            {
                active++;
                maxDur = Math.Max(maxDur, ep.Dur);
                minDur = Math.Min(minDur, ep.Dur);
                if (!prev) starts++;
            }
            prev = on;
        }
        Assert.True(starts > 0, "за 10 минут нет ни одного эпизода");
        Assert.True(active < total / 2, $"эпизоды занимают {active}/{total} кадров — должны быть реже");
        Assert.True(maxDur <= 6f && minDur >= 1f, $"длительность {minDur}..{maxDur} с вне 1..6");
        Assert.True(active < total * 0.9f && starts < total / 60, "эпизоды не короче «всё время»");
    }

    // ---- Тесты контракта: тождество и отклик ----

    [Fact]
    public void ZeroSeverity_IsIdentity()
    {
        using var gpu = new GpuHarness(W, H);
        SetGray(gpu, SmoothV);
        var input = GrayInput(SmoothV);
        using var f = gpu.Create<DvbFilter>();
        f.ForceEpisode = 0.5f;
        var outp = Flatten(gpu.Run(f, 37, D(("severity", 0f))));
        Assert.True(MaxAbs(outp, input) <= ZeroTol, $"при severity 0 выход отличается на {MaxAbs(outp, input) * 255:F2}/255");
    }

    [Fact]
    public void OutsideEpisode_IsIdentity()
    {
        using var gpu = new GpuHarness(W, H);
        SetGray(gpu, SmoothV);
        var input = GrayInput(SmoothV);
        using var f = gpu.Create<DvbFilter>();
        f.ForceEpisode = -1f;
        var outp = Flatten(gpu.Run(f, 37, D(("rate", 0f))));
        Assert.True(MaxAbs(outp, input) <= ZeroTol, $"вне эпизода выход отличается на {MaxAbs(outp, input) * 255:F2}/255");
    }

    [Fact]
    public void ForcedEpisode_InMiddle_ChangesPicture()
    {
        using var gpu = new GpuHarness(W, H);
        SetGray(gpu, SmoothV);
        var input = GrayInput(SmoothV);
        using var f = gpu.Create<DvbFilter>();
        f.ForceEpisode = 0.5f;
        double best = 0;
        foreach (var fr in Moments) best = Math.Max(best, TileMax(Flatten(gpu.Run(f, fr)), input));
        Assert.True(best >= 0.03, $"максимум по плиткам {best:F3} < 0.03");
    }

    [Fact]
    public void SameInputs_GiveSamePixels()
    {
        using var gpu = new GpuHarness(W, H);
        using var a = gpu.Create<DvbFilter>();
        using var b = gpu.Create<DvbFilter>();
        float[]? la = null, lb = null;
        for (int f = 0; f < 12; f++)
        {
            SetGray(gpu, (x, y) => SmoothV(x + f, y));
            a.ForceEpisode = 0.3f;
            la = Flatten(gpu.Run(a, f * 3));
        }
        for (int f = 0; f < 12; f++)
        {
            SetGray(gpu, (x, y) => SmoothV(x + f, y));
            b.ForceEpisode = 0.3f;
            lb = Flatten(gpu.Run(b, f * 3));
        }
        Assert.Equal(0.0, MaxAbs(la!, lb!));
    }

    // ---- Заморозка ----

    [Fact]
    public void Freeze_HoldsPicture_ThenRefreshesPartially()
    {
        using var gpu = new GpuHarness(W, H);
        using var f = gpu.Create<DvbFilter>();
        var outs = new List<float[]>();
        for (int fr = 0; fr <= 150; fr++)
        {
            float lt = fr / 60f;                                  // локальное время эпизода, с
            SetGray(gpu, (x, y) => lt < 1.0f ? SmoothV(x, y) : OtherV(x, y));
            f.ForceEpisode = lt / 4f;                             // ForcedLen = 4 с
            outs.Add(Flatten(gpu.Run(f, fr)));
        }
        // Заморозка 0.5–1.5 с: кадры неподвижны, хотя вход меняется в 1.0 с.
        for (int fr = 31; fr <= 89; fr++)
            Assert.True(MaxAbs(outs[fr], outs[31]) <= 1e-5, $"кадр {fr} в заморозке изменился");

        // Догрузка 1.5–2.0 с: часть блоков уже новые, часть старые.
        var frozen = outs[89];
        var mid = ChangedBlocks(outs[105], frozen, 0.01);   // lt = 1.75 с, доля обновлённых ~ 0.5
        int changed = 0, total = 0;
        foreach (var c in mid) { total++; if (c) changed++; }
        double share = (double)changed / total;
        Assert.True(share > 0.15 && share < 0.85, $"при частичном обновлении изменилось {share:P0} блоков");

        // После догрузки почти все блоки новые.
        var done = ChangedBlocks(outs[149], frozen, 0.01);
        int doneCount = 0;
        foreach (var c in done) if (c) doneCount++;
        Assert.True((double)doneCount / total > 0.85, $"после догрузки изменилось только {(double)doneCount / total:P0}");
    }

    // ---- Срезы, сетка и зелёные блоки ----

    [Fact]
    public void SliceTail_IsSolid_FromFirstBadBlockToRowEnd()
    {
        using var gpu = new GpuHarness(W, H);
        SetGray(gpu, RampV);
        var input = GrayInput(RampV);
        using var f = gpu.Create<DvbFilter>();
        f.ForceEpisode = 0.5f;
        int cols = W / 16, tailRows = 0;
        foreach (var fr in Moments)
        {
            var outp = Flatten(gpu.Run(f, fr, D(("severity", 1f))));
            var bad = ChangedBlocks(outp, input, 0.06);
            for (int by = 0; by < H / 16; by++)
            {
                int first = -1;
                for (int bx = 0; bx < cols; bx++) if (bad[by, bx]) { first = bx; break; }
                if (first < 0) continue;
                tailRows++;
                int n = 0;
                for (int bx = first; bx < cols; bx++) if (bad[by, bx]) n++;
                double share = (double)n / (cols - first);
                Assert.True(share >= 0.9, $"кадр {fr}, строка блоков {by}: от первого испорченного блока {first} до конца испорчено {share:P0}");
            }
        }
        Assert.True(tailRows >= 1, "при severity 1 ни в одном моменте нет среза");
    }

    [Fact]
    public void GreenBlocks_AppearAtHighSeverity_AndStartOnBlockGrid()
    {
        using var gpu = new GpuHarness(W, H);
        SetGray(gpu, SmoothV);
        using var f = gpu.Create<DvbFilter>();
        f.ForceEpisode = 0.5f;

        int greenHi = 0;
        foreach (var fr in Moments)
        {
            var hi = gpu.Run(f, fr, D(("severity", 1f)));
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (IsGreen(hi, x, y)) greenHi++;
            }
            for (int y = 0; y < H; y++)
            for (int x = 1; x < W; x++)
            {
                if (IsGreen(hi, x, y) && !IsGreen(hi, x - 1, y))
                    Assert.True(x % 16 == 0, $"кадр {fr}: зелёный участок начинается не на сетке блока: x={x}, y={y}");
            }
        }
        Assert.True(greenHi > 0, "при высокой силе зелёных блоков нет");

        int greenLo = 0;
        foreach (var fr in Moments)
        {
            var lo = gpu.Run(f, fr, D(("severity", 0.2f)));
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (IsGreen(lo, x, y)) greenLo++;
        }
        Assert.Equal(0, greenLo);
    }

    private static bool IsGreen(float[,,] img, int x, int y) =>
        img[y, x, 1] - img[y, x, 0] > 0.3f && img[y, x, 1] - img[y, x, 2] > 0.3f;

    [Fact]
    public void Episode_IsBlocky_BoundariesOnQuantGridAreJumpier()
    {
        using var gpu = new GpuHarness(W, H);
        SetGray(gpu, SmoothV);
        using var f = gpu.Create<DvbFilter>();
        f.ForceEpisode = 0.5f;
        var img = gpu.Run(f, 37, D(("severity", 1f)));

        double boundary = 0, interior = 0;
        int nb = 0, ni = 0;
        for (int y = 0; y < H; y++)
        for (int x = 1; x < W; x++)
        {
            double d = Math.Abs(img[y, x, 0] - img[y, x - 1, 0]);
            int m = x % 8;
            if (m == 0) { boundary += d; nb++; }
            else if (m == 3 || m == 4) { interior += d; ni++; }
        }
        double b = boundary / nb, i = interior / ni;
        Assert.True(b > 2.0 * i, $"границы блоков {b:F4}, внутри {i:F4}: блочности мало");
    }

    [Fact]
    public void Episode_LowQuality_IsBlocky_EvenWithoutTails()
    {
        using var gpu = new GpuHarness(W, H);
        SetGray(gpu, SmoothV);
        using var f = gpu.Create<DvbFilter>();
        f.ForceEpisode = 0.5f;
        var img = gpu.Run(f, 37, D(("severity", 0.4f)));   // качество 0.4 < 0.6: блочность 8×8 на всём кадре

        double boundary = 0, interior = 0;
        int nb = 0, ni = 0;
        for (int y = 0; y < H; y++)
        for (int x = 1; x < W; x++)
        {
            double d = Math.Abs(img[y, x, 0] - img[y, x - 1, 0]);
            int m = x % 8;
            if (m == 0) { boundary += d; nb++; }
            else if (m == 3 || m == 4) { interior += d; ni++; }
        }
        double b = boundary / nb, i = interior / ni;
        Assert.True(b > 1.5 * i, $"при качестве 0.4 границы блоков {b:F4}, внутри {i:F4}: блочность не включилась");
    }

    [Fact]
    public void TailBlocks_HaveSteps_AtBlockBoundaries()
    {
        using var gpu = new GpuHarness(W, H);
        SetGray(gpu, RampV);
        var input = GrayInput(RampV);
        using var f = gpu.Create<DvbFilter>();
        f.ForceEpisode = 0.5f;
        double bSum = 0, iSum = 0;
        int nb = 0, ni = 0;
        foreach (var fr in Moments)
        {
            var outp = Flatten(gpu.Run(f, fr, D(("severity", 1f))));
            var bad = ChangedBlocks(outp, input, 0.06);
            for (int by = 0; by < H / 16; by++)
            {
                int first = -1;
                for (int bx = 0; bx < W / 16; bx++) if (bad[by, bx]) { first = bx; break; }
                if (first < 0) continue;
                // внутри хвоста: пары соседних пикселей, оба в испорченных блоках
                for (int y = by * 16; y < by * 16 + 16; y++)
                for (int x = first * 16 + 1; x < W; x++)
                {
                    if (!bad[by, x / 16] || !bad[by, (x - 1) / 16]) continue;
                    double d = 0;
                    for (int c = 0; c < 3; c++)
                    {
                        int i1 = (y * W + x) * 3 + c, i0 = (y * W + x - 1) * 3 + c;
                        d += Math.Abs(outp[i1] - outp[i0]);
                    }
                    d /= 3;
                    int m = x % 16;
                    if (m == 0) { bSum += d; nb++; }
                    else if (m >= 4 && m <= 11) { iSum += d; ni++; }
                }
            }
        }
        Assert.True(nb > 0 && ni > 0, "в хвостах не набралось пар пикселей для сравнения");
        double bm = bSum / nb, im = iSum / ni;
        Assert.True(bm > 1.5 * im, $"в хвосте границы блоков {bm:F4}, внутри {im:F4}: ступеней мало");
    }

    // ---- Производительность и ресайз ----

    [Fact]
    public void Performance_120Frames_1080p()
    {
        const int pw = 1920, ph = 1080;
        using var gpu = new GpuHarness(pw, ph);
        gpu.SetInput((x, y) => (SmoothV(x, y), SmoothV(x, y), SmoothV(x, y)));
        using var f = gpu.Create<DvbFilter>();
        using var output = new RenderTarget(gpu.Gl, pw, ph);
        var p = new FilterParams();
        foreach (var s in f.Params) p[s.Name] = s.Default;
        f.ForceEpisode = 0.5f;
        var gl = gpu.Gl;

        for (int i = 0; i < 10; i++)
        {
            f.Apply(gpu.InputTexture, output, new FrameInfo(i * Frame, Frame, i, pw, ph, 0, AudioFrame.Silent), p);
            gl.Finish();
        }
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 120; i++)
        {
            f.Apply(gpu.InputTexture, output, new FrameInfo(i * Frame, Frame, i, pw, ph, 0, AudioFrame.Silent), p);
            gl.Finish();
        }
        sw.Stop();
        double msPerFrame = sw.Elapsed.TotalMilliseconds / 120;
        Console.WriteLine($"dvb: {msPerFrame:F3} мс/кадр при 1920×1080 (бюджет 1.5, порог 4)");
        Assert.True(msPerFrame < 4.0, $"{msPerFrame:F3} мс/кадр — выше порога 4 мс");
    }

    [Fact]
    public void Resize_DoesNotThrow_AndMatchesFreshFilter()
    {
        using var gpu = new GpuHarness(W, H);
        SetGray(gpu, SmoothV);
        using var f = gpu.Create<DvbFilter>();
        f.Resize(W * 2, H * 2);
        f.Resize(W, H);
        f.ForceEpisode = 0.5f;
        var resized = Flatten(gpu.Run(f, 20));

        using var fresh = gpu.Create<DvbFilter>();
        fresh.ForceEpisode = 0.5f;
        var expected = Flatten(gpu.Run(fresh, 20));
        Assert.Equal(0.0, MaxAbs(resized, expected));
    }
}
