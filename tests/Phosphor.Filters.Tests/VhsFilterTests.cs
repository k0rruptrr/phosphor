using Phosphor.Filters;
using Phosphor.Gfx;

namespace Phosphor.Filters.Tests;

public class VhsFilterTests
{
    private static readonly string[] ParamNames =
    {
        "jitter", "tracking", "bleed", "noise", "rgbShift",
        "bottomSkew", "wobble", "saturation", "dropouts",
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
    };

    private const string ShaderName = "vhs.frag";

    [Fact]
    public void Id_IsVhs()
    {
        Assert.Equal("vhs", new VhsFilter().Id);
    }

    [Fact]
    public void Params_HasExactlyTheNineExpectedNames()
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
        foreach (var frame in new long[] { 60, 180, 300, 420, 540 })
        {
            // Скользящее окно 7 строк: штрихи — шум строки, а не жёсткость края; проверяем огибающую полосы.
            var prof = Smooth(RowMeanDev(gpu.Run(f, frame, BandOnly)), 7);
            float maxJump = 0f, peak = 0f;
            for (int y = 1; y < prof.Length; y++) maxJump = Math.Max(maxJump, Math.Abs(prof[y] - prof[y - 1]));
            foreach (var v in prof) peak = Math.Max(peak, Math.Abs(v));
            Assert.True(maxJump < 0.12f, $"кадр {frame}: скачок между соседними строками {maxJump:F3}");
            Assert.True(peak > 0.08f, $"кадр {frame}: пик отклонения {peak:F3}");
        }
    }

    [Fact]
    public void Band_HasHorizontalStreaks_MoreThanVerticalCorrelation()
    {
        using var gpu = new GpuHarness(640, 360);
        gpu.SetInput((x, y) => (0.5f, 0.5f, 0.5f));
        using var f = gpu.Create<VhsFilter>();
        var img = gpu.Run(f, 300, BandOnly);
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

    [Fact]
    public void Band_RestsInLowerThirdOfFrame_Over600Frames()
    {
        using var gpu = new GpuHarness(320, 180);
        gpu.SetInput((x, y) => (0.5f, 0.5f, 0.5f));
        using var f = gpu.Create<VhsFilter>();
        int h = 180, inLow = 0;
        for (long frame = 0; frame < 600; frame++)
        {
            var prof = RowMeanDev(gpu.Run(f, frame, BandOnly));
            int c = 0;
            for (int y = 1; y < h; y++) if (prof[y] > prof[c]) c = y;   // строка пика (0 — верх)
            if (h - 1 - c < 0.3f * h) inLow++;                           // от низа кадра
        }
        Assert.True(inLow >= 0.7 * 600, $"центр полосы в нижних 30% кадра: {inLow} из 600");
    }
}
