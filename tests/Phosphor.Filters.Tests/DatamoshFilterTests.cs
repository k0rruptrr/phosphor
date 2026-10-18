using System.Diagnostics;
using Phosphor.Core;
using Phosphor.Filters;
using Phosphor.Gfx;
using Silk.NET.OpenGL;

namespace Phosphor.Filters.Tests;

public class DatamoshFilterTests
{
    private const int W = 320, H = 192;
    private const double Tol = 2.0 / 255.0;

    private static readonly string[] ParamNames = { "intensity", "rate", "block", "onTransition" };

    private static readonly Dictionary<string, float> Defaults = new()
    {
        ["intensity"] = 0.75f,
        ["rate"] = 0.5f,
        ["block"] = 16f,
        ["onTransition"] = 1f,
    };

    private static readonly string[] ShaderFiles =
    {
        "datamosh_luma.frag", "datamosh_motion.frag", "datamosh_state.frag", "datamosh.frag", "datamosh_copy.frag",
    };

    private static Dictionary<string, float> Ov(params (string K, float V)[] kv)
    {
        var d = new Dictionary<string, float>();
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    // ---- Картинки: A — гладкая, B — шум по клеткам 8 px (не сдвиг A), шахматка 16 px сдвигается ----

    private static (float r, float g, float b) Checker(int x, int y, int shift)
    {
        int cx = (x - shift + 4096) / 16, cy = y / 16;
        float v = ((cx + cy) & 1) == 0 ? 0.9f : 0.1f;
        return (v, v, v);
    }

    private static (float r, float g, float b) Smooth(int x, int y)
    {
        float v = 0.5f + 0.4f * MathF.Sin(x * 0.04f) * MathF.Cos(y * 0.05f);
        return (v, v * 0.8f + 0.1f, 1f - v);
    }

    private static (float r, float g, float b) Noise(int x, int y)
    {
        unchecked
        {
            uint h = (uint)(x / 8) * 73856093u ^ (uint)(y / 8) * 19349663u;
            h ^= h >> 13;
            h *= 0x5BD1E995u;
            h ^= h >> 15;
            float v = (h & 0xFFFF) / 65535f;
            return (v, 1f - v, v * 0.5f + 0.25f);
        }
    }

    private static float[,,] Img(Func<int, int, (float r, float g, float b)> f)
    {
        var img = new float[H, W, 3];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            var (r, g, b) = f(x, y);
            img[y, x, 0] = r; img[y, x, 1] = g; img[y, x, 2] = b;
        }
        return img;
    }

    private static float[,,] Run(GpuHarness gpu, DatamoshFilter f, Func<int, int, (float r, float g, float b)> input,
        long frame, IReadOnlyDictionary<string, float>? ov = null)
    {
        gpu.SetInput(input);
        return gpu.Run(f, frame, ov);
    }

    private static double MaxAbsDiff(float[,,] a, float[,,] b)
    {
        double m = 0;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        for (int c = 0; c < 3; c++)
            m = Math.Max(m, Math.Abs(a[y, x, c] - b[y, x, c]));
        return m;
    }

    /// <summary>Максимум по плиткам 64×16 средней |Δ| яркости.</summary>
    private static double MaxTileDiff(float[,,] a, float[,,] b)
    {
        double best = 0;
        for (int ty = 0; ty < H; ty += 16)
        for (int tx = 0; tx < W; tx += 64)
        {
            double s = 0;
            for (int y = ty; y < ty + 16; y++)
            for (int x = tx; x < tx + 64; x++)
                s += Math.Abs(GpuHarness.Luma(a, x, y) - GpuHarness.Luma(b, x, y));
            best = Math.Max(best, s / (16 * 64));
        }
        return best;
    }

