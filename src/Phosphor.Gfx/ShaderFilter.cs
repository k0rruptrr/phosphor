using Phosphor.Core;
using Silk.NET.OpenGL;

namespace Phosphor.Gfx;

/// <summary>
/// База для фильтра из одного фрагментного шейдера. Шейдер — embedded resource в сборке наследника.
///
/// Uniform'ы, которые база выставляет сама (объявляй в шейдере только нужные, неиспользуемые можно не объявлять):
///   uniform sampler2D uInput;      // входная картинка, юнит 0
///   uniform vec2  uResolution;     // размер в пикселях
///   uniform float uTime;           // секунды
///   uniform float uDt;             // секунды с прошлого кадра
///   uniform int   uFrame;          // номер кадра
///   uniform float p_&lt;имя&gt;;        // каждый ParamSpec, например p_curvature
/// Вход: in vec2 vUv; (0,0) — левый низ. Выход: out vec4 fragColor;
/// Шейдер: #version 330 core.
/// </summary>
public abstract class ShaderFilter : IFilter
{
    protected GL Gl = null!;
    protected uint Program;
    protected int Width, Height;
    private readonly Dictionary<string, int> _locs = new();

    public abstract string Id { get; }
    public abstract IReadOnlyList<ParamSpec> Params { get; }

    /// <summary>LogicalName ресурса фрагментного шейдера, например "Phosphor.Filters.Shaders.crt.frag".</summary>
    protected abstract string FragmentResource { get; }

    public virtual void Init(GL gl, int width, int height)
    {
        Gl = gl; Width = width; Height = height;
        string frag = GlUtil.LoadResource(GetType().Assembly, FragmentResource);
        Program = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex, frag, Id);
    }

    public virtual void Resize(int width, int height) { Width = width; Height = height; }

    public virtual void Apply(uint inputTex, RenderTarget output, in FrameInfo frame, FilterParams p)
    {
        output.Bind();
        Gl.Disable(EnableCap.Blend);
        Gl.UseProgram(Program);
        Gl.ActiveTexture(TextureUnit.Texture0);
        Gl.BindTexture(TextureTarget.Texture2D, inputTex);
        SetInt("uInput", 0);
        SetVec2("uResolution", output.Width, output.Height);
        SetFloat("uTime", frame.Time);
        SetFloat("uDt", frame.Dt);
        SetInt("uFrame", (int)frame.Frame);
        foreach (var kv in p.All) SetFloat("p_" + kv.Key, kv.Value);
        BeforeDraw(frame, p);
        GlUtil.DrawFullscreen(Gl);
        AfterDraw(output, frame);
        Gl.ActiveTexture(TextureUnit.Texture0);
    }

    /// <summary>Хук: выставить свои uniform'ы/текстуры (юниты 1+) перед отрисовкой.</summary>
    protected virtual void BeforeDraw(in FrameInfo frame, FilterParams p) { }

    /// <summary>Хук: после отрисовки (например, скопировать output в историю кадров).</summary>
    protected virtual void AfterDraw(RenderTarget output, in FrameInfo frame) { }

    protected int Loc(string name)
    {
        if (!_locs.TryGetValue(name, out var l)) _locs[name] = l = Gl.GetUniformLocation(Program, name);
        return l;
    }
    protected void SetFloat(string n, float v) { int l = Loc(n); if (l >= 0) Gl.Uniform1(l, v); }
    protected void SetInt(string n, int v) { int l = Loc(n); if (l >= 0) Gl.Uniform1(l, v); }
    protected void SetVec2(string n, float x, float y) { int l = Loc(n); if (l >= 0) Gl.Uniform2(l, x, y); }

    public virtual void Dispose()
    {
        if (Program != 0) Gl.DeleteProgram(Program);
        Program = 0;
    }
}
