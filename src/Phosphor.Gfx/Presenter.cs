using Silk.NET.OpenGL;

namespace Phosphor.Gfx;

/// <summary>Вывод готовой текстуры в прямоугольник текущего (обычно экранного) фреймбуфера.</summary>
public sealed class Presenter : IDisposable
{
    private readonly GL _gl;
    private readonly uint _prog;
    private readonly int _locTex;

    private const string Frag = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColor;
        uniform sampler2D uTex;
        void main() { fragColor = vec4(clamp(texture(uTex, vUv).rgb, 0.0, 1.0), 1.0); }
        """;

    public Presenter(GL gl)
    {
        _gl = gl;
        _prog = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex, Frag, "present");
        _locTex = gl.GetUniformLocation(_prog, "uTex");
    }

    /// <summary>Нарисовать tex в прямоугольник (x,y,w,h) фреймбуфера fbo (0 — окно). x,y — от левого низа.</summary>
    public void Draw(uint tex, uint fbo, int x, int y, int w, int h)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.Viewport(x, y, (uint)w, (uint)h);
        _gl.Disable(EnableCap.Blend);
        _gl.UseProgram(_prog);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.Uniform1(_locTex, 0);
        GlUtil.DrawFullscreen(_gl);
    }

    public void Dispose() => _gl.DeleteProgram(_prog);
}