    /// <summary>Корреляция Пирсона яркости двух картинок.</summary>
    private static double Corr(float[,,] a, float[,,] b)
    {
        int n = W * H;
        double ma = 0, mb = 0;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++) { ma += GpuHarness.Luma(a, x, y); mb += GpuHarness.Luma(b, x, y); }
        ma /= n; mb /= n;
        double cov = 0, va = 0, vb = 0;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double da = GpuHarness.Luma(a, x, y) - ma, db = GpuHarness.Luma(b, x, y) - mb;
            cov += da * db; va += da * da; vb += db * db;
        }
        return cov / Math.Sqrt(va * vb);
    }

    /// <summary>Средняя |cur(x,y) − prev(x−dx, y)| по внутренней области (без краёв).</summary>
    private static double MeanShiftDiff(float[,,] cur, float[,,] prev, int dx)
    {
        double s = 0;
        int n = 0;
        for (int y = 0; y < H; y++)
        for (int x = 16; x < W - 16; x++)
        {
            for (int c = 0; c < 3; c++) s += Math.Abs(cur[y, x, c] - prev[y, x - dx, c]);
            n += 3;
        }
        return s / n;
    }

    /// <summary>Хеш всех пикселей кадра для проверки детерминизма.</summary>
    private static long Checksum(float[,,] img)
    {
        long h = 17;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        for (int c = 0; c < 3; c++)
            h = unchecked(h * 31 + BitConverter.SingleToInt32Bits(img[y, x, c]));
        return h;
    }

    // ---- Контракт и шейдеры ----

    [Fact]
    public void Id_IsDatamosh()
    {
        Assert.Equal("datamosh", new DatamoshFilter().Id);
    }

    [Fact]
    public void Params_HasExactlyTheExpectedNames()
    {
        Assert.Equal(ParamNames, new DatamoshFilter().Params.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Params_DefaultsMatchSpec_AndInsideRange()
    {
        foreach (var p in new DatamoshFilter().Params)
        {
            Assert.Equal(Defaults[p.Name], p.Default);
            Assert.True(p.Min <= p.Default && p.Default <= p.Max, $"{p.Name}: Default вне [Min, Max]");
            Assert.False(string.IsNullOrWhiteSpace(p.Label));
            Assert.True(p.Label!.Length <= 22, $"{p.Name}: Label длиннее 22");
        }
    }

    [Fact]
    public void Intensity_IsArtifactAndTextOff()
    {
        var intensity = new DatamoshFilter().Params.Single(p => p.Name == "intensity");
        Assert.Equal(0f, intensity.Clean);
        Assert.True(intensity.TextOff);
    }

    [Fact]
    public void Shaders_ExistAndPassStaticSyntaxCheck()
    {
        foreach (var file in ShaderFiles)
        {
            var src = ShaderStatic.Load("Phosphor.Filters.Shaders." + file);
            ShaderStatic.CheckSyntax(src, file);
        }
    }

    // ---- Расписание (чистый C#, без GPU) ----

    [Fact]
    public void Schedule_RateZero_NoEpisodes()
    {
        int episodes = 0;
        for (double t = 0; t < 600; t += 1.0 / 60)
            if (DatamoshFilter.Schedule(t, 0f, out _)) episodes++;
        Assert.Equal(0, episodes);
    }

    [Fact]
    public void Schedule_DefaultRate_EpisodesExistAndAreShorterAndRarerThanAlways()
    {
        int episodes = 0, activeFrames = 0, total = 0;
        bool prev = false;
        for (double t = 0; t < 600; t += 1.0 / 60)
        {
            bool a = DatamoshFilter.Schedule(t, 0.5f, out _);
            if (a && !prev) episodes++;
            if (a) activeFrames++;
            prev = a;
            total++;
        }
        Assert.True(episodes >= 1, "за 10 минут при rate 0.5 нет ни одного эпизода");
        Assert.True(episodes < 30, $"эпизодов слишком много: {episodes}");
        Assert.True(activeFrames < total * 0.2, $"эпизоды идут слишком часто: {activeFrames}/{total}");
    }

    [Fact]
    public void Schedule_HigherRate_MoreEpisodes()
    {
        int Count(float rate)
        {
            int n = 0;
            bool prev = false;
            for (double t = 0; t < 600; t += 1.0 / 60)
            {
                bool a = DatamoshFilter.Schedule(t, rate, out _);
                if (a && !prev) n++;
                prev = a;
            }
            return n;
        }
        Assert.True(Count(6f) > Count(0.5f));
    }

    // ---- GPU: поведение ----

    [Fact]
    public void Strength0_IsIdentity_InsideEpisode()
    {
        using var gpu = new GpuHarness(W, H);
        using var f = gpu.Create<DatamoshFilter>();
        f.ForceEpisode = 0.5f;
        var ov = Ov(("intensity", 0f));
        for (long n = 0; n < 6; n++)
        {
            int shift = (int)(8 * n);
            var outImg = Run(gpu, f, (x, y) => Checker(x, y, shift), n, ov);
            Assert.True(MaxAbsDiff(outImg, Img((x, y) => Checker(x, y, shift))) <= Tol, $"кадр {n}");
        }
    }

    [Fact]
    public void OutsideEpisode_IsIdentity()
    {
        using var gpu = new GpuHarness(W, H);
        using var f = gpu.Create<DatamoshFilter>();
        f.ForceEpisode = -1f;
        var ov = Ov(("rate", 0f));
        for (long n = 0; n < 6; n++)
        {
            int shift = (int)(8 * n);
            var outImg = Run(gpu, f, (x, y) => Checker(x, y, shift), n, ov);
            Assert.True(MaxAbsDiff(outImg, Img((x, y) => Checker(x, y, shift))) <= Tol, $"кадр {n}");
        }
    }

    [Fact]
    public void ForceEpisode_ChangesPicture()
    {
        using var gpu = new GpuHarness(W, H);
        using var f = gpu.Create<DatamoshFilter>();
        var rateOff = Ov(("rate", 0f));
        // Старая картинка A идёт без эпизодов; затем вход становится шумом B, и эпизод включается.
        for (long n = 0; n < 4; n++) Run(gpu, f, Smooth, n, rateOff);
        f.ForceEpisode = 0.5f;
        var outImg = Run(gpu, f, Noise, 4, rateOff);
        var input = Img(Noise);
        Assert.True(MaxTileDiff(outImg, input) >= 0.03, "в эпизоде картинка не отличается от входа");
    }

    [Fact]
    public void StaticInput_InEpisode_Freezes()
    {
        using var gpu = new GpuHarness(W, H);
        using var f = gpu.Create<DatamoshFilter>();
        f.ForceEpisode = 0.5f;
        var first = Run(gpu, f, Smooth, 0);
        for (long n = 1; n < 8; n++)
        {
            var outImg = Run(gpu, f, Smooth, n);
            Assert.True(MaxAbsDiff(outImg, first) <= Tol, $"кадр {n} изменился при статичном входе");
        }
    }

    [Fact]
    public void MovingInput_InEpisode_KeepsFlowing()
    {
        // Шахматка сдвигается на 8 px вправо каждый кадр. Векторы должны найти сдвиг, и выход продолжает
        // сдвигаться: out_n(x) ≈ out_{n−1}(x−8). Без векторов кадр бы стоял (сдвиг 0 даёт большую разницу).
        using var gpu = new GpuHarness(W, H);
        using var f = gpu.Create<DatamoshFilter>();
        f.ForceEpisode = 0.5f;
        var prev = Run(gpu, f, (x, y) => Checker(x, y, 0), 0);
        for (long n = 1; n <= 5; n++)
        {
            int shift = (int)(8 * n);
            var cur = Run(gpu, f, (x, y) => Checker(x, y, shift), n);
            if (n >= 2)
            {
                double moving = MeanShiftDiff(cur, prev, 8);
                double frozen = MeanShiftDiff(cur, prev, 0);
                Assert.True(moving < 0.03, $"кадр {n}: сдвиг на 8 px не совпал ({moving:F4})");
                Assert.True(frozen > 0.2, $"кадр {n}: выход не сдвигается ({frozen:F4})");
            }
            prev = cur;
        }
    }

    [Fact]
    public void SceneChange_OldPictureStaysVisible_ThenKeyframeShowsNew()
    {
        using var gpu = new GpuHarness(W, H);
        using var f = gpu.Create<DatamoshFilter>();
        var rateOff = Ov(("rate", 0f));
        f.ForceEpisode = 0.5f;
        var a = Img(Smooth);
        var b = Img(Noise);
        for (long n = 0; n < 10; n++) Run(gpu, f, Smooth, n);
        for (long n = 10; n <= 40; n++)
        {
            var outImg = Run(gpu, f, Noise, n);
            Assert.True(Corr(outImg, a) > Corr(outImg, b), $"кадр {n}: новая картинка уже важнее старой");
        }
        // Ключевой кадр: эпизод кончился — выход = новый вход.
        f.ForceEpisode = -1f;
        var keyframe = Run(gpu, f, Noise, 41, rateOff);
        Assert.True(MaxAbsDiff(keyframe, b) <= Tol);
    }

    [Fact]
    public void Deterministic_TwoRunsGiveSamePixels()
    {
        var sums = new List<long>[2];
        for (int run = 0; run < 2; run++)
        {
            sums[run] = new List<long>();
            using var gpu = new GpuHarness(W, H);
            using var f = gpu.Create<DatamoshFilter>();
            var ov = Ov(("rate", 6f));
            for (long n = 0; n < 300; n++)
            {
                long k = n;
                var outImg = Run(gpu, f, (x, y) => Smooth(x + (int)(k * 2), y), n, ov);
                sums[run].Add(Checksum(outImg));
            }
        }
        Assert.Equal(sums[0], sums[1]);
    }

    [Fact]
    public void Performance_1080p_UnderBudget()
    {
        using var gpu = new GpuHarness(1920, 1080);
        using var f = gpu.Create<DatamoshFilter>();
        using var output = new RenderTarget(gpu.Gl, 1920, 1080);
        gpu.SetInput((x, y) => Smooth(x, y));
        f.ForceEpisode = 0.5f;
        var p = new FilterParams();
        foreach (var s in f.Params) p[s.Name] = s.Default;
        var fi = new FrameInfo(0f, 1f / 60f, 0, 1920, 1080, 0, AudioFrame.Silent);
        for (int i = 0; i < 10; i++) f.Apply(gpu.InputTexture, output, fi, p);
        gpu.Gl.Finish();
        var sw = Stopwatch.StartNew();
        const int frames = 120;
        for (int i = 0; i < frames; i++)
        {
            f.Apply(gpu.InputTexture, output, fi, p);
            gpu.Gl.Finish();
        }
        sw.Stop();
        double msPerFrame = sw.Elapsed.TotalMilliseconds / frames;
        Console.WriteLine($"datamosh 1920x1080 active: {msPerFrame:F3} ms/frame");
        Assert.True(msPerFrame < 4.0, $"{msPerFrame:F3} ms/кадр — больше порога 4 мс");
    }

    [Fact]
    public void Resize_DoesNotCrashOrLeakGlErrors()
    {
        using var gpu = new GpuHarness(W, H);
        var f = gpu.Create<DatamoshFilter>();
        f.ForceEpisode = 0.5f;
        for (long n = 0; n < 3; n++) Run(gpu, f, Smooth, n);
        f.Resize(W / 2, H / 2);
        f.Resize(W, H);
        for (long n = 3; n < 6; n++) Run(gpu, f, Smooth, n);
        f.Dispose();
        for (int i = 0; i < 5; i++)
        {
            var g = gpu.Create<DatamoshFilter>();
            g.ForceEpisode = 0.5f;
            Run(gpu, g, Smooth, i);
            g.Dispose();
        }
        Assert.Equal(GLEnum.NoError, gpu.Gl.GetError());
    }
}
