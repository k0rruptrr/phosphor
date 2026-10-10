using Phosphor.Core;
using Phosphor.Gfx;
using Phosphor.Overlays;
using Phosphor.Text;
using Silk.NET.OpenGL;

namespace Phosphor.App;

/// <summary>
/// Обводка и композит OSD. Вход — premultiplied глифы (rgb = Fg·покрытие, a = покрытие).
/// Stroke: альфа = максимум по диску радиуса r (px выхода), цвет — чёрный (премножен нулём) → чёрный контур под буквами.
/// Blend: премножённый результат ложится поверх кадра альфа-смешиванием (One, OneMinusSrcAlpha).
/// </summary>
public sealed class OsdOutline : IDisposable
{
    private const string StrokeFrag = """
        #version 330 core
        out vec4 fragColor;
        uniform sampler2D uSrc;   // premultiplied глифы
        uniform float uRadius;    // радиус обводки, px текстуры
        uniform int uMaxR;        // ceil(uRadius)

        void main() {
            ivec2 p = ivec2(gl_FragCoord.xy);
            vec4 c = texelFetch(uSrc, p, 0);
            float a = c.a;
            float r2 = uRadius * uRadius + 0.25;
            for (int dy = -uMaxR; dy <= uMaxR; dy++)
                for (int dx = -uMaxR; dx <= uMaxR; dx++) {
                    if (float(dx * dx + dy * dy) > r2) continue;
                    a = max(a, texelFetch(uSrc, p + ivec2(dx, dy), 0).a);
                }
            fragColor = vec4(c.rgb, a);
        }
        """;

    private const string BlendFrag = """
        #version 330 core
        out vec4 fragColor;
        in vec2 vUv;
        uniform sampler2D uSrc;
        void main() {
            fragColor = texture(uSrc, vUv);
        }
        """;

    private readonly GL _gl;
    private readonly uint _strokeProg, _blendProg;
    private readonly int _locRadius, _locMaxR;

    public OsdOutline(GL gl)
    {
        _gl = gl;
        _strokeProg = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex, StrokeFrag, "osd-stroke");
        _blendProg = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex, BlendFrag, "osd-blend");
        _locRadius = gl.GetUniformLocation(_strokeProg, "uRadius");
        _locMaxR = gl.GetUniformLocation(_strokeProg, "uMaxR");
    }

    /// <summary>Контур: из src (глифы того же размера, что dst) в dst — глифы поверх чёрной обводки радиуса radiusPx.</summary>
    public void Stroke(uint src, RenderTarget dst, float radiusPx)
    {
        dst.Bind();
        _gl.Disable(EnableCap.Blend);
        _gl.UseProgram(_strokeProg);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, src);
        _gl.Uniform1(_locRadius, radiusPx);
        _gl.Uniform1(_locMaxR, (int)MathF.Ceiling(radiusPx));
        GlUtil.DrawFullscreen(_gl);
    }

    /// <summary>Наложить premultiplied src на dst (dst — текущий кадр сцены), растянув src на весь dst.</summary>
    public void Blend(uint src, RenderTarget dst)
    {
        dst.Bind();
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        _gl.UseProgram(_blendProg);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, src);
        GlUtil.DrawFullscreen(_gl);
        _gl.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        if (_strokeProg != 0) _gl.DeleteProgram(_strokeProg);
        if (_blendProg != 0) _gl.DeleteProgram(_blendProg);
    }
}

/// <summary>
/// OSD видика одного монитора: своя сетка OsdGeometry.Rows строк (буквы одного размера на всех сценах),
/// прозрачный рендер глифов, контур шейдером, альфа-композит поверх выхода сцены — до фильтров, чтобы VHS рвал и OSD.
/// Если видик ничего не показывает в этом кадре — слой не рисуется вовсе.
/// </summary>
internal sealed class OsdLayer : IDisposable
{
    private readonly CharGrid _grid;
    private readonly GridRenderer _renderer;    // свой экземпляр: размер сетки OSD отличается от сцен
    private readonly RenderTarget _glyphs;
    private readonly RenderTarget _outline;
    private readonly OsdOutline _pass;

    public int Cols => _grid.Cols;
    public int Rows => _grid.Rows;

    public OsdLayer(Shared s, int vpW, int vpH)
    {
        var font = s.FontFor(OsdGeometry.CellHeight(vpH));
        int cw = font.Atlas.CellWidth, ch = font.Atlas.CellHeight;
        int cols = OsdGeometry.Cols(vpW, cw);
        _grid = new CharGrid(cols, OsdGeometry.Rows);
        _renderer = new GridRenderer(s.Gl, font.Atlas);
        _glyphs = new RenderTarget(s.Gl, cols * cw, OsdGeometry.Rows * ch);
        _outline = new RenderTarget(s.Gl, cols * cw, OsdGeometry.Rows * ch);
        _pass = new OsdOutline(s.Gl);
    }

    /// <summary>Нарисовать OSD видика и наложить на target (выход сцены). Без показа — ничего не делает.</summary>
    public void Compose(RenderTarget target, VcrOverlay vcr, in OverlayContext ctx)
    {
        if (!vcr.IsVisible(ctx.Organism.Fatigue)) return;
        vcr.Apply(_grid, ctx);
        _renderer.RenderGlyphs(_grid, _glyphs);
        // Обводка 2 px на 1080 строк, масштаб по высоте кадра
        _pass.Stroke(_glyphs.Texture, _outline, 2f * target.Height / 1080f);
        _pass.Blend(_outline.Texture, target);
    }

    public void Dispose()
    {
        _pass.Dispose();
        _renderer.Dispose();
        _glyphs.Dispose();
        _outline.Dispose();
    }
}
