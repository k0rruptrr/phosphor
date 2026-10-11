using Phosphor.Core;
using Phosphor.Gfx;
using Silk.NET.OpenGL;

namespace Phosphor.Filters.Tests;

/// <summary>
/// Контракт ползунков: у каждой «силы» 0 — нет вклада, Max — виден (явный перебор).
/// Стандартный вход: полосы «глифов» (белые 6×12 на чёрном, сетка), вертикальный градиент, цветные заливки
/// с контрастными границами (красное к синему, голубое); 1280×720.
/// Прочие параметры фильтра в фоне выключены (нейтральный фон), чтобы эффект был один.
/// (а) при p = 0 рендер совпадает с опорным (≤ 2/255): с подчинённым параметром, изменённым при p = 0 (если он есть),
///     иначе — тот же p = 0 на другом кадре. Для экрана опора — вход без обработки (корпус выключен).
/// (б) M(p) — максимум по плиткам 64×16 средней |Δ| между рендером при p и при 0 (по каналам, шкала 0..1).
///     M(Max) ≥ 0.03 и M(Max) ≥ 1.5 × M(def), если def ≠ 0 и def ≠ Max.
///     Для разреженных временных эффектов (dropouts, fires, tracking, badTracking) M — максимум по 8 кадрам подряд.
/// </summary>
public sealed unsafe class SliderContractTests
{
    private const int W = 1280, H = 720;
    private const double ZeroTol = 2.0 / 255.0;   // (а): max |Δ| по каналу
    private const double MinTileVisible = 0.03;    // (б): M(Max), шкала 0..1
    private const double RatioMin = 1.5;           // (б): M(Max) / M(def)
    // Плитка 64×16: артефакты в основном горизонтальные и бывают в узкой полосе (шов головок — 2.5% высоты)
    private const int TileSize = 64;
    private const int TileH = 16;
    private const int SparseFrames = 8;
    private const long OtherFrame = 37;            // (а) без подчинённого: сравнение с кадром через ~0.6 с

    private static readonly HashSet<string> Sparse = new()
    {
        "vhs.dropouts", "tv.fires", "vhs.tracking", "vhs.badTracking",
    };

    private sealed record FilterCase(
        string Id, string Filter, string Param, long Frame, Dictionary<string, float> Base,
        (string Name, float Value)? Sub = null, bool CheckDefault = true);

