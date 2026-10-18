using System.Diagnostics;
using Phosphor.Core;
using Phosphor.Filters;
using Phosphor.Gfx;
using Silk.NET.OpenGL;

namespace Phosphor.Filters.Tests;

public class GlowFilterTests
{
    private static readonly string[] ParamNames = { "strength", "radius", "core", "threshold", "saturation" };

    private static readonly Dictionary<string, float> Defaults = new()
    {
        ["strength"] = 1f,
        ["radius"] = 20f,
        ["core"] = 0.7f,
        ["threshold"] = 0.2f,
        ["saturation"] = 1.3f,
    };

    private static readonly string[] Shaders =
    {
        "glow.frag", "glow_prefilter.frag", "glow_down.frag", "glow_up.frag",
    };

    private static string LoadShader(string fileName)
    {
        using var s = typeof(GlowFilter).Assembly.GetManifestResourceStream("Phosphor.Filters.Shaders." + fileName);
        Assert.NotNull(s);
        using var r = new StreamReader(s!);
        return r.ReadToEnd();
    }

    private static FrameInfo Frame(long frame, int w, int h) =>
        new(frame / 60f, 1f / 60f, frame, w, h, 0, AudioFrame.Silent);

    /// <summary>Чёрный кадр с белым квадратом 4×4, центр в точке (128, 128) континуума (пиксели 126..129).</summary>
    private static void SetWhiteDot(GpuHarness gpu) =>
        gpu.SetInput((x, y) => x is >= 126 and <= 129 && y is >= 126 and <= 129 ? (1f, 1f, 1f) : (0f, 0f, 0f));

    /// <summary>Чёрный кадр с оранжевым квадратом 4×4 (1, 0.5, 0.1) в той же точке.</summary>
    private static void SetOrangeDot(GpuHarness gpu) =>
        gpu.SetInput((x, y) => x is >= 126 and <= 129 && y is >= 126 and <= 129 ? (1f, 0.5f, 0.1f) : (0f, 0f, 0f));

    /// <summary>Чёрный кадр с горизонтальным штрихом 4 px толщиной (y 126..129), x 60..195, цвет (1, 0.5, 0.1).</summary>
    private static void SetOrangeBar(GpuHarness gpu) =>
        gpu.SetInput((x, y) => x is >= 60 and <= 195 && y is >= 126 and <= 129 ? (1f, 0.5f, 0.1f) : (0f, 0f, 0f));

    private static float Hue(float r, float g, float b)
    {
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        float d = max - min;
        if (d < 1e-6f) return float.NaN;
        float h;
        if (max == r) h = 60f * ((g - b) / d);
        else if (max == g) h = 60f * ((b - r) / d + 2f);
        else h = 60f * ((r - g) / d + 4f);
        return (h + 360f) % 360f;
    }

    private static float HueDiff(float a, float b)
    {
        float d = MathF.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }

    [Fact]
    public void Id_IsGlow()
    {
        using var f = new GlowFilter();
        Assert.Equal("glow", f.Id);
    }

    [Fact]
    public void Params_HasExactlyTheFiveExpectedNames()
    {
        var names = new GlowFilter().Params.Select(p => p.Name).ToArray();
        Assert.Equal(ParamNames, names);
    }

    [Fact]
    public void Params_MinLessOrEqualDefaultLessOrEqualMax()
    {
        foreach (var p in new GlowFilter().Params)
        {
            Assert.True(p.Min <= p.Default, $"{p.Name}: Min {p.Min} > Default {p.Default}");
            Assert.True(p.Default <= p.Max, $"{p.Name}: Default {p.Default} > Max {p.Max}");
        }
    }

    [Fact]
    public void Params_DefaultsAndRangesMatchSpec()
    {
        var ps = new GlowFilter().Params.ToDictionary(p => p.Name);
        Assert.Equal(0f, ps["strength"].Min); Assert.Equal(3f, ps["strength"].Max);
        Assert.Equal(2f, ps["radius"].Min); Assert.Equal(64f, ps["radius"].Max);
        Assert.Equal(0f, ps["core"].Min); Assert.Equal(2f, ps["core"].Max);
        Assert.Equal(0f, ps["threshold"].Min); Assert.Equal(1f, ps["threshold"].Max);
        Assert.Equal(0f, ps["saturation"].Min); Assert.Equal(2f, ps["saturation"].Max);
        foreach (var p in ps.Values)
            Assert.Equal(Defaults[p.Name], p.Default);
    }

