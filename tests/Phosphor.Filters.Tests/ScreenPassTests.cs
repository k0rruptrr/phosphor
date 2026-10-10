using Phosphor.Core;
using Phosphor.Gfx;
using Phosphor.Platform;
using Silk.NET.OpenGL;

namespace Phosphor.Filters.Tests;

/// <summary>ScreenPass («корпус монитора») на настоящем GPU: геометрия, блик, рамка, статичность.</summary>
public sealed unsafe class ScreenPassTests
{
    private const int W = 128, H = 128;

    /// <summary>Текстура W×H из готового массива rgba (float, построчно снизу вверх).</summary>
    private static uint MakeTex(GL gl, float[] rgba)
    {
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)W, (uint)H, 0, PixelFormat.Rgba, PixelType.Float, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        fixed (float* p = rgba)
            gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)W, (uint)H, PixelFormat.Rgba, PixelType.Float, p);
        return tex;
    }

    /// <summary>Однотонная картинка (rgb = v, a = 1).</summary>
    private static float[] Flat(float v)
    {
        var px = new float[W * H * 4];
        for (int i = 0; i < W * H; i++) { px[i * 4] = v; px[i * 4 + 1] = v; px[i * 4 + 2] = v; px[i * 4 + 3] = 1f; }
        return px;
    }

    /// <summary>Градиент по X: r = x / (W-1), g = b = r, a = 1.</summary>
    private static float[] GradientX()
    {
        var px = new float[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float v = x / (float)(W - 1);
                int i = (y * W + x) * 4;
                px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 1f;
            }
        return px;
    }

    /// <summary>Все пиксели текстуры (rgba float), читается через временный FBO.</summary>
    private static float[] ReadAll(GL gl, uint tex)
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
            var err = gl.GetError();
            if (err != GLEnum.NoError) throw new InvalidOperationException($"GL error: {err}");
            return px;
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(fbo);
        }
    }

    /// <summary>Яркость красного канала пикселя (x, y), y — снизу вверх (GL).</summary>
    private static float R(float[] px, int x, int y) => px[(y * W + x) * 4];

    /// <summary>Конфиг, где всё геометрическое выключено; поля переопределяются точечно.</summary>
    private static ScreenConfig Off() => new()
    {
        Trail = 0f, Curvature = 0f, CornerRadius = 0f, Vignette = 0f, Overscan = 0f,
        Glare = 0f, Bezel = "none", BezelColor = "black", BezelSize = 0.06f, BezelGlow = 0.5f,
    };

    [Fact]
    public void AllOff_ReturnsSameTexture()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        uint white = MakeTex(gl, Flat(1f));
        try
        {
            using var sp = new ScreenPass(gl);
            Assert.Equal(white, sp.Apply(white, W, H, Off()));
        }
        finally { gl.DeleteTexture(white); }
    }

    [Fact]
    public void Disabled_FlatScreen_ReturnsSameTextureDespiteFields()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        uint white = MakeTex(gl, Flat(1f));
        try
        {
            using var sp = new ScreenPass(gl);
            var cfg = Off();
            cfg.Enabled = false;
            cfg.Bezel = "tv";
            cfg.Curvature = 0.2f;
            Assert.Equal(white, sp.Apply(white, W, H, cfg));
        }
        finally { gl.DeleteTexture(white); }
    }

    [Fact]
    public void Overscan_ScalesPictureTowardCenter_CenterUnchanged()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        uint src = MakeTex(gl, GradientX());
        try
        {
            using var sp = new ScreenPass(gl);
            var cfg = Off();
            cfg.Overscan = 0.05f;
            var px = ReadAll(gl, sp.Apply(src, W, H, cfg));
            // Центр: вход в центре — 0.5
            Assert.InRange(R(px, W / 2, H / 2), 0.48f, 0.52f);
            // Край: картинка увеличена, край показывает вход ближе к центру, чем без overscan (~0.008)
            Assert.True(R(px, 1, H / 2) > 0.04f, $"край {R(px, 1, H / 2)}");
        }
        finally { gl.DeleteTexture(src); }
    }

    [Fact]
    public void Curvature_CornerRadiusCutsCorners_WithoutBezel()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        uint white = MakeTex(gl, Flat(1f));
        try
        {
            using var sp = new ScreenPass(gl);
            // Без рамки: угол за скруглением (радиус 0.1·H) чёрный, центр белый.
            var cfg = Off();
            cfg.Curvature = 0.1f;
            cfg.CornerRadius = 0.1f;
            var px = ReadAll(gl, sp.Apply(white, W, H, cfg));
            Assert.True(R(px, 0, 0) < 0.01f, $"угол {R(px, 0, 0)}");
            Assert.True(R(px, W / 2, H / 2) > 0.9f, $"центр {R(px, W / 2, H / 2)}");
        }
        finally { gl.DeleteTexture(white); }
    }

    // Кривизна с рамкой: проём гнётся той же функцией, что картинка. Проверяем на 512×512, рамка thin (b ≈ 31 px).
    private static float[] ScanTopWhiteY(float[] px, int x)
    {
        // Возвращает верхнюю белую точку столбца (y, GL) — для проверки формы проёма.
        for (int y = N - 1; y >= 0; y--)
            if (RN(px, x, y) > 0.5f) return new[] { (float)y };
        return new[] { -1f };
    }

    [Fact]
    public void Bezel_ApertureBowed_TopMidpointHigherThanNearCorner()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        uint white = MakeN(gl, 1f);
        try
        {
            using var sp = new ScreenPass(gl);
            var cfg = Off();
            cfg.Curvature = 0.1f;
            cfg.Bezel = "thin";
            cfg.BezelColor = "black";
            cfg.BezelSize = 0.15f;
            var px = ReadN(gl, sp.Apply(white, N, N, cfg));
            float mid = ScanTopWhiteY(px, N / 2)[0];
            float nearCorner = ScanTopWhiteY(px, 40)[0];
            Assert.True(mid - nearCorner >= 3f, $"середина верхнего края {mid}, у угла {nearCorner}");
        }
        finally { gl.DeleteTexture(white); }
    }

    [Fact]
    public void Bezel_TvFascia_TopLighterThanBottomOnBlack()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        uint black = MakeN(gl, 0f);
        try
        {
            using var sp = new ScreenPass(gl);
            var cfg = Off();
            cfg.Bezel = "tv";
            cfg.BezelColor = "beige";
            cfg.BezelSize = 0.1f;   // b ≈ 51 px, фаска ≈ 23 px
            var px = ReadN(gl, sp.Apply(black, N, N, cfg));
            // Пиксели на расстоянии ≈10 px от проёма (за пределами 5-пиксельной тени): верхний клин и нижний
            float top = RN(px, 256, 471), bottom = RN(px, 256, 41);
            // Клинья 1.1 (верх) и 0.45 (низ) при подкраске фосфором: верх вдвое светлее низа
            Assert.True(top > 2f * bottom, $"фаска сверху {top}, снизу {bottom}");
        }
        finally { gl.DeleteTexture(black); }
    }

    [Fact]
    public void Glare_BroadTint_CenterAbout006()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        uint black = MakeN(gl, 0f);
        try
        {
            using var sp = new ScreenPass(gl);
            var cfg = Off();
            cfg.Glare = 1f;
            var px = ReadN(gl, sp.Apply(black, N, N, cfg));
            // Отлив в центре стекла (чуть выше середины): ≈0.06 при glare 1
            float c = RN(px, 256, 317);
            Assert.InRange(c, 0.04f, 0.07f);
        }
        finally { gl.DeleteTexture(black); }
    }

    [Fact]
    public void Thin_BlackPlasticFrame_WhiteCenter_GlowOnFasciaOnlyWhenEnabled()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        uint white = MakeTex(gl, Flat(1f));
        uint black = MakeTex(gl, Flat(0f));
        try
        {
            using var sp = new ScreenPass(gl);
            var cfg = Off();
            cfg.Bezel = "thin";
            cfg.BezelColor = "black";
            cfg.BezelSize = 0.15f;   // thin: b = 0.15 * 128 * 0.4 ≈ 7.7 px
            cfg.BezelGlow = 0.5f;

            var pxWhite = ReadAll(gl, sp.Apply(white, W, H, cfg));
            Assert.True(R(pxWhite, 0, H / 2) < 0.15f, $"рамка у края {R(pxWhite, 0, H / 2)}");
            Assert.True(R(pxWhite, W / 2, H / 2) > 0.9f, $"центр {R(pxWhite, W / 2, H / 2)}");

            // Пиксель фаски: в рамке, у самого окна экрана (x = 6, окно с x = 7.7)
            float fasciaWhite = R(pxWhite, 6, H / 2);
            var pxBlack = ReadAll(gl, sp.Apply(black, W, H, cfg));
            float fasciaBlack = R(pxBlack, 6, H / 2);
            Assert.True(fasciaWhite > fasciaBlack + 0.05f, $"отсвет: белый {fasciaWhite}, чёрный {fasciaBlack}");

            // bezelGlow = 0 — отсвета нет, разницы нет
            cfg.BezelGlow = 0f;
            float noGlowWhite = R(ReadAll(gl, sp.Apply(white, W, H, cfg)), 6, H / 2);
            float noGlowBlack = R(ReadAll(gl, sp.Apply(black, W, H, cfg)), 6, H / 2);
            Assert.InRange(noGlowWhite - noGlowBlack, -0.005f, 0.005f);
        }
        finally
        {
            gl.DeleteTexture(white);
            gl.DeleteTexture(black);
        }
    }

    // Блик проверяется на 512×512: полоса рамы окна ≈3 px, граница отражения различима.
    private const int N = 512;

    private static uint MakeN(GL gl, float v)
    {
        var px = new float[N * N * 4];
        for (int i = 0; i < N * N; i++) { px[i * 4] = v; px[i * 4 + 1] = v; px[i * 4 + 2] = v; px[i * 4 + 3] = 1f; }
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)N, (uint)N, 0, PixelFormat.Rgba, PixelType.Float, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        fixed (float* p = px)
            gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)N, (uint)N, PixelFormat.Rgba, PixelType.Float, p);
        return tex;
    }

    private static float[] ReadN(GL gl, uint tex)
    {
        uint fbo = gl.GenFramebuffer();
        try
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
            if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete) throw new InvalidOperationException("FBO неполный");
            var px = new float[N * N * 4];
            gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
            fixed (float* p = px)
                gl.ReadPixels(0, 0, (uint)N, (uint)N, PixelFormat.Rgba, PixelType.Float, p);
            return px;
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(fbo);
        }
    }

    private static float RN(float[] px, int x, int y) => px[(y * N + x) * 4];

    [Fact]
    public void Glare_WhiteInput_BarelyChanged()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        uint white = MakeN(gl, 1f);
        try
        {
            using var sp = new ScreenPass(gl);
            var cfg = Off();
            var noGlare = ReadN(gl, sp.Apply(white, N, N, cfg));
            cfg.Glare = 1f;
            var glare = ReadN(gl, sp.Apply(white, N, N, cfg));
            // Экранное смешивание: на белом входе отлив почти не меняет яркость
            foreach (var (x, y) in new[] { (123, 430), (69, 430), (200, 200), (20, 20) })
                Assert.InRange(RN(glare, x, y) - RN(noGlare, x, y), -0.02f, 0.02f);
        }
        finally { gl.DeleteTexture(white); }
    }

    [Fact]
    public void Glare_IsStatic_TwoApplyGiveSameResult()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        uint black = MakeTex(gl, Flat(0f));
        try
        {
            using var sp = new ScreenPass(gl);
            var cfg = Off();
            cfg.Glare = 0.8f;
            cfg.Bezel = "tv";
            cfg.BezelColor = "beige";
            cfg.BezelSize = 0.08f;
            var first = ReadAll(gl, sp.Apply(black, W, H, cfg));
            var second = ReadAll(gl, sp.Apply(black, W, H, cfg));
            for (int i = 0; i < first.Length; i++)
                Assert.Equal(first[i], second[i], 6);
        }
        finally { gl.DeleteTexture(black); }
    }
}