    private static Dictionary<string, float> D(params (string K, float V)[] kv)
    {
        var d = new Dictionary<string, float>();
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    private static Dictionary<string, float> With(Dictionary<string, float> d, string k, float v)
    {
        var c = new Dictionary<string, float>(d) { [k] = v };
        return c;
    }

    // Нейтральный фон каждого фильтра: все эффекты выключены, кадр не меняется со временем.
    private static readonly Dictionary<string, float> CrtBase = D(("scanlines", 0f), ("bloom", 0f), ("flicker", 0f));
    private static readonly Dictionary<string, float> TvPalBase = D(("mode", 0f), ("sharpness", 0f), ("ghost", 0f));
    private static readonly Dictionary<string, float> TvSecamBase = D(("mode", 1f), ("fires", 0f), ("interference", 0f),
        ("edge", 0f), ("bottles", 0f), ("fibers", 0f));
    private static readonly Dictionary<string, float> VhsBase = D(("jitter", 0f), ("tracking", 0f), ("bleed", 0f),
        ("noise", 0f), ("rgbShift", 0f), ("bottomSkew", 0f), ("wobble", 0f), ("dropouts", 0f), ("badTracking", 0f),
        ("iqShift", 0f), ("chromaClip", 0f), ("saturation", 1f), ("lumaRes", 1920f), ("chromaRes", 400f));
    private static readonly Dictionary<string, float> PhosphorBase = D(("persistence", 0f), ("color", 0f), ("brightness", 1f));
    private static readonly Dictionary<string, float> ChromaBase = D();

    private static readonly FilterCase[] FilterCases =
    {
        new("crt.scanlines", "crt", "scanlines", 0, CrtBase),
        new("crt.bloom", "crt", "bloom", 0, CrtBase, ("bloomRadius", 16f)),
        new("crt.flicker", "crt", "flicker", 5, CrtBase),
        // дефолт = Max = 1: условие «≥ 1.5× дефолта» по смыслу невыполнимо, проверяется только Max
        new("tv.amount", "tv", "amount", 0, TvPalBase, null, CheckDefault: false),
        new("tv.sharpness", "tv", "sharpness", 0, TvPalBase),
        new("tv.ghost", "tv", "ghost", 0, TvPalBase, ("ghostDelay", 0.06f)),
        new("tv.fires", "tv", "fires", 0, TvSecamBase, ("fireSize", 2.5f)),
        new("tv.interference", "tv", "interference", 0, TvSecamBase),
        new("tv.edge", "tv", "edge", 0, TvSecamBase),
        new("tv.bottles", "tv", "bottles", 0, TvSecamBase),
        new("tv.fibers", "tv", "fibers", 0, TvSecamBase),
        new("vhs.jitter", "vhs", "jitter", 0, VhsBase),
        new("vhs.tracking", "vhs", "tracking", -1, VhsBase),   // -1: кадр, где эпизод идёт на Max (подбирается в тесте)
        new("vhs.bleed", "vhs", "bleed", 0, VhsBase),
        new("vhs.noise", "vhs", "noise", 0, VhsBase),
        new("vhs.rgbShift", "vhs", "rgbShift", 0, VhsBase),
        new("vhs.bottomSkew", "vhs", "bottomSkew", 0, VhsBase),
        new("vhs.wobble", "vhs", "wobble", 0, VhsBase),
        new("vhs.dropouts", "vhs", "dropouts", 5, VhsBase),
        new("vhs.badTracking", "vhs", "badTracking", 0, VhsBase, ("bandSpeed", 3f)),
        new("vhs.iqShift", "vhs", "iqShift", 0, VhsBase),
        new("vhs.chromaClip", "vhs", "chromaClip", 0, VhsBase),
        // дефолт = Max = 1 (см. tv.amount)
        new("phosphor.tint", "phosphor", "tint", 0, PhosphorBase, null, CheckDefault: false),
        new("chroma.amount", "chroma", "amount", 0, ChromaBase),
    };

    private static IFilter MakeFilter(GpuHarness gpu, string id) => id switch
    {
        "crt" => gpu.Create<CrtFilter>(),
        "tv" => gpu.Create<TvFilter>(),
        "vhs" => gpu.Create<VhsFilter>(),
        "phosphor" => gpu.Create<PhosphorFilter>(),
        "chroma" => gpu.Create<ChromaFilter>(),
        _ => throw new ArgumentException($"нет фильтра '{id}'", nameof(id)),
    };

    /// <summary>Стандартный вход. (x, y) — y сверху. Цветные границы: красное к синему, голубое отдельно.</summary>
    private static (float r, float g, float b) Pattern(int x, int y)
    {
        float t = y / (float)(H - 1);
        if (x >= 880 && x < 1080 && y >= 120 && y < 260) return (0.9f, 0.1f, 0.1f);
        if (x >= 1080 && x < 1200 && y >= 120 && y < 260) return (0.1f, 0.2f, 0.9f);
        if (x >= 160 && x < 420 && y >= 470 && y < 620) return (0.1f, 0.7f, 0.8f);
        bool lane = (y / 120) % 2 == 0;
        if (lane && x >= 40 && x % 16 < 6 && y % 24 < 12) return (1f, 1f, 1f);
        return (0.05f + 0.2f * t, 0.05f, 0.12f - 0.08f * t);
    }

    private static float[] Flatten(float[,,] img)
    {
        var r = new float[H * W * 3];
        int i = 0;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            r[i++] = img[y, x, 0]; r[i++] = img[y, x, 1]; r[i++] = img[y, x, 2];
        }
        return r;
    }

    private static float[] Render(GpuHarness gpu, IFilter f, long frame, Dictionary<string, float> o) =>
        Flatten(gpu.Run(f, frame, o));

