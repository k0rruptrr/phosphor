using System.Reflection;
using System.Runtime.CompilerServices;
using Phosphor.Core;
using Phosphor.Gfx;
using Silk.NET.OpenGL;

[assembly: InternalsVisibleTo("Phosphor.Filters.Tests")]

namespace Phosphor.Filters;

/// <summary>
/// Датамош: видеокодек потерял ключевой кадр, новые кадры приходят только как P-кадры (векторы движения и остаток).
/// Старая картинка течёт по векторам движения новой, пиксели размазываются блоками. Новая картинка проступает
/// только там, где разница большая. Конец эпизода — ключевой кадр: сразу чисто.
/// Состояние — прошлый испорченный кадр (ping-pong), история прошлого входа и яркости четверти.
/// Расписание эпизодов считается здесь же, по слотам по 6 с; смена сцены — отдельный эпизод.
/// </summary>
public sealed class DatamoshFilter : IFilter
{
    private static readonly IReadOnlyList<ParamSpec> _params = new ParamSpec[]
    {
        new ParamSpec("intensity", 0.75f, 0f, 1f, null, "Сила мош-эффекта: 0 — выкл; 1 — почти чистое течение, новый кадр почти не проступает", "интенсивность", Clean: 0f, TextOff: true),
        new ParamSpec("rate", 0.5f, 0f, 6f, null, "Эпизоды в минуту: сколько раз за минуту кодек «теряет» ключевой кадр", "частота эпизодов"),
        new ParamSpec("block", 16f, 8f, 32f, null, "Размер блока векторов движения, px: крупнее — блоки видны грубее", "размер блока"),
        new ParamSpec("onTransition", 1f, 0f, 1f, null, "1 — эпизод при смене сцены: старая сцена течёт в движении новой", "по смене сцены"),
    };

    private const string ShaderPrefix = "Phosphor.Filters.Shaders.";
    private const int MotionBlockMin = 8;      // сетка векторов рассчитана на минимальный блок
    private const double SlotSec = 6.0;        // длина слота расписания, с
    private const double EpisodeMinSec = 1.5;  // длина эпизода: 1.5…5 с
    private const double EpisodeSpanSec = 3.5;
    private const double TransitionSec = 3.0;  // эпизод при смене сцены

    private GL Gl = null!;
    private int _w, _h;

    private uint _progLuma, _progSad, _progMotion, _progState, _progOut, _progCopy;
    private int _lumaRes, _sadQCur, _sadQPrev, _sadQSize, _sadGrid, _sadBlock, _motGrid;
    private int _stIn, _stPrevIn, _stMosh, _stVec, _stRes, _stBlock, _stActive, _stLeak;
    private int _outAmt;

    private RenderTarget _prevIn = null!;          // вход прошлого кадра
    private RenderTarget[] _mosh = null!;          // состояние, ping-pong
    private RenderTarget[] _luma = null!;          // яркость четверти, ping-pong
    private RenderTarget _sad = null!;             // SAD по (блок, кандидат): texel (cx, cy·49 + c)
    private RenderTarget _vec = null!;             // векторы блоков, сетка под блок 8

    private int _moshCur;
    private int _lumaCur;
    private bool _hasHistory;
    private float _prevTransition;
    private double _transitionStart = double.NegativeInfinity;

    /// <summary>Тестовый хук: −1 — по расписанию, иначе фаза эпизода 0..1.</summary>
    internal float ForceEpisode = -1f;

    public string Id => "datamosh";

    public IReadOnlyList<ParamSpec> Params => _params;

    public void Init(GL gl, int width, int height)
    {
        Gl = gl;
        _w = width;
        _h = height;
        var asm = GetType().Assembly;

        _progLuma = Prog(asm, "datamosh_luma.frag", "luma");
        _progSad = Prog(asm, "datamosh_sad.frag", "sad");
        _progMotion = Prog(asm, "datamosh_motion.frag", "motion");
        _progState = Prog(asm, "datamosh_state.frag", "state");
        _progOut = Prog(asm, "datamosh.frag", "out");
        _progCopy = Prog(asm, "datamosh_copy.frag", "copy");

        _lumaRes = Loc(_progLuma, "uRes");
        _sadQCur = Loc(_progSad, "uQCur");
        _sadQPrev = Loc(_progSad, "uQPrev");
        _sadQSize = Loc(_progSad, "uQSize");
        _sadGrid = Loc(_progSad, "uGrid");
        _motGrid = Loc(_progMotion, "uGrid");
        _sadBlock = Loc(_progSad, "uBlock");
        _stIn = Loc(_progState, "uInput");
        _stPrevIn = Loc(_progState, "uPrevIn");
        _stMosh = Loc(_progState, "uMosh");
        _stVec = Loc(_progState, "uVec");
        _stRes = Loc(_progState, "uResolution");
        _stBlock = Loc(_progState, "uBlock");
        _stActive = Loc(_progState, "uActive");
        _stLeak = Loc(_progState, "uLeak");
        _outAmt = Loc(_progOut, "uAmt");

        // Сэмплеры — фиксированные юниты, выставляются один раз.
        SetSampler(_progLuma, "uInput", 0);
        SetSampler(_progSad, "uQCur", 0);
        SetSampler(_progSad, "uQPrev", 1);
        SetSampler(_progMotion, "uSad", 0);
        SetSampler(_progState, "uInput", 0);
        SetSampler(_progState, "uPrevIn", 1);
        SetSampler(_progState, "uMosh", 2);
        SetSampler(_progState, "uVec", 3);
        SetSampler(_progOut, "uInput", 0);
        SetSampler(_progOut, "uMosh", 1);
        SetSampler(_progCopy, "uInput", 0);

        Allocate(width, height);
    }

