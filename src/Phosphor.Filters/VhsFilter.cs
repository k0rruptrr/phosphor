using Phosphor.Core;
using Phosphor.Gfx;
using Silk.NET.OpenGL;

namespace Phosphor.Filters;

/// <summary>
/// Эмуляция зажёванной VHS-кассеты: дрожание строк, полоса трекинга, растекание цветности,
/// шум, сдвиг RGB, шум головок внизу кадра, качка ленты, выпадения сигнала.
/// Полоса цветности (chromaRes < 400) многопроходная, как bloom у crt: даунсэмпл I/Q в RT W/4 × H/4,
/// сепарабельный гаусс по x и по y, основной проход vhs.frag читает размытую цветность (uChroma).
/// </summary>
public sealed class VhsFilter : ShaderFilter
{
    private static readonly IReadOnlyList<ParamSpec> _params = new ParamSpec[]
    {
        new ParamSpec("jitter", 0.25f, 0f, 3f, null, "Дрожание строк: сдвиг по x для групп по 2 строки (0..3 px × jitter)", "дрожание строк"),
        new ParamSpec("tracking", 0.2f, 0f, 1f, null, "Автотрекинг: при > 0 эпизоды полосы помех (1.5–3 с), пауза 40 с (0+) … 6 с (1), сила ∝ значению", "трекинг"),
        new ParamSpec("bleed", 0.5f, 0f, 6f, null, "Растекание цветности вправо (~10 px × bleed)", "растекание цвета"),
        new ParamSpec("noise", 0.12f, 0f, 1f, null, "Зерно/снег, меняется каждый кадр", "снег"),
        new ParamSpec("rgbShift", 1.0f, 0f, 8f, null, "Сдвиг красного вправо и синего влево, в пикселях", "сдвиг RGB"),
        new ParamSpec("bottomSkew", 0.5f, 0f, 2f, null, "Шов переключения головок в нижних 2.5% кадра: надломы строк (до 45 px × bottomSkew у низа)", "шов головок"),
        new ParamSpec("wobble", 0.3f, 0f, 3f, null, "Медленная качка кадра по x (до 3 px × wobble)", "качка ленты"),
        new ParamSpec("saturation", 0.85f, 0f, 2f, null, "Насыщенность (VHS блёклый)", "насыщенность"),
        new ParamSpec("dropouts", 0.2f, 0f, 20f, null, "Частота белых горизонтальных выпадений сигнала", "выпадения"),
        new ParamSpec("badTracking", 0f, 0f, 1f, null, "Плохой трекинг: катящаяся полоса перекоса, цветного шума и белых чёрточек (0 — нет)", "плохой трекинг"),
        new ParamSpec("bandSpeed", 1f, 0.1f, 3f, null, "Скорость полосы плохого трекинга: 1 = проход сверху вниз за ~8 с", "скорость полосы"),
        new ParamSpec("iqShift", 1.5f, 0f, 12f, null, "Разъезд цветности: I из точки x+сдвиг, Q из x−сдвиг, px на 1080 по высоте (0 — нет)", "разъезд цветности"),
        new ParamSpec("chromaClip", 0.4f, 0f, 1f, null, "Обрезка цветности: порог |I|,|Q| (0 — без обрезки)", "обрезка цветности"),
        new ParamSpec("lumaRes", 1920f, 200f, 1920f, null, "Разрешение яркости, отсчётов по горизонтали (1920 — выкл)", "разрешение яркости"),
        new ParamSpec("chromaRes", 400f, 20f, 400f, null, "Разрешение цвета, отсчётов по горизонтали; по вертикали вдвое хуже (400 — выкл)", "разрешение цвета"),
    };

    private const string ChromaResource = "Phosphor.Filters.Shaders.vhs_chroma.frag";
    private const string BlurResource = "Phosphor.Filters.Shaders.vhs_blur.frag";

    private uint _chromaProg;
    private uint _blurProg;
    private int _chromaInputLoc = -1;
    private int _chromaFullLoc = -1;
    private int _chromaIqsLoc = -1;
    private int _chromaRsLoc = -1;
    private int _blurInputLoc = -1;
    private int _blurStepLoc = -1;

    private RenderTarget? _chromaDown;   // W/4 × H/4: I/Q после box-даунсэмпла
    private RenderTarget? _chromaH;      // после гаусса по x
    private RenderTarget? _chromaV;      // после гаусса по y, читает основной проход

    public override string Id => "vhs";

    protected override string FragmentResource => "Phosphor.Filters.Shaders.vhs.frag";

    public override IReadOnlyList<ParamSpec> Params => _params;

