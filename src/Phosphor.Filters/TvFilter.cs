using Phosphor.Core;
using Phosphor.Gfx;
using Silk.NET.OpenGL;

namespace Phosphor.Filters;

/// <summary>
/// Телевизионная цветность через композитный сигнал. Проход 1 (tv_encode.frag) кодирует строку в сигнал
/// (поднесущая, частота в циклах на активную строку 52 мкс) в собственный RenderTarget. Проход 2 (tv_decode.frag)
/// декодирует сигнал тем же способом, что телевизор: фильтр нижних частот для яркости, демодуляция цветности
/// и смешивание с исходником по amount. Артефакты (муар, ползущие точки, линия задержки PAL, факелы SECAM) возникают из модели.
/// </summary>
public sealed class TvFilter : ShaderFilter
{
    private static readonly IReadOnlyList<ParamSpec> _params = new ParamSpec[]
    {
        new ParamSpec("mode", 0f, 0f, 2f, new[] { "pal", "secam", "ntsc" }, "Система цветности"),
        new ParamSpec("amount", 1f, 0f, 1f, null, "Общая сила артефактов, 0 = почти без изменений"),
        new ParamSpec("hue", 0f, -30f, 30f, null, "Оттенок в градусах (ручка Tint для NTSC)"),
        new ParamSpec("sharpness", 0.3f, 0f, 1f, null, "Звон и перерегулирование яркости на краях"),
        new ParamSpec("saturation", 0.6f, 0f, 1.5f, null, "SECAM: насыщенность декодированного цвета (пастель)"),
        new ParamSpec("fires", 0.5f, 0f, 1f, null, "SECAM: количество факелов (красно-розовых и бирюзовых штрихов)"),
        new ParamSpec("signal", 1f, 0f, 1f, null, "SECAM: качество приёма, 1 = идеально, ниже = снег и факелы от шума"),
        new ParamSpec("edge", 0.35f, 0f, 1f, null, "Сигналы опознавания (бутылки) сверху кадра"),
    };

    private const string EncodeResource = "Phosphor.Filters.Shaders.tv_encode.frag";

    private uint _encProg;
    private int _encInputLoc = -1;
    private int _encResLoc = -1;
    private int _encFrameLoc = -1;
    private int _encModeLoc = -1;

    private RenderTarget? _signal;
    private uint _srcTex;

    public override string Id => "tv";

    protected override string FragmentResource => "Phosphor.Filters.Shaders.tv_decode.frag";

    public override IReadOnlyList<ParamSpec> Params => _params;

    public override void Init(GL gl, int width, int height)
    {
        base.Init(gl, width, height);

        _encProg = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex,
            GlUtil.LoadResource(GetType().Assembly, EncodeResource), Id + ".encode");
        _encInputLoc = gl.GetUniformLocation(_encProg, "uInput");
        _encResLoc = gl.GetUniformLocation(_encProg, "uResolution");
        _encFrameLoc = gl.GetUniformLocation(_encProg, "uFrame");
        _encModeLoc = gl.GetUniformLocation(_encProg, "p_mode");

        _signal = new RenderTarget(gl, width, height);
    }

    public override void Resize(int width, int height)
    {
        base.Resize(width, height);
        _signal?.Resize(width, height);
    }

    public override void Apply(uint inputTex, RenderTarget output, in FrameInfo frame, FilterParams p)
    {
        var signal = _signal!;

        // Проход 1: кодирование в композит.
        Gl.Disable(EnableCap.Blend);
        signal.Bind();
        Gl.UseProgram(_encProg);
        Gl.ActiveTexture(TextureUnit.Texture0);
        Gl.BindTexture(TextureTarget.Texture2D, inputTex);
        Gl.Uniform1(_encInputLoc, 0);
        Gl.Uniform2(_encResLoc, (float)signal.Width, (float)signal.Height);
        Gl.Uniform1(_encFrameLoc, (int)frame.Frame);
        Gl.Uniform1(_encModeLoc, p["mode"]);
        GlUtil.DrawFullscreen(Gl);

        // Проход 2: декодирование сигнала; исходник нужен для смешивания по amount.
        _srcTex = inputTex;
        base.Apply(signal.Texture, output, frame, p);
    }

    protected override void BeforeDraw(in FrameInfo frame, FilterParams p)
    {
        Gl.ActiveTexture(TextureUnit.Texture1);
        Gl.BindTexture(TextureTarget.Texture2D, _srcTex);
        SetInt("uSrc", 1);
        Gl.ActiveTexture(TextureUnit.Texture0);
    }

    public override void Dispose()
    {
        _signal?.Dispose();
        _signal = null;
        if (_encProg != 0) Gl.DeleteProgram(_encProg);
        _encProg = 0;
        base.Dispose();
    }
}
