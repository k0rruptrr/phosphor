using Phosphor.Core;
using Phosphor.Gfx;
using Silk.NET.OpenGL;

namespace Phosphor.Filters;

/// <summary>
/// Неоновое свечение: pyramid bloom (dual-filter). Порог с мягким коленом на 1/2 → даунсэмплы 1/4..1/64 →
/// обратный ход tent-апсэмплом, где уровни взвешены по radius → композит:
/// out = src + (1 - exp(-(core·L1 + strength·Halo))) · (1 - src). Последний множитель не даёт ядру уйти в белое выше 1.
/// Постоянный фильтр, без эпизодов и истории.
/// </summary>
public sealed class GlowFilter : ShaderFilter
{
    private static readonly IReadOnlyList<ParamSpec> _params = new ParamSpec[]
    {
        new ParamSpec("strength", 1f, 0f, 3f, null, "Сила широкого ореола цвета свечения", "ореол"),
        new ParamSpec("radius", 20f, 2f, 64f, null, "Радиус ореола в пикселях выхода", "радиус ореола"),
        new ParamSpec("core", 0.7f, 0f, 2f, null, "Сила плотного горячего ядра у штриха", "ядро"),
        new ParamSpec("threshold", 0.2f, 0f, 1f, null, "Яркость, с которой начинается свечение", "порог свечения"),
        new ParamSpec("saturation", 1.3f, 0f, 2f, null, "Насыщенность ореола: 1 — как есть, 0 — серый", "насыщенность"),
    };

    private const int Levels = 5;                   // уровни 1/4, 1/8, 1/16, 1/32, 1/64
    private const float HaloWidth = 0.5f;           // ширина гаусса по октавам
    private const float HaloGain = 1.9f;              // множитель ореола, подбирается по замеру профиля (тест Halo_Profile)
    private const string PrefilterResource = "Phosphor.Filters.Shaders.glow_prefilter.frag";
    private const string DownResource = "Phosphor.Filters.Shaders.glow_down.frag";
    private const string UpResource = "Phosphor.Filters.Shaders.glow_up.frag";

    private uint _prefilterProg;
    private uint _downProg;
    private uint _upProg;
    private int _thresholdLoc = -1;
    private int _downTexelLoc = -1;
    private int _upTexelLoc = -1;
    private int _upWeightLoc = -1;
    private int _upUseCoarseLoc = -1;

    private RenderTarget? _pre;                     // порог, 1/2
    private readonly RenderTarget?[] _down = new RenderTarget?[Levels];   // D1..D5
    private readonly RenderTarget?[] _up = new RenderTarget?[Levels];     // U1..U5 (накопленные)
    private readonly float[] _weights = new float[Levels];

    public override string Id => "glow";

    protected override string FragmentResource => "Phosphor.Filters.Shaders.glow.frag";

    public override IReadOnlyList<ParamSpec> Params => _params;