    [Fact]
    public void Params_LabelsShortAndDescriptionsFilled()
    {
        foreach (var p in new GlowFilter().Params)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Label), $"{p.Name}: нет Label");
            Assert.True(p.Label!.Length <= 22, $"{p.Name}: Label длиннее 22 символов");
            Assert.False(string.IsNullOrWhiteSpace(p.Description), $"{p.Name}: нет Description");
        }
    }

    [Theory]
    [MemberData(nameof(ShaderNames))]
    public void ShaderResource_Exists(string fileName)
    {
        Assert.NotNull(typeof(GlowFilter).Assembly.GetManifestResourceStream("Phosphor.Filters.Shaders." + fileName));
    }

    public static IEnumerable<object[]> ShaderNames() => Shaders.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(ShaderNames))]
    public void Shader_StartsWithVersion330Core(string fileName)
    {
        Assert.StartsWith("#version 330 core", LoadShader(fileName));
    }

    [Theory]
    [MemberData(nameof(ShaderNames))]
    public void Shader_BracesAndParenthesesAreBalanced(string fileName)
    {
        var shader = LoadShader(fileName);
        Assert.Equal(shader.Count(ch => ch == '{'), shader.Count(ch => ch == '}'));
        Assert.Equal(shader.Count(ch => ch == '('), shader.Count(ch => ch == ')'));
    }

    [Fact]
    public void Shader_CompositeDeclaresEveryParamAndInterface()
    {
        var shader = LoadShader("glow.frag");
        foreach (var name in ParamNames)
            if (name is "strength" or "core" or "saturation")
                Assert.Contains($"uniform float p_{name};", shader);
        Assert.Contains("out vec4 fragColor", shader);
        Assert.Contains("in vec2 vUv", shader);
        Assert.Contains("uniform sampler2D uInput", shader);
        Assert.Contains("uniform sampler2D uCore", shader);
        Assert.Contains("uniform sampler2D uHalo", shader);
    }

    [Fact]
    public void Shader_HasNoUndeclaredParams()
    {
        var shader = LoadShader("glow.frag");
        var matches = System.Text.RegularExpressions.Regex.Matches(shader, @"uniform\s+float\s+p_(\w+)\s*;");
        Assert.All(matches.Cast<System.Text.RegularExpressions.Match>(),
            m => Assert.Contains(m.Groups[1].Value, ParamNames));
    }

    [Fact]
    public void Strength0_Core0_IsIdentity()
    {
        using var gpu = new GpuHarness(64, 48);
        using var f = gpu.Create<GlowFilter>();
        // Вход: детерминированный шум, сохраняем его в src для сравнения.
        var src = new float[48, 64, 3];
        gpu.SetInput((x, y) =>
        {
            float r = Hash01(x, y, 1), g = Hash01(x, y, 2), b = Hash01(x, y, 3);
            src[y, x, 0] = r; src[y, x, 1] = g; src[y, x, 2] = b;
            return (r, g, b);
        });
        var outImg = gpu.Run(f, 1, new Dictionary<string, float> { ["strength"] = 0f, ["core"] = 0f });
        int bad = 0;
        for (int y = 0; y < 48; y++)
        for (int x = 0; x < 64; x++)
        for (int c = 0; c < 3; c++)
            if (MathF.Abs(outImg[y, x, c] - src[y, x, c]) > 2f / 255f) bad++;
        Assert.Equal(0, bad);
    }

    [Fact]
    public void DefaultGlow_AddsHaloAroundDotCompared()
    {
        using var gpu = new GpuHarness(256, 256);
        using var f = gpu.Create<GlowFilter>();
        SetOrangeBar(gpu);
        var off = gpu.Run(f, 1, new Dictionary<string, float> { ["strength"] = 0f, ["core"] = 0f });
        var on = gpu.Run(f, 1);
        // Над штрихом, на расстоянии 12 px: дефолтный ореол добавляет яркость к тождеству.
        float delta = GpuHarness.Luma(on, 128, 127 - 12) - GpuHarness.Luma(off, 128, 127 - 12);
        Assert.True(delta > 0.01f, $"добавка ореола на 12 px: {delta}");
    }

    /// <summary>Детерминированный хеш в [0, 1) от координат и канала.</summary>
    private static float Hash01(int x, int y, int salt)
    {
        uint h = (uint)(x * 374761393 + y * 668265263 + salt * 2246822519);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / 16777216f;
    }

    [Fact]
    public void WhiteDot_HaloIsRadiallySymmetric()
    {
        using var gpu = new GpuHarness(256, 256);
        using var f = gpu.Create<GlowFilter>();
        SetWhiteDot(gpu);
        var img = gpu.Run(f, 1);
        foreach (int d in new[] { 16, 24, 32 })
        {
            float right = GpuHarness.Luma(img, 128 + d, 128);
            float left = GpuHarness.Luma(img, 127 - d, 128);
            float up = GpuHarness.Luma(img, 128, 127 - d);
            float down = GpuHarness.Luma(img, 128, 128 + d);
            float max = MathF.Max(MathF.Max(right, left), MathF.Max(up, down));
            float min = MathF.Min(MathF.Min(right, left), MathF.Min(up, down));
            Assert.True(min > 0f, $"d={d}: ореол не виден");
            Assert.True((max - min) / max <= 0.10f,
                $"d={d}: разброс по 4 направлениям {(max - min) / max:P1} (right {right}, left {left}, up {up}, down {down})");
        }
    }

    [Fact]
    public void WhiteDot_HaloDecaysMonotonically()
    {
        using var gpu = new GpuHarness(256, 256);
        using var f = gpu.Create<GlowFilter>();
        SetWhiteDot(gpu);
        var img = gpu.Run(f, 1);
        float prev = float.MaxValue;
        for (int d = 6; d <= 60; d += 3)
        {
            float v = GpuHarness.Luma(img, 128 + d, 128);
            Assert.True(v <= prev + 1e-4f, $"d={d}: яркость {v} выше предыдущей {prev}");
            prev = v;
        }
    }

    [Fact]
    public void LargerRadius_GivesBrighterHaloAtDistance()
    {
        using var gpu = new GpuHarness(256, 256);
        using var f = gpu.Create<GlowFilter>();
        SetWhiteDot(gpu);
        var wide = gpu.Run(f, 1, new Dictionary<string, float> { ["radius"] = 40f });
        float wideLuma = GpuHarness.Luma(wide, 128 + 40, 128);
        var narrow = gpu.Run(f, 1, new Dictionary<string, float> { ["radius"] = 8f });
        float narrowLuma = GpuHarness.Luma(narrow, 128 + 40, 128);
        Assert.True(wideLuma > narrowLuma * 1.5f,
            $"на расстоянии 40 px: radius 40 → {wideLuma}, radius 8 → {narrowLuma}");
    }

    [Fact]
    public void OrangeStroke_GivesOrangeHalo()
    {
        using var gpu = new GpuHarness(256, 256);
        using var f = gpu.Create<GlowFilter>();
        SetOrangeBar(gpu);
        var img = gpu.Run(f, 1);
        float strokeHue = Hue(1f, 0.5f, 0.1f);
        // Ореол над штрихом (по вертикали, вне самого штриха).
        foreach (int d in new[] { 16, 20, 24 })
        {
            int y = 127 - d;
            float r = img[y, 128, 0], g = img[y, 128, 1], b = img[y, 128, 2];
            Assert.True(GpuHarness.Luma(img, 128, y) > 0.01f, $"d={d}: ореол не виден");
            float h = Hue(r, g, b);
            Assert.True(HueDiff(h, strokeHue) <= 15f, $"d={d}: оттенок ореола {h}°, штриха {strokeHue}°");
            Assert.True(r > g && g > b, $"d={d}: ореол не оранжевый: {r} {g} {b}");
        }
    }

    /// <summary>На самом штрихе ядро светлеет, но оттенок источника сохраняется (±10°), а не уходит в жёлтый.</summary>
    [Fact]
    public void StrokeCore_LighterButKeepsSourceHue()
    {
        using var gpu = new GpuHarness(256, 256);
        using var f = gpu.Create<GlowFilter>();
        SetOrangeDot(gpu);
        var img = gpu.Run(f, 1);
        float r = img[128, 128, 0], g = img[128, 128, 1], b = img[128, 128, 2];
        float srcHue = Hue(1f, 0.5f, 0.1f);
        float h = Hue(r, g, b);
        Console.WriteLine($"Stroke core: {r:F3} {g:F3} {b:F3}, hue {h:F1} (src {srcHue:F1})");
        Assert.True(HueDiff(h, srcHue) <= 10f, $"оттенок ядра {h}°, источника {srcHue}°");
        Assert.True(r + g + b > 1f + 0.5f + 0.1f, $"ядро не светлее штриха: {r} {g} {b}");
        Assert.True(r <= 1f + 1e-4f, $"пересвет: {r}");
    }

    [Fact]
    public void Strength3_DoesNotOverexpose_KeepsHue()
    {
        using var gpu = new GpuHarness(256, 256);
        using var f = gpu.Create<GlowFilter>();
        SetOrangeDot(gpu);
        var img = gpu.Run(f, 1, new Dictionary<string, float> { ["strength"] = 3f });
        float r = img[128, 128, 0], g = img[128, 128, 1], b = img[128, 128, 2];
        Assert.True(r <= 1f + 1e-4f && g <= 1f && b <= 1f, $"пересвет: {r} {g} {b}");
        Assert.True(r > g && g > b, $"оттенок потерян: {r} {g} {b}");
    }

    /// <summary>
    /// Профиль ореола на тонком штрихе 4 px (оранжевый, яркость = max канал 1) при параметрах по умолчанию.
    /// Добавка (на чёрном фоне = выход) замеряется по max каналу на расстоянии от края штриха.
    /// Цели: radius/2 ≥ 12%, radius ≥ 4%, 2·radius ≤ 2%.
    /// </summary>
    [Fact]
    public void ThinBar_HaloProfileMatchesTarget()
    {
        using var gpu = new GpuHarness(256, 256);
        using var f = gpu.Create<GlowFilter>();
        SetOrangeBar(gpu);
        var img = gpu.Run(f, 1);
        float radius = Defaults["radius"];
        float At(int d)
        {
            int y = 125 - d;           // d = 0 — ближайший пиксель над штрихом
            return MathF.Max(img[y, 128, 0], MathF.Max(img[y, 128, 1], img[y, 128, 2]));
        }
        float a = At((int)(radius / 2)), b = At((int)radius), c = At((int)(2 * radius));
        Console.WriteLine($"Glow profile: r/2={a:F4} r={b:F4} 2r={c:F4}");
        Assert.True(a >= 0.12f, $"на radius/2 добавка {a:P1}, нужно ≥ 12%");
        Assert.True(b >= 0.04f, $"на radius добавка {b:P1}, нужно ≥ 4%");
        Assert.True(c <= 0.02f, $"на 2·radius добавка {c:P1}, нужно ≤ 2%");
    }

    [Fact]
    public void Deterministic_TwoRunsGiveSamePixels()
    {
        using var gpu = new GpuHarness(128, 96);
        using var f = gpu.Create<GlowFilter>();
        SetOrangeDot(gpu);
        float[,,]? first = null;
        for (int run = 0; run < 2; run++)
        {
            float[,,]? last = null;
            for (long frame = 0; frame < 10; frame++)
                last = gpu.Run(f, frame);
            if (first is null) { first = last; continue; }
            int diff = 0;
            for (int y = 0; y < 96; y++)
            for (int x = 0; x < 128; x++)
            for (int c = 0; c < 3; c++)
                if (first![y, x, c] != last![y, x, c]) diff++;
            Assert.Equal(0, diff);
        }
    }

    [Fact]
    public void Performance_1080p_UnderBudget()
    {
        using var gpu = new GpuHarness(1920, 1080);
        using var f = gpu.Create<GlowFilter>();
        gpu.SetInput((x, y) => ((x + y) % 97 < 6) ? (1f, 0.6f, 0.2f) : (0.05f, 0.04f, 0.03f));
        using var target = new RenderTarget(gpu.Gl, 1920, 1080);
        var p = new FilterParams();
        foreach (var s in f.Params) p[s.Name] = s.Default;
        for (int i = 0; i < 10; i++) f.Apply(gpu.InputTexture, target, Frame(i, 1920, 1080), p);
        gpu.Gl.Finish();

        const int frames = 120;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            f.Apply(gpu.InputTexture, target, Frame(i, 1920, 1080), p);
            gpu.Gl.Finish();
        }
        sw.Stop();
        double msPerFrame = sw.Elapsed.TotalMilliseconds / frames;
        Console.WriteLine($"Glow 1920x1080: {msPerFrame:F3} мс/кадр");
        Assert.True(msPerFrame <= 4.0, $"{msPerFrame:F3} мс/кадр, порог 4 мс");
    }

    [Fact]
    public void Resize_DoesNotThrowAndKeepsWorking()
    {
        using var gpu = new GpuHarness(256, 256);
        using var f = gpu.Create<GlowFilter>();
        SetWhiteDot(gpu);
        var p = new FilterParams();
        foreach (var s in f.Params) p[s.Name] = s.Default;
        foreach (var (w, h) in new[] { (640, 360), (1280, 720), (100, 50), (1, 1), (640, 360) })
        {
            f.Resize(w, h);
            using var target = new RenderTarget(gpu.Gl, w, h);
            f.Apply(gpu.InputTexture, target, Frame(1, w, h), p);
            gpu.Gl.Finish();
            Assert.Equal(GLEnum.NoError, gpu.Gl.GetError());
        }
    }
}
