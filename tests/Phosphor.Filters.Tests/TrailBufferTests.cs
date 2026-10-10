using Phosphor.Gfx;
using Phosphor.Platform;
using Silk.NET.OpenGL;

namespace Phosphor.Filters.Tests;

/// <summary>Шлейф TrailBuffer на настоящем GPU: out = mix(current, prevOut, trail).</summary>
public sealed unsafe class TrailBufferTests
{
    private const int W = 16, H = 16;

    /// <summary>Однотонная текстура (rgb = v, a = 1).</summary>
    private static uint MakeTex(GL gl, float v)
    {
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)W, (uint)H, 0, PixelFormat.Rgba, PixelType.Float, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        var px = new float[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            px[i * 4] = v; px[i * 4 + 1] = v; px[i * 4 + 2] = v; px[i * 4 + 3] = 1f;
        }
        fixed (float* p = px)
            gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)W, (uint)H, PixelFormat.Rgba, PixelType.Float, p);
        return tex;
    }

    /// <summary>Средний красный канал текстуры (0..255), читается через временный FBO.</summary>
    private static float Mean(GL gl, uint tex)
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

            double sum = 0;
            for (int i = 0; i < W * H; i++) sum += px[i * 4];
            return (float)(sum / (W * H) * 255.0);
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(fbo);
        }
    }

    [Fact]
    public void Trail05_DecaysWhiteFrameThroughBlackFrames()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        uint white = MakeTex(gl, 1f);
        uint black = MakeTex(gl, 0f);
        try
        {
            using var tb = new TrailBuffer(gl);
            tb.Resize(W, H);
            float a = Mean(gl, tb.Apply(white, 0.5f));  // первый кадр: prev = current
            float b = Mean(gl, tb.Apply(black, 0.5f));  // 0.5 * 1
            float c = Mean(gl, tb.Apply(black, 0.5f));  // 0.5 * 0.5
            Assert.InRange(a, 252f, 255.5f);
            Assert.InRange(b, 125f, 131f);
            Assert.InRange(c, 61f, 67f);
        }
        finally
        {
            gl.DeleteTexture(white);
            gl.DeleteTexture(black);
        }
    }

    [Fact]
    public void Trail0_ReturnsInputTextureAndNoTrail()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        uint white = MakeTex(gl, 1f);
        uint black = MakeTex(gl, 0f);
        try
        {
            using var tb = new TrailBuffer(gl);
            tb.Resize(W, H);
            Assert.Equal(white, tb.Apply(white, 0f));
            uint outTex = tb.Apply(black, 0f);
            Assert.Equal(black, outTex);
            Assert.InRange(Mean(gl, outTex), 0f, 0.5f);
        }
        finally
        {
            gl.DeleteTexture(white);
            gl.DeleteTexture(black);
        }
    }
}