    public void Resize(int width, int height)
    {
        _w = width;
        _h = height;
        Allocate(width, height);
    }

    /// <summary>Создать буферы или пересоздать их под новый размер. История после этого начинается заново.</summary>
    private void Allocate(int width, int height)
    {
        int qw = Math.Max(1, (width + 3) / 4), qh = Math.Max(1, (height + 3) / 4);
        int gw = Math.Max(1, (width + MotionBlockMin - 1) / MotionBlockMin);
        int gh = Math.Max(1, (height + MotionBlockMin - 1) / MotionBlockMin);
        if (_prevIn is null)
        {
            _prevIn = new RenderTarget(Gl, width, height);
            _mosh = new[] { new RenderTarget(Gl, width, height), new RenderTarget(Gl, width, height) };
            _luma = new[] { new RenderTarget(Gl, qw, qh), new RenderTarget(Gl, qw, qh) };
            _sad = new RenderTarget(Gl, gw, gh * 49);
            _vec = new RenderTarget(Gl, gw, gh);
        }
        else
        {
            _prevIn.Resize(width, height);
            _mosh[0].Resize(width, height);
            _mosh[1].Resize(width, height);
            _luma[0].Resize(qw, qh);
            _luma[1].Resize(qw, qh);
            _sad.Resize(gw, gh * 49);
            _vec.Resize(gw, gh);
        }
        _hasHistory = false;   // после смены размера история пуста
    }

    public void Apply(uint inputTex, RenderTarget output, in FrameInfo frame, FilterParams p)
    {
        float intensity = p["intensity"];
        float rate = p["rate"];
        int block = (int)MathF.Round(p["block"]);
        bool onTransition = p["onTransition"] >= 0.5f;

        // Смена сцены: фронт transition 0→1 запускает эпизод.
        float tr = frame.Get("transition");
        bool rising = tr > 0f && _prevTransition <= 0f;
        _prevTransition = tr;
        if (rising && onTransition) _transitionStart = frame.Time;

        double t = frame.Time;
        bool episode;
        float phase = 0f;
        if (ForceEpisode >= 0f)
        {
            episode = true;
            phase = ForceEpisode;
        }
        else if (t >= _transitionStart && t < _transitionStart + TransitionSec)
        {
            episode = true;
            phase = (float)((t - _transitionStart) / TransitionSec);
        }
        else
        {
            episode = Schedule(t, rate, out phase);
        }

        // Мошинг идёт только при силе > 0 и при уже имеющейся истории; вне эпизода состояние = вход.
        bool moshing = episode && intensity > 0f && _hasHistory;
        float amt = episode ? Smooth01(intensity / 0.25f) * Envelope(phase) : 0f;
        float leak = 0.5f * (1f - intensity);

        Gl.Disable(EnableCap.Blend);

        // 1. Яркость входа в четверти разрешения: нужна каждый кадр, чтобы знать прошлый.
        var luma = _luma[_lumaCur];
        var lumaPrev = _luma[1 - _lumaCur];
        luma.Bind();
        Gl.UseProgram(_progLuma);
        BindTex(0, inputTex);
        Gl.Uniform2(_lumaRes, _w, _h);
        GlUtil.DrawFullscreen(Gl);

        // 2. Векторы движения блоков (только в эпизоде): SAD по парам (блок, кандидат), затем минимум.
        if (moshing)
        {
            int gw = (_w + block - 1) / block, gh = (_h + block - 1) / block;
            _sad.Bind();
            Gl.UseProgram(_progSad);
            BindTex(0, luma.Texture);
            BindTex(1, lumaPrev.Texture);
            Gl.Uniform2(_sadQSize, luma.Width, luma.Height);
            Gl.Uniform2(_sadGrid, gw, gh);
            Gl.Uniform1(_sadBlock, block);
            GlUtil.DrawFullscreen(Gl);

            _vec.Bind();
            Gl.UseProgram(_progMotion);
            BindTex(0, _sad.Texture);
            Gl.Uniform2(_motGrid, gw, gh);
            GlUtil.DrawFullscreen(Gl);
        }

        // 3. Состояние: старая картинка по векторам плюс остаток (или чистый вход вне эпизода).
        var prevState = _mosh[_moshCur];
        var newState = _mosh[1 - _moshCur];
        newState.Bind();
        Gl.UseProgram(_progState);
        BindTex(0, inputTex);
        BindTex(1, _prevIn.Texture);
        BindTex(2, prevState.Texture);
        BindTex(3, _vec.Texture);
        Gl.Uniform2(_stRes, (float)_w, (float)_h);
        Gl.Uniform1(_stBlock, (float)block);
        Gl.Uniform1(_stActive, moshing ? 1f : 0f);
        Gl.Uniform1(_stLeak, leak);
        GlUtil.DrawFullscreen(Gl);
        _moshCur = 1 - _moshCur;

        // 4. Выход: вход, смешанный с состоянием по огибающей эпизода.
        output.Bind();
        Gl.UseProgram(_progOut);
        BindTex(0, inputTex);
        BindTex(1, _mosh[_moshCur].Texture);
        Gl.Uniform1(_outAmt, amt);
        GlUtil.DrawFullscreen(Gl);

        // 5. Вход — в историю для следующего кадра.
        _prevIn.Bind();
        Gl.UseProgram(_progCopy);
        BindTex(0, inputTex);
        GlUtil.DrawFullscreen(Gl);

        _lumaCur = 1 - _lumaCur;
        _hasHistory = true;
        Gl.ActiveTexture(TextureUnit.Texture0);
    }