    public override void Init(GL gl, int width, int height)
    {
        base.Init(gl, width, height);

        var asm = GetType().Assembly;
        _chromaProg = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex,
            GlUtil.LoadResource(asm, ChromaResource), Id + ".chroma");
        _blurProg = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex,
            GlUtil.LoadResource(asm, BlurResource), Id + ".blur");

        _chromaInputLoc = gl.GetUniformLocation(_chromaProg, "uInput");
        _chromaFullLoc = gl.GetUniformLocation(_chromaProg, "uFull");
        _chromaIqsLoc = gl.GetUniformLocation(_chromaProg, "uIqs");
        _chromaRsLoc = gl.GetUniformLocation(_chromaProg, "uRs");
        _blurInputLoc = gl.GetUniformLocation(_blurProg, "uInput");
        _blurStepLoc = gl.GetUniformLocation(_blurProg, "uStep");

        SizeChromaTargets(width, height);
    }

    public override void Resize(int width, int height)
    {
        base.Resize(width, height);
        SizeChromaTargets(width, height);
    }

    private void SizeChromaTargets(int width, int height)
    {
        int qw = Math.Max(1, width / 4);
        int qh = Math.Max(1, height / 4);
        if (_chromaDown is null || _chromaH is null || _chromaV is null)
        {
            _chromaDown = new RenderTarget(Gl, qw, qh);
            _chromaH = new RenderTarget(Gl, qw, qh);
            _chromaV = new RenderTarget(Gl, qw, qh);
        }
        else
        {
            _chromaDown.Resize(qw, qh);
            _chromaH.Resize(qw, qh);
            _chromaV.Resize(qw, qh);
        }
    }

    public override void Apply(uint inputTex, RenderTarget output, in FrameInfo frame, FilterParams p)
    {
        // Полоса цветности нужна только при chromaRes < 400; при 400 основной проход берёт bleed-тапы сам.
        if (p["chromaRes"] < 399.5f) RunChromaPasses(inputTex, p);
        // Основной проход vhs.frag; uChroma подключается в BeforeDraw.
        base.Apply(inputTex, output, frame, p);
    }

    /// <summary>
    /// Три прохода: (1) box-даунсэмпл I/Q в W/4 × H/4 с разъездом iqShift, (2) гаусс по x, (3) гаусс по y.
    /// Гаусс: 13 проб, шаг σ/3, σx = 0.5/chromaRes в uv, σy = 0.5/240 в uv.
    /// </summary>
    private void RunChromaPasses(uint inputTex, FilterParams p)
    {
        var down = _chromaDown!;
        var h = _chromaH!;
        var v = _chromaV!;
        float fullW = Width;
        float fullH = Height;
        float iqs = p["iqShift"] * (fullH / 1080f) / fullW;
        float rs = p["rgbShift"] / fullW;
        float stepX = 1f / (6f * p["chromaRes"]);     // σx / 3 в uv
        const float stepY = 1f / 1440f;               // σy / 3 в uv: σy = 0.5/240

        Gl.Disable(EnableCap.Blend);

        // 1. Даунсэмпл I/Q.
        down.Bind();
        Gl.UseProgram(_chromaProg);
        Gl.ActiveTexture(TextureUnit.Texture0);
        Gl.BindTexture(TextureTarget.Texture2D, inputTex);
        Gl.Uniform1(_chromaInputLoc, 0);
        Gl.Uniform2(_chromaFullLoc, fullW, fullH);
        Gl.Uniform1(_chromaIqsLoc, iqs);
        Gl.Uniform1(_chromaRsLoc, rs);
        GlUtil.DrawFullscreen(Gl);

        // 2. Гаусс по горизонтали.
        h.Bind();
        Gl.UseProgram(_blurProg);
        Gl.BindTexture(TextureTarget.Texture2D, down.Texture);
        Gl.Uniform1(_blurInputLoc, 0);
        Gl.Uniform2(_blurStepLoc, stepX, 0f);
        GlUtil.DrawFullscreen(Gl);

        // 3. Гаусс по вертикали.
        v.Bind();
        Gl.BindTexture(TextureTarget.Texture2D, h.Texture);
        Gl.Uniform2(_blurStepLoc, 0f, stepY);
        GlUtil.DrawFullscreen(Gl);
    }

    protected override void BeforeDraw(in FrameInfo frame, FilterParams p)
    {
        Gl.ActiveTexture(TextureUnit.Texture1);
        Gl.BindTexture(TextureTarget.Texture2D, _chromaV!.Texture);
        SetInt("uChroma", 1);
        Gl.ActiveTexture(TextureUnit.Texture0);
    }

    public override void Dispose()
    {
        _chromaDown?.Dispose();
        _chromaH?.Dispose();
        _chromaV?.Dispose();
        _chromaDown = null;
        _chromaH = null;
        _chromaV = null;
        if (_chromaProg != 0) Gl.DeleteProgram(_chromaProg);
        if (_blurProg != 0) Gl.DeleteProgram(_blurProg);
        _chromaProg = 0;
        _blurProg = 0;
        base.Dispose();
    }
}
