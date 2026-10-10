using Phosphor.Core;
using Phosphor.Gfx;
using Phosphor.Platform;
using Silk.NET.OpenGL;

namespace Phosphor.Filters.Tests;

/// <summary>ScreenPass: маска кинескопа (shadow / trinitron / diagonal) и чересстрочность на настоящем GPU.</summary>
public sealed unsafe class ScreenMaskInterlaceTests
{
    private const int N = 512;

    /// <summary>Текстура N×N: серый уровень f(x, y), rgb = v, a = 1.</summary>
    private static uint Make(GL gl, Func<int, int, float> f)
    {
        var px = new float[N * N * 4];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                int i = (y * N + x) * 4;
                float v = f(x, y);
                px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 1f;
            }
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)N, (uint)N, 0, PixelFormat.Rgba, PixelType.Float, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        fixed (float* p = px)
            gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)N, (uint)N, PixelFormat.Rgba, PixelType.Float, p);
        return tex;
    }

    /// <summary>Все пиксели текстуры N×N (rgba float), GL-порядок: y снизу вверх.</summary>
    private static float[] Read(GL gl, uint tex)
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

    private static float At(float[] px, int x, int y, int c = 0) => px[(y * N + x) * 4 + c];

    /// <summary>Среднее по строке канала c.</summary>
    private static float RowMean(float[] px, int y, int c = 0)
    {
        float s = 0f;
        for (int x = 0; x < N; x++) s += At(px, x, y, c);
        return s / N;
    }

    /// <summary>Конфиг, где всё геометрическое, блик, рамка, маска и чересстрочность выключены.</summary>
    private static ScreenConfig Off() => new()
    {
        Trail = 0f, Curvature = 0f, CornerRadius = 0f, Vignette = 0f, Overscan = 0f,
        Glare = 0f, Bezel = "none", BezelColor = "black", BezelSize = 0.06f, BezelGlow = 0.5f,
        Mask = 0f, MaskType = "shadow", Interlace = 0f,
    };

    [Fact]
    public void MaskAlone_IsNotOff_ReturnsNewTexture()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        uint white = Make(gl, (x, y) => 1f);
        try
        {
            using var sp = new ScreenPass(gl);
            var cfg = Off();
            cfg.Mask = 0.5f;
            Assert.NotEqual(white, sp.Apply(white, N, N, cfg));
        }
        finally { gl.DeleteTexture(white); }
    }

    [Fact]
    public void Shadow_Mask1_TriadPeriod3px_AverageKept()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        uint white = Make(gl, (x, y) => 1f);
        try
        {
            using var sp = new ScreenPass(gl);
            var cfg = Off();
            cfg.Mask = 1f;
            cfg.MaskType = "shadow";
            var px = Read(gl, sp.Apply(white, N, N, cfg));
            int y = N / 2;
            for (int x = 30; x < 60; x++)
                Assert.InRange(At(px, x, y) - At(px, x + 3, y), -1e-3f, 1e-3f);
            // Столбец x % 3 == 0 — красный лидер: R заметно ярче G
            Assert.True(At(px, 30, y, 0) > At(px, 30, y, 1) + 0.3f, $"R {At(px, 30, y, 0)}, G {At(px, 30, y, 1)}");
            // Среднее по 3 px ≈ входу (белый = 1) для каждого канала
            for (int c = 0; c < 3; c++)
            {
                float avg = (At(px, 33, y, c) + At(px, 34, y, c) + At(px, 35, y, c)) / 3f;
                Assert.InRange(avg, 0.97f, 1.03f);
            }
        }
        finally { gl.DeleteTexture(white); }
    }

    [Fact]
    public void Trinitron_Mask1_TwoDarkThreadsAtThirds_RestUniform()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        uint white = Make(gl, (x, y) => 1f);
        try
        {
            using var sp = new ScreenPass(gl);
            var cfg = Off();
            cfg.Mask = 1f;
            cfg.MaskType = "trinitron";
            var px = Read(gl, sp.Apply(white, N, N, cfg));
            int t1 = N / 3, t2 = 2 * N / 3;
            // Нити на 1/3 и 2/3 высоты: темнее соседних строк минимум на 20%
            Assert.True(RowMean(px, t1) <= 0.8f * RowMean(px, t1 - 10), $"нить {RowMean(px, t1)}, рядом {RowMean(px, t1 - 10)}");
            Assert.True(RowMean(px, t2) <= 0.8f * RowMean(px, t2 - 10), $"нить {RowMean(px, t2)}, рядом {RowMean(px, t2 - 10)}");
            // В остальном по вертикали однородно
            Assert.InRange(RowMean(px, 20) - RowMean(px, 100), -1e-3f, 1e-3f);
            Assert.InRange(RowMean(px, 250) - RowMean(px, 100), -1e-3f, 1e-3f);
        }
        finally { gl.DeleteTexture(white); }
    }

    /// <summary>Пиксели текстуры w×h (rgba float), GL-порядок.</summary>
    private static float[] ReadWH(GL gl, uint tex, int w, int h)
    {
        uint fbo = gl.GenFramebuffer();
        try
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
            if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete) throw new InvalidOperationException("FBO неполный");
            var px = new float[w * h * 4];
            gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
            fixed (float* p = px)
                gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.Float, p);
            return px;
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(fbo);
        }
    }

    /// <summary>Среднее R по строке y выхода шириной w.</summary>
    private static float RowMeanWH(float[] px, int w, int y)
    {
        float s = 0f;
        for (int x = 0; x < w; x++) s += px[(y * w + x) * 4];
        return s / w;
    }

    [Fact]
    public void Trinitron_At1080_ThreadsOnExactRows360And720()
    {
        // 1080 — 1/3 и 2/3 высоты целые: нити ложатся ровно на строки 360 и 720 (GL, снизу вверх).
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        uint white = Make(gl, (x, y) => 1f);
        try
        {
            using var sp = new ScreenPass(gl);
            var cfg = Off();
            cfg.Mask = 1f;
            cfg.MaskType = "trinitron";
            const int w = N, h = 1080;
            var px = ReadWH(gl, sp.Apply(white, w, h, cfg), w, h);
            // Нити — физические: затемнение фиксированное 30% независимо от силы маски (белый вход: ratio 0.70)
            float r360 = RowMeanWH(px, w, 360) / RowMeanWH(px, w, 350);
            float r720 = RowMeanWH(px, w, 720) / RowMeanWH(px, w, 710);
            Assert.InRange(r360, 0.69f, 0.71f);
            Assert.InRange(r720, 0.69f, 0.71f);
            // Соседние строки нитей не трогают
            Assert.InRange(RowMeanWH(px, w, 361) - RowMeanWH(px, w, 350), -1e-3f, 1e-3f);
            Assert.InRange(RowMeanWH(px, w, 721) - RowMeanWH(px, w, 710), -1e-3f, 1e-3f);
        }
        finally { gl.DeleteTexture(white); }
    }

    [Fact]
    public void Diagonal_LeaderShiftsAlongAntiDiagonal()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        uint white = Make(gl, (x, y) => 1f);
        try
        {
            using var sp = new ScreenPass(gl);
            var cfg = Off();
            cfg.Mask = 1f;
            cfg.MaskType = "diagonal";
            var px = Read(gl, sp.Apply(white, N, N, cfg));
            // (x + y) mod 3 == 0 — красный лидер в (10, 11)
            Assert.True(At(px, 10, 11, 0) > At(px, 10, 11, 1) + 0.1f, $"R {At(px, 10, 11, 0)}, G {At(px, 10, 11, 1)}");
            // Тот же лидер на диагонали (x + 1, y − 1): каналы совпадают
            Assert.InRange(At(px, 11, 10, 0) - At(px, 10, 11, 0), -1e-4f, 1e-4f);
            Assert.InRange(At(px, 11, 10, 1) - At(px, 10, 11, 1), -1e-4f, 1e-4f);
            // Соседний столбец в той же строке — лидер зелёный: (11, 11), (x + y) mod 3 == 1
            Assert.True(At(px, 11, 11, 1) > At(px, 11, 11, 0) + 0.1f, $"G {At(px, 11, 11, 1)}, R {At(px, 11, 11, 0)}");
            // Среднее по 3 px сохраняется
            for (int c = 0; c < 3; c++)
            {
                float avg = (At(px, 30, 50, c) + At(px, 31, 50, c) + At(px, 32, 50, c)) / 3f;
                Assert.InRange(avg, 0.97f, 1.03f);
            }
        }
        finally { gl.DeleteTexture(white); }
    }

    [Fact]
    public void Interlace_MovingStrip_CombOnOneField_TraceOnOther()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        uint a = Make(gl, (x, y) => x == 100 ? 1f : 0f);
        uint b = Make(gl, (x, y) => x == 200 ? 1f : 0f);
        try
        {
            using var sp = new ScreenPass(gl);
            var cfg = Off();
            cfg.Interlace = 1f;
            sp.Apply(a, N, N, cfg);
            var px = Read(gl, sp.Apply(b, N, N, cfg));
            int current = 0, other = 0, ambiguous = 0;
            for (int y = 0; y < N; y++)
            {
                float at200 = At(px, 200, y), at100 = At(px, 100, y);
                if (at200 > 0.9f && at100 < 0.05f) current++;        // строки текущего поля: только новая полоса
                else if (at200 < 0.05f && at100 > 0.8f) other++;     // строки другого поля: след старой полосы
                else ambiguous++;
            }
            Assert.Equal(0, ambiguous);
            Assert.True(current > N / 4, $"строк текущего поля {current}");
            Assert.True(other > N / 4, $"строк другого поля {other}");
        }
        finally
        {
            gl.DeleteTexture(a);
            gl.DeleteTexture(b);
        }
    }

    [Fact]
    public void Interlace_OffThenOn_NoStaleTrace()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        uint a = Make(gl, (x, y) => x == 100 ? 1f : 0f);
        uint b = Make(gl, (x, y) => x == 200 ? 1f : 0f);
        try
        {
            using var sp = new ScreenPass(gl);
            sp.Apply(a, N, N, Off());                  // чересстрочность выключена: прошлого кадра нет
            var on = Off();
            on.Interlace = 1f;
            var px = Read(gl, sp.Apply(b, N, N, on));  // первый кадр после включения: следа A быть не должно
            for (int y = 0; y < N; y++)
                Assert.True(At(px, 100, y) < 0.05f, $"след A в строке {y}: {At(px, 100, y)}");
        }
        finally
        {
            gl.DeleteTexture(a);
            gl.DeleteTexture(b);
        }
    }

    [Fact]
    public void Interlace_StaticPicture_OutputMatchesInputWithinFlicker()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        static float Value(int x, int y) => ((x * 7 + y * 3) % 256) / 255f;
        uint src = Make(gl, Value);
        try
        {
            using var sp = new ScreenPass(gl);
            var cfg = Off();
            cfg.Interlace = 1f;
            sp.Apply(src, N, N, cfg);
            var px = Read(gl, sp.Apply(src, N, N, cfg));
            // Мерцание поля: строки «старого» поля ×(1 − 0.12) — отклонение не больше 0.13
            float worst = 0f;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                    worst = Math.Max(worst, Math.Abs(At(px, x, y) - Value(x, y)));
            Assert.True(worst <= 0.13f, $"наибольшее отклонение {worst}");
        }
        finally { gl.DeleteTexture(src); }
    }
}
