using Phosphor.Gfx;
using Phosphor.Platform;
using Silk.NET.OpenGL;

namespace Phosphor.Filters.Tests;

/// <summary>Множитель яркости Presenter.Draw (диммер заставки) на настоящем GPU.</summary>
public sealed unsafe class PresenterGainTests
{
    private const int W = 16, H = 16;

    /// <summary>Белая текстура → Presenter с gain → средний канал выхода (0..255).</summary>
    private static float DrawWhite(float gain)
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;

        uint tex = gl.GenTexture();
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, tex);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)W, (uint)H, 0, PixelFormat.Rgba, PixelType.Float, null);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            var white = new float[W * H * 4];
            Array.Fill(white, 1f);
            fixed (float* p = white)
                gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)W, (uint)H, PixelFormat.Rgba, PixelType.Float, p);

            using var presenter = new Presenter(gl);
            using var rt = new RenderTarget(gl, W, H);
            presenter.Draw(tex, rt.Fbo, 0, 0, W, H, gain);
            gl.Finish();

            var px = new float[W * H * 4];
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, rt.Fbo);
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
            gl.DeleteTexture(tex);
        }
    }

    [Fact]
    public void Gain025_DarkensWhiteTo64()
    {
        float v = DrawWhite(0.25f);
        Assert.InRange(v, 62f, 66f);
    }

    [Fact]
    public void DefaultGain_LeavesWhiteAt255()
    {
        float v = DrawWhite(1f);
        Assert.InRange(v, 254f, 255f);
    }
}
