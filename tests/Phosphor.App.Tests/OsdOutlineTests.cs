using Phosphor.Gfx;
using Phosphor.App;
using Phosphor.Platform;
using Silk.NET.OpenGL;

namespace Phosphor.App.Tests;

/// <summary>Обводка OSD на настоящем GPU: белый глиф-точка → чёрный контур радиуса 2 px, дальше — прозрачно.</summary>
public sealed unsafe class OsdOutlineTests
{
    private const int N = 32;

    [Fact]
    public void Stroke_is_black_within_2px_and_transparent_beyond()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var gl = win.Gl;
        using var src = new RenderTarget(gl, N, N);
        using var dst = new RenderTarget(gl, N, N);
        using var pass = new OsdOutline(gl);

        // Premultiplied: один белый пиксель-глиф в центре, остальное прозрачно
        var data = new float[N * N * 4];
        int c = (16 * N + 16) * 4;
        data[c] = 1f; data[c + 1] = 1f; data[c + 2] = 1f; data[c + 3] = 1f;
        gl.BindTexture(TextureTarget.Texture2D, src.Texture);
        fixed (float* p = data)
            gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, N, N, PixelFormat.Rgba, PixelType.Float, p);

        pass.Stroke(src.Texture, dst, 2f);
        var px = ReadRgba(gl, dst);

        // Глиф: белый, непрозрачный
        Assert.True(px[16, 16, 3] > 0.99f);
        Assert.True(px[16, 16, 0] > 0.99f);

        // Вокруг в радиусе 2 px: альфа > 0, цвет ≈ чёрный (премножен нулём)
        foreach (var (x, y) in new[] { (18, 16), (14, 16), (16, 18), (16, 14), (17, 17) })
        {
            Assert.True(px[x, y, 3] > 0.99f, $"обводка пропала в ({x},{y})");
            Assert.True(px[x, y, 0] < 0.01f && px[x, y, 1] < 0.01f && px[x, y, 2] < 0.01f, $"цвет не чёрный в ({x},{y})");
        }

        // Дальше радиуса — альфа 0
        foreach (var (x, y) in new[] { (19, 16), (16, 19), (13, 16), (18, 18) })
            Assert.True(px[x, y, 3] < 0.001f, $"обводка вышла за радиус в ({x},{y})");
    }

    /// <summary>Читает RGBA (float) из RenderTarget. Индекс [x, y, канал], y — как в GL (от низа); для симметричной точки не важно.</summary>
    private static float[,,] ReadRgba(GL gl, RenderTarget rt)
    {
        var buf = new float[rt.Width * rt.Height * 4];
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, rt.Fbo);
        gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
        fixed (float* p = buf)
            gl.ReadPixels(0, 0, (uint)rt.Width, (uint)rt.Height, PixelFormat.Rgba, PixelType.Float, p);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        var r = new float[rt.Width, rt.Height, 4];
        for (int y = 0; y < rt.Height; y++)
            for (int x = 0; x < rt.Width; x++)
                for (int k = 0; k < 4; k++)
                    r[x, y, k] = buf[(y * rt.Width + x) * 4 + k];
        return r;
    }
}