    private static (double mean, double max) Diff(float[] a, float[] b)
    {
        double sum = 0, mx = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = Math.Abs(a[i] - b[i]);
            sum += d;
            if (d > mx) mx = d;
        }
        return (sum / a.Length, mx);
    }

    /// <summary>Максимум по плиткам TileSize×TileSize средней |Δ| (по каналам) между двумя rgb-массивами W×H.</summary>
    private static double TileMax(float[] a, float[] b)
    {
        double best = 0;
        for (int ty = 0; ty < H; ty += TileH)
        for (int tx = 0; tx < W; tx += TileSize)
        {
            double sum = 0;
            int n = 0;
            for (int y = ty; y < Math.Min(ty + TileH, H); y++)
            for (int x = tx; x < Math.Min(tx + TileSize, W); x++)
            {
                int i = (y * W + x) * 3;
                for (int c = 0; c < 3; c++) { sum += Math.Abs(a[i + c] - b[i + c]); n++; }
            }
            best = Math.Max(best, sum / n);
        }
        return best;
    }

    public static IEnumerable<object[]> FilterIds() => FilterCases.Select(c => new object[] { c.Id });

    [Theory]
    [MemberData(nameof(FilterIds))]
    public void Filter_SliderZeroIsNoContribution_MaxIsVisible(string id)
    {
        var c0 = FilterCases.Single(x => x.Id == id);
        using var gpu = new GpuHarness(W, H);
        gpu.SetInput((x, y) => Pattern(x, y));
        using var f = MakeFilter(gpu, c0.Filter);
        var spec = f.Params.Single(s => s.Name == c0.Param);

        // Кадр для (а) и (б): у эпизодических эффектов (трекинг) берём кадр с максимальным откликом при Max
        long frame = c0.Frame;
        if (frame < 0)
        {
            var off = Render(gpu, f, 0, With(c0.Base, c0.Param, 0f));
            double best = -1;
            for (long fr = 0; fr <= 2400; fr += 12)
            {
                double d = TileMax(off, Render(gpu, f, fr, With(c0.Base, c0.Param, spec.Max)));
                if (d > best) { best = d; frame = fr; }
            }
        }
        var c = c0 with { Frame = frame };

        // (а) 0 = нет вклада
        var zero = With(c.Base, c.Param, 0f);
        var zeroNow = Render(gpu, f, c.Frame, zero);
        float[] zeroRef;
        string refName;
        if (c.Sub is { } sub)
        {
            zeroRef = Render(gpu, f, c.Frame, With(zero, sub.Name, sub.Value));
            refName = $"{sub.Name}={sub.Value}";
        }
        else
        {
            zeroRef = Render(gpu, f, c.Frame + OtherFrame, zero);
            refName = $"кадр {c.Frame + OtherFrame}";
        }
        var dz = Diff(zeroNow, zeroRef);
        Assert.True(dz.max <= ZeroTol,
            $"{id}: при {c.Param}=0 есть вклад (опора: {refName}), max |Δ| {dz.max * 255:F1}/255");

        // (б) M(p) по плиткам; для разреженных эффектов — максимум по 8 кадрам
        int nFrames = Sparse.Contains(id) ? SparseFrames : 1;
        double Visible(double value)
        {
            var over = With(c.Base, c.Param, (float)value);
            double best = 0;
            for (int k = 0; k < nFrames; k++)
            {
                long fr = c.Frame + k;
                best = Math.Max(best, TileMax(Render(gpu, f, fr, zero), Render(gpu, f, fr, over)));
            }
            return best;
        }

        double mMax = Visible(spec.Max);
        Assert.True(mMax >= MinTileVisible,
            $"{id}: при Max={spec.Max} почти не виден: M {mMax:F4} (нужно ≥ {MinTileVisible})");

        if (c.CheckDefault && spec.Default != 0f && spec.Default != spec.Max)
        {
            double mDef = Visible(spec.Default);
            Assert.True(mMax >= RatioMin * mDef,
                $"{id}: M у Max {mMax:F4} меньше 1.5× M у дефолта {spec.Default} ({mDef:F4})");
        }
    }

    // ---- Экран (ScreenPass): корпус монитора ----

    private sealed record ScreenCase(string Id, string Param, bool BezelTv = false, bool CheckDefault = true);

    private static readonly ScreenCase[] ScreenCases =
    {
        new("screen.glare", "glare"),
        new("screen.vignette", "vignette"),
        // кривизна: плиточный максимум немонотонен (чёрные углы есть уже у дефолта) — только «Max виден»
        new("screen.curvature", "curvature", CheckDefault: false),
        new("screen.mask", "mask"),
        new("screen.overscan", "overscan"),
        // только (б): рамка tv рисуется и без свечения, опора «вход» не применима
        // отсвет — смешивание, насыщается к 1; это не «перебор»-эффект — только «Max виден»
        new("screen.bezelGlow", "bezelGlow", BezelTv: true, CheckDefault: false),
    };

    /// <summary>Корпус с выключенными силами; рамка tv — только для проверки свечения рамки.</summary>
    private static ScreenConfig NeutralScreen(bool bezelTv) => new()
    {
        Enabled = true,
        Curvature = 0f, CornerRadius = 0f, Vignette = 0f, Overscan = 0f, Glare = 0f,
        Bezel = bezelTv ? "tv" : "none", BezelGlow = 0.5f,
        Mask = 0f, Interlace = 0f, Trail = 0f,
    };

    private static uint UploadPattern(GL gl)
    {
        var px = new float[W * H * 4];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            var (r, g, b) = Pattern(x, y);
            int i = (y * W + x) * 4;
            px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 1f;
        }
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)W, (uint)H, 0, PixelFormat.Rgba, PixelType.Float, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        fixed (float* p = px)
            gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)W, (uint)H, PixelFormat.Rgba, PixelType.Float, p);
        return tex;
    }

    /// <summary>Вход в том же порядке строк, что и у ReadRgb (строка 0 — нижняя текстуры), rgb.</summary>
    private static float[] PatternRgb()
    {
        var r = new float[W * H * 3];
        int i = 0;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            var (pr, pg, pb) = Pattern(x, y);
            r[i++] = pr; r[i++] = pg; r[i++] = pb;
        }
        return r;
    }

    private static float[] ReadRgb(GL gl, uint tex)
    {
        uint fbo = gl.GenFramebuffer();
        try
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
            var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete) throw new InvalidOperationException($"FBO неполный: {status}");
            var px = new float[W * H * 4];
            gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
            fixed (float* p = px)
                gl.ReadPixels(0, 0, (uint)W, (uint)H, PixelFormat.Rgba, PixelType.Float, p);
            var rgb = new float[W * H * 3];
            for (int i = 0; i < W * H; i++)
            {
                rgb[i * 3] = px[i * 4]; rgb[i * 3 + 1] = px[i * 4 + 1]; rgb[i * 3 + 2] = px[i * 4 + 2];
            }
            return rgb;
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(fbo);
        }
    }

    public static IEnumerable<object[]> ScreenIds() => ScreenCases.Select(c => new object[] { c.Id });

    [Theory]
    [MemberData(nameof(ScreenIds))]
    public void Screen_SliderZeroIsNoContribution_MaxIsVisible(string id)
    {
        var c = ScreenCases.Single(x => x.Id == id);
        using var gpu = new GpuHarness(W, H);
        using var sp = new ScreenPass(gpu.Gl);
        uint inTex = UploadPattern(gpu.Gl);
        var spec = ScreenConfig.Specs.Single(s => s.Name == c.Param);
        var input = PatternRgb();

        float[] Shot(ScreenConfig cfg) => ReadRgb(gpu.Gl, sp.Apply(inTex, W, H, cfg));

        // (а) 0 = нет вклада: вход проходит без изменений (для bezelGlow не применимо)
        var zeroCfg = NeutralScreen(c.BezelTv);
        zeroCfg.Set(c.Param, 0);
        var zeroOut = Shot(zeroCfg);
        if (!c.BezelTv)
        {
            var dz = Diff(zeroOut, input);
            Assert.True(dz.max <= ZeroTol, $"{id}: при {c.Param}=0 корпус меняет картинку, max |Δ| {dz.max * 255:F1}/255");
        }

        // (б) M по плиткам
        var maxCfg = NeutralScreen(c.BezelTv);
        maxCfg.Set(c.Param, spec.Max);
        double mMax = TileMax(zeroOut, Shot(maxCfg));
        Assert.True(mMax >= MinTileVisible, $"{id}: при Max={spec.Max} почти не виден: M {mMax:F4} (нужно ≥ {MinTileVisible})");

        if (c.CheckDefault && spec.Default != 0f && spec.Default != spec.Max)
        {
            var defCfg = NeutralScreen(c.BezelTv);
            defCfg.Set(c.Param, spec.Default);
            double mDef = TileMax(zeroOut, Shot(defCfg));
            Assert.True(mMax >= RatioMin * mDef,
                $"{id}: M у Max {mMax:F4} меньше 1.5× M у дефолта {spec.Default} ({mDef:F4})");
        }

        gpu.Gl.DeleteTexture(inTex);
    }
}