    public override void Init(GL gl, int width, int height)
    {
        base.Init(gl, width, height);

        var asm = GetType().Assembly;
        _prefilterProg = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex,
            GlUtil.LoadResource(asm, PrefilterResource), Id + ".prefilter");
        _downProg = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex,
            GlUtil.LoadResource(asm, DownResource), Id + ".down");
        _upProg = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex,
            GlUtil.LoadResource(asm, UpResource), Id + ".up");

        _thresholdLoc = gl.GetUniformLocation(_prefilterProg, "uThreshold");
        _downTexelLoc = gl.GetUniformLocation(_downProg, "uTexel");
        _upTexelLoc = gl.GetUniformLocation(_upProg, "uTexel");
        _upWeightLoc = gl.GetUniformLocation(_upProg, "uWeight");
        _upUseCoarseLoc = gl.GetUniformLocation(_upProg, "uUseCoarse");

        // Юниты сэмплеров фиксированы: задаём один раз.
        gl.UseProgram(_prefilterProg);
        gl.Uniform1(gl.GetUniformLocation(_prefilterProg, "uInput"), 0);
        gl.UseProgram(_downProg);
        gl.Uniform1(gl.GetUniformLocation(_downProg, "uInput"), 0);
        gl.UseProgram(_upProg);
        gl.Uniform1(gl.GetUniformLocation(_upProg, "uSkip"), 0);
        gl.Uniform1(gl.GetUniformLocation(_upProg, "uCoarse"), 1);

        AllocateTargets(width, height);
    }

    public override void Resize(int width, int height)
    {
        base.Resize(width, height);
        AllocateTargets(width, height);
    }

    private void AllocateTargets(int width, int height)
    {
        int pw = Math.Max(1, width / 2), ph = Math.Max(1, height / 2);
        if (_pre is null) _pre = new RenderTarget(Gl, pw, ph);
        else _pre.Resize(pw, ph);

        for (int i = 0; i < Levels; i++)
        {
            int div = 4 << i;                       // 4, 8, 16, 32, 64
            int lw = Math.Max(1, width / div), lh = Math.Max(1, height / div);
            if (_down[i] is null)
            {
                _down[i] = new RenderTarget(Gl, lw, lh);
                _up[i] = new RenderTarget(Gl, lw, lh);
            }
            else
            {
                _down[i]!.Resize(lw, lh);
                _up[i]!.Resize(lw, lh);
            }
        }
    }

    /// <summary>
    /// Вес уровня i покрывает масштаб 2^(2+i) пикселей выхода. Гаусс по log2 с шириной 1 октава вокруг radius,
    /// умножен на HaloGain. Без нормировки на площадь: это художественный ореол, а не физический блум,
    /// тонкий штрих должен светиться заметно.
    /// </summary>
    private void ComputeWeights(float radius)
    {
        float lr = MathF.Log2(radius);
        for (int i = 0; i < Levels; i++)
        {
            float d = (2f + i) - lr;
            _weights[i] = HaloGain * MathF.Exp(-0.5f * (d * d) / (HaloWidth * HaloWidth));
        }
    }

    public override void Apply(uint inputTex, RenderTarget output, in FrameInfo frame, FilterParams p)
    {
        Gl.Disable(EnableCap.Blend);
        ComputeWeights(p["radius"]);

        // 1. Порог с мягким коленом, на 1/2. Цвет сохраняется: умножаем на вес.
        var pre = _pre!;
        pre.Bind();
        Gl.UseProgram(_prefilterProg);
        Gl.ActiveTexture(TextureUnit.Texture0);
        Gl.BindTexture(TextureTarget.Texture2D, inputTex);
        Gl.Uniform1(_thresholdLoc, p["threshold"]);
        GlUtil.DrawFullscreen(Gl);

        // 2. Даунсэмплы 1/4..1/64: цепочка, каждый уровень из предыдущего.
        RenderTarget src = pre;
        for (int i = 0; i < Levels; i++)
        {
            var dst = _down[i]!;
            dst.Bind();
            Gl.UseProgram(_downProg);
            Gl.ActiveTexture(TextureUnit.Texture0);
            Gl.BindTexture(TextureTarget.Texture2D, src.Texture);
            Gl.Uniform2(_downTexelLoc, 1f / src.Width, 1f / src.Height);
            GlUtil.DrawFullscreen(Gl);
            src = dst;
        }

        // 3. Обратный ход: U5 = w5·D5; Uk = wk·Dk + tent(U(k+1)).
        for (int i = Levels - 1; i >= 0; i--)
        {
            var up = _up[i]!;
            bool hasCoarse = i < Levels - 1;
            var coarse = hasCoarse ? _up[i + 1]! : _down[i]!;   // без uUseCoarse шейдер не читает этот юнит
            up.Bind();
            Gl.UseProgram(_upProg);
            Gl.ActiveTexture(TextureUnit.Texture0);
            Gl.BindTexture(TextureTarget.Texture2D, _down[i]!.Texture);
            Gl.ActiveTexture(TextureUnit.Texture1);
            Gl.BindTexture(TextureTarget.Texture2D, coarse.Texture);
            Gl.Uniform2(_upTexelLoc, 1f / coarse.Width, 1f / coarse.Height);
            Gl.Uniform1(_upWeightLoc, _weights[i]);
            Gl.Uniform1(_upUseCoarseLoc, hasCoarse ? 1 : 0);
            GlUtil.DrawFullscreen(Gl);
            Gl.ActiveTexture(TextureUnit.Texture0);
        }

        // 4. Композит в output; uCore и uHalo подключаются в BeforeDraw.
        base.Apply(inputTex, output, frame, p);
    }

    protected override void BeforeDraw(in FrameInfo frame, FilterParams p)
    {
        Gl.ActiveTexture(TextureUnit.Texture1);
        Gl.BindTexture(TextureTarget.Texture2D, _up[0]!.Texture);     // накопленный ореол
        SetInt("uHalo", 1);
        Gl.ActiveTexture(TextureUnit.Texture2);
        Gl.BindTexture(TextureTarget.Texture2D, _down[0]!.Texture);   // узкое ядро L1 (1/4)
        SetInt("uCore", 2);
        Gl.ActiveTexture(TextureUnit.Texture0);
    }

    public override void Dispose()
    {
        _pre?.Dispose();
        _pre = null;
        for (int i = 0; i < Levels; i++)
        {
            _down[i]?.Dispose();
            _up[i]?.Dispose();
            _down[i] = null;
            _up[i] = null;
        }
        if (_prefilterProg != 0) Gl.DeleteProgram(_prefilterProg);
        if (_downProg != 0) Gl.DeleteProgram(_downProg);
        if (_upProg != 0) Gl.DeleteProgram(_upProg);
        _prefilterProg = 0;
        _downProg = 0;
        _upProg = 0;
        base.Dispose();
    }
}
