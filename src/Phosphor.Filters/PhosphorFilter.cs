using Phosphor.Core;
using Phosphor.Gfx;
using Silk.NET.OpenGL;

namespace Phosphor.Filters;

/// <summary>
/// Монохромный экран с послесвечением: люминофор (green/amber/white), затухающий след от прошлого кадра.
/// История хранится в собственном RenderTarget и копируется из output после каждого прохода.
/// </summary>
public sealed class PhosphorFilter : ShaderFilter
{
    private static readonly IReadOnlyList<ParamSpec> _params = new ParamSpec[]
    {
        new ParamSpec("color", 0f, 0f, 2f, new[] { "green", "amber", "white" }, "Тип люминофора"),
        new ParamSpec("persistence", 0.82f, 0f, 0.98f, null, "Послесвечение: доля яркости прошлого кадра через 1/60 с"),
        new ParamSpec("tint", 1f, 0f, 1f, null, "1 = полностью монохромно, 0 = исходные цвета"),
        new ParamSpec("brightness", 1f, 0f, 3f, null, "Общий множитель яркости"),
    };

    private RenderTarget? _history;

    public override string Id => "phosphor";

    protected override string FragmentResource => "Phosphor.Filters.Shaders.phosphor.frag";

    public override IReadOnlyList<ParamSpec> Params => _params;

    public override void Init(GL gl, int width, int height)
    {
        base.Init(gl, width, height);
        _history = new RenderTarget(gl, width, height);
        _history.Clear(0, 0, 0, 1);
    }

    public override void Resize(int width, int height)
    {
        base.Resize(width, height);
        if (_history is null) return;
        _history.Dispose();
        _history = new RenderTarget(Gl, width, height);
        _history.Clear(0, 0, 0, 1);
    }

    protected override void BeforeDraw(in FrameInfo frame, FilterParams p)
    {
        Gl.ActiveTexture(TextureUnit.Texture1);
        Gl.BindTexture(TextureTarget.Texture2D, _history!.Texture);
        SetInt("uHistory", 1);
        Gl.ActiveTexture(TextureUnit.Texture0);
    }

    protected override void AfterDraw(RenderTarget output, in FrameInfo frame)
    {
        var history = _history!;
        Gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, output.Fbo);
        Gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, history.Fbo);
        Gl.BlitFramebuffer(0, 0, output.Width, output.Height, 0, 0, history.Width, history.Height,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        output.Bind();
    }

    public override void Dispose()
    {
        _history?.Dispose();
        _history = null;
        base.Dispose();
    }
}
