using Phosphor.Core;
using Phosphor.Gfx;
using Silk.NET.OpenGL;

namespace Phosphor.Filters;

/// <summary>
/// Эмуляция ЭЛТ-сигнала: скан-линии, апертурная маска, bloom, мерцание. Геометрия кинескопа — в ScreenPass.
/// Bloom многопроходный: bright-pass с даунсэмплом в половину размера, сепарабельный гаусс H и V,
/// затем финальный проход crt.frag добавляет размытый результат (uBloom).
/// </summary>
public sealed class CrtFilter : ShaderFilter
{
    private static readonly IReadOnlyList<ParamSpec> _params = new ParamSpec[]
    {
        new ParamSpec("scanlines", 0.5f, 0f, 1f, null, "Сила тёмных скан-линий", "строки развёртки"),
        new ParamSpec("scanlinePeriod", 3f, 1f, 8f, null, "Период скан-линий в пикселях выхода", "период строк"),
        new ParamSpec("mask", 0.3f, 0f, 1f, null, "Сила апертурной решётки (RGB-триады по 3 пикселя)", "маска кинескопа"),
        new ParamSpec("bloom", 0.8f, 0f, 3f, null, "Сила свечения вокруг ярких участков", "свечение"),
        new ParamSpec("bloomRadius", 6f, 0.5f, 16f, null, "Радиус свечения в пикселях входа", "радиус свечения"),
        new ParamSpec("flicker", 0.03f, 0f, 0.3f, null, "Амплитуда мерцания яркости", "мерцание"),
        new ParamSpec("brightness", 1.1f, 0f, 3f, null, "Общий множитель яркости", "яркость"),
    };

    private const string BrightResource = "Phosphor.Filters.Shaders.crt_bright.frag";
    private const string BlurResource = "Phosphor.Filters.Shaders.crt_blur.frag";

    private uint _brightProg;
    private uint _blurProg;
    private int _brightInputLoc = -1;
    private int _blurInputLoc = -1;
    private int _blurDirLoc = -1;

    private RenderTarget? _bright;
    private RenderTarget? _blurA;
    private RenderTarget? _blurB;

    public override string Id => "crt";

    protected override string FragmentResource => "Phosphor.Filters.Shaders.crt.frag";

    public override IReadOnlyList<ParamSpec> Params => _params;

    public override void Init(GL gl, int width, int height)
    {
        base.Init(gl, width, height);

        var asm = GetType().Assembly;
        _brightProg = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex,
            GlUtil.LoadResource(asm, BrightResource), Id + ".bright");
        _blurProg = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex,
            GlUtil.LoadResource(asm, BlurResource), Id + ".blur");

        _brightInputLoc = gl.GetUniformLocation(_brightProg, "uInput");
        _blurInputLoc = gl.GetUniformLocation(_blurProg, "uInput");
        _blurDirLoc = gl.GetUniformLocation(_blurProg, "uDir");

        SizeBloomTargets(width, height);
    }

    public override void Resize(int width, int height)
    {
        base.Resize(width, height);
        SizeBloomTargets(width, height);
    }

    private void SizeBloomTargets(int width, int height)
    {
        int hw = Math.Max(1, width / 2);
        int hh = Math.Max(1, height / 2);
        if (_bright is null || _blurA is null || _blurB is null)
        {
            _bright = new RenderTarget(Gl, hw, hh);
            _blurA = new RenderTarget(Gl, hw, hh);
            _blurB = new RenderTarget(Gl, hw, hh);
        }
        else
        {
            _bright.Resize(hw, hh);
            _blurA.Resize(hw, hh);
            _blurB.Resize(hw, hh);
        }
    }

    public override void Apply(uint inputTex, RenderTarget output, in FrameInfo frame, FilterParams p)
    {
        var bright = _bright!;
        var blurA = _blurA!;
        var blurB = _blurB!;
        float halfW = bright.Width;
        float halfH = bright.Height;

        Gl.Disable(EnableCap.Blend);

        // 1. Bright-pass с даунсэмплом в половину размера.
        bright.Bind();
        Gl.UseProgram(_brightProg);
        Gl.ActiveTexture(TextureUnit.Texture0);
        Gl.BindTexture(TextureTarget.Texture2D, inputTex);
        Gl.Uniform1(_brightInputLoc, 0);
        GlUtil.DrawFullscreen(Gl);

        // 2. Гаусс по горизонтали, 9 тапов, шаг = bloomRadius / 4 пикселя половинного разрешения.
        float step = p["bloomRadius"] / 4f;
        blurA.Bind();
        Gl.UseProgram(_blurProg);
        Gl.BindTexture(TextureTarget.Texture2D, bright.Texture);
        Gl.Uniform1(_blurInputLoc, 0);
        Gl.Uniform2(_blurDirLoc, step / halfW, 0f);
        GlUtil.DrawFullscreen(Gl);

        // 3. Гаусс по вертикали.
        blurB.Bind();
        Gl.BindTexture(TextureTarget.Texture2D, blurA.Texture);
        Gl.Uniform2(_blurDirLoc, 0f, step / halfH);
        GlUtil.DrawFullscreen(Gl);

        // 4. Финальный проход crt.frag в output; uBloom подключается в BeforeDraw.
        base.Apply(inputTex, output, frame, p);
    }

    protected override void BeforeDraw(in FrameInfo frame, FilterParams p)
    {
        Gl.ActiveTexture(TextureUnit.Texture1);
        Gl.BindTexture(TextureTarget.Texture2D, _blurB!.Texture);
        SetInt("uBloom", 1);
        Gl.ActiveTexture(TextureUnit.Texture0);
    }

    public override void Dispose()
    {
        _bright?.Dispose();
        _blurA?.Dispose();
        _blurB?.Dispose();
        _bright = null;
        _blurA = null;
        _blurB = null;
        if (_brightProg != 0) Gl.DeleteProgram(_brightProg);
        if (_blurProg != 0) Gl.DeleteProgram(_blurProg);
        _brightProg = 0;
        _blurProg = 0;
        base.Dispose();
    }
}