    /// <summary>
    /// Расписание: слот 6 с; в слоте хеш решает, есть ли эпизод (rate эпизодов в минуту — 10 слотов в минуту),
    /// затем длина 1.5–5 с и начало внутри слота. Детерминировано от времени.
    /// </summary>
    internal static bool Schedule(double t, float rate, out float phase)
    {
        phase = 0f;
        if (rate <= 0f || t < 0.0) return false;
        long k = (long)Math.Floor(t / SlotSec);
        if (Hash01(k, 0) >= rate / (60f / (float)SlotSec)) return false;
        double dur = EpisodeMinSec + EpisodeSpanSec * Hash01(k, 2);
        double start = k * SlotSec + Hash01(k, 1) * (SlotSec - dur);
        if (t < start || t >= start + dur) return false;
        phase = (float)((t - start) / dur);
        return true;
    }

    /// <summary>Огибающая эпизода: нарастание 0..0.15, держится, спад 0.8..1.</summary>
    private static float Envelope(float ph) => Smooth01(ph / 0.15f) * (1f - Smooth01((ph - 0.8f) / 0.2f));

    private static float Smooth01(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    /// <summary>Целочисленный хеш в [0, 1) от слота и соли.</summary>
    private static float Hash01(long k, uint salt)
    {
        unchecked
        {
            uint h = (uint)k * 0x9E3779B1u ^ (salt + 1u) * 0x85EBCA77u;
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h * (1f / 4294967296f);
        }
    }

    private void BindTex(int unit, uint tex)
    {
        Gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
        Gl.BindTexture(TextureTarget.Texture2D, tex);
    }

    private uint Prog(Assembly asm, string file, string name) =>
        GlUtil.CreateProgram(Gl, GlUtil.FullscreenVertex, GlUtil.LoadResource(asm, ShaderPrefix + file), Id + "." + name);

    private int Loc(uint prog, string name) => Gl.GetUniformLocation(prog, name);

    private void SetSampler(uint prog, string name, int unit)
    {
        Gl.UseProgram(prog);
        Gl.Uniform1(Loc(prog, name), unit);
    }

    public void Dispose()
    {
        _prevIn?.Dispose();
        if (_mosh is not null) foreach (var r in _mosh) r.Dispose();
        if (_luma is not null) foreach (var r in _luma) r.Dispose();
        _sad?.Dispose();
        _vec?.Dispose();
        if (_progLuma != 0) Gl.DeleteProgram(_progLuma);
        if (_progSad != 0) Gl.DeleteProgram(_progSad);
        if (_progMotion != 0) Gl.DeleteProgram(_progMotion);
        if (_progState != 0) Gl.DeleteProgram(_progState);
        if (_progOut != 0) Gl.DeleteProgram(_progOut);
        if (_progCopy != 0) Gl.DeleteProgram(_progCopy);
        _progLuma = _progSad = _progMotion = _progState = _progOut = _progCopy = 0;
    }
}
