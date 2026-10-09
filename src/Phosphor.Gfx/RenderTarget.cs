using Silk.NET.OpenGL;

namespace Phosphor.Gfx;

/// <summary>
/// Текстура + FBO. Формат RGBA16F (запас по яркости для bloom/послесвечения), фильтрация Linear, ClampToEdge.
/// Соглашение по координатам во всём проекте: GL-стиль, uv (0,0) — ЛЕВЫЙ НИЗ, (1,1) — правый верх.
/// Верхняя строка сетки символов рисуется у uv.y ≈ 1.
/// </summary>
public sealed unsafe class RenderTarget : IDisposable
{
    private readonly GL _gl;
    public uint Fbo { get; private set; }
    public uint Texture { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public RenderTarget(GL gl, int width, int height)
    {
        _gl = gl;
        Allocate(width, height);
    }

    /// <summary>Сделать целью рендера и выставить viewport на весь размер.</summary>
    public void Bind()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, Fbo);
        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
    }

    public void Clear(float r = 0, float g = 0, float b = 0, float a = 1)
    {
        Bind();
        _gl.ClearColor(r, g, b, a);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
    }

    public void Resize(int width, int height)
    {
        if (width == Width && height == Height) return;
        Free();
        Allocate(width, height);
    }

    private void Allocate(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width; Height = height;

        Texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, Texture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)width, (uint)height, 0,
            PixelFormat.Rgba, PixelType.HalfFloat, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        Fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, Fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, Texture, 0);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete) throw new InvalidOperationException($"FBO неполный: {status}");

        _gl.ClearColor(0, 0, 0, 1);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void Free()
    {
        if (Fbo != 0) _gl.DeleteFramebuffer(Fbo);
        if (Texture != 0) _gl.DeleteTexture(Texture);
        Fbo = 0; Texture = 0;
    }

    public void Dispose() => Free();
}
