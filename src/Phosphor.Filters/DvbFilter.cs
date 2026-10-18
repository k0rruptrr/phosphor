using System.Runtime.CompilerServices;
using Phosphor.Core;
using Phosphor.Gfx;
using Silk.NET.OpenGL;

[assembly: InternalsVisibleTo("Phosphor.Filters.Tests")]

namespace Phosphor.Filters;

/// <summary>
/// Срыв цифрового эфира (DVB-S2 / MPEG): ошибки срезов по макроблокам (замёрзший блок, неверный вектор,
/// плоский цвет, зелёный блок, DCT-узор), блочность низкого битрейта, заморозка кадра с догрузкой блоков
/// и сильный срыв. Эпизоды по расписанию: слоты по 6 с, в каждом слоте хеш решает, есть ли эпизод.
/// Два прохода: расписание срезов (полоса 1 × H) и основной шейдер; история — выход прошлого кадра.
/// </summary>
public sealed class DvbFilter : ShaderFilter
{
    private static readonly IReadOnlyList<ParamSpec> _params = new ParamSpec[]
    {
        new ParamSpec("severity", 0.6f, 0f, 1f, null, "Насколько плохой сигнал в эпизоде: 0 — эпизодов нет, 1 — почти весь кадр рассыпается", "сила сигнала", Clean: 0f, TextOff: true),
        new ParamSpec("rate", 0.8f, 0f, 6f, null, "Эпизодов срыва в минуту (0 — нет); длительность эпизода 1–6 с", "эпизодов в минуту"),
        new ParamSpec("block", 16f, 8f, 32f, null, "Размер макроблока: сетка ошибок среза, px", "размер блока"),
        new ParamSpec("freeze", 1f, 0f, 1f, null, "Заморозки: кадр замирает на 0.3–2 с, затем блоки догружаются кусками (1 — разрешено)", "заморозки"),
    };

    private const string SliceResource = "Phosphor.Filters.Shaders.dvb_slice.frag";
    private const float SlotSec = 6f;        // расписание: слоты по 6 с, не больше одного эпизода в слоте
    private const float ForcedLen = 4f;      // длина эпизода при ForceEpisode, с
    private const float RowPerQ = 0.07f;     // вероятность среза на строку блоков на единицу качества (1–4 среза при 0.6)

    private uint _sliceProg;
    private int _sliceBlockLoc = -1;
    private int _sliceColsLoc = -1;
    private int _sliceTimeLoc = -1;
    private int _sliceOriginLoc = -1;
    private int _sliceQLoc = -1;

    private RenderTarget? _slice;   // 2 × H: параметры срезов по строкам (см. dvb_slice.frag)
    private RenderTarget? _hist;    // история: выход прошлого кадра
    private bool _histOk;

    // Состояние эпизода на текущий кадр: считается в Apply, отдаётся шейдеру в BeforeDraw.
    private float _q;
    private float _epoch;
    private float _freezeOn;
    private float _refresh = 1f;
    private float _brk;
    private float _block = 16f;

    /// <summary>Тестовый хук: фаза эпизода 0..1 (эпизод длится ForcedLen с, заморозка в 0.5–1.5 с); −1 — по расписанию.</summary>
    internal float ForceEpisode = -1f;

    public override string Id => "dvb";

    protected override string FragmentResource => "Phosphor.Filters.Shaders.dvb.frag";

    public override IReadOnlyList<ParamSpec> Params => _params;

    public override void Init(GL gl, int width, int height)
    {
        base.Init(gl, width, height);

        var asm = GetType().Assembly;
        _sliceProg = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex,
            GlUtil.LoadResource(asm, SliceResource), Id + ".slice");
        _sliceBlockLoc = gl.GetUniformLocation(_sliceProg, "uBlock");
        _sliceColsLoc = gl.GetUniformLocation(_sliceProg, "uCols");
        _sliceTimeLoc = gl.GetUniformLocation(_sliceProg, "uTime");
        _sliceOriginLoc = gl.GetUniformLocation(_sliceProg, "uOrigin");
        _sliceQLoc = gl.GetUniformLocation(_sliceProg, "uQ");

        _slice = new RenderTarget(gl, 2, height);
        _hist = new RenderTarget(gl, width, height);
        _histOk = false;
    }

    public override void Resize(int width, int height)
    {
        base.Resize(width, height);
        _slice!.Resize(2, height);
        _hist!.Resize(width, height);
        _histOk = false;
    }

    public override void Apply(uint inputTex, RenderTarget output, in FrameInfo frame, FilterParams p)
    {
        float severity = p["severity"];
        float rate = p["rate"];
        _block = MathF.Round(p["block"]);
        bool freezeAllowed = p["freeze"] > 0.5f;
        float t = frame.Time;

        bool active;
        EpisodeInfo ep;
        if (ForceEpisode >= 0f)
        {
            ep = EpisodeInfo.Forced(ForceEpisode);
            active = true;
        }
        else
        {
            active = Find(t, rate, out ep);
        }

        float q = 0f, freezeOn = 0f, refresh = 1f, brk = 0f;
        if (active)
        {
            Evaluate(in ep, severity, freezeAllowed, out q, out freezeOn, out refresh, out brk);
        }
        // Нулевая сила — тождество: ни срезов, ни заморозки, ни срыва.
        if (q <= 0f)
        {
            q = 0f; freezeOn = 0f; refresh = 1f; brk = 0f;
        }
        _q = q;
        _freezeOn = freezeOn;
        _refresh = refresh;
        _brk = brk;
        _epoch = MathF.Floor(t * 2f);

        if (q > 0f) RunSlicePass(_block, t, RowPerQ * q, q);

        base.Apply(inputTex, output, frame, p);
    }

    /// <summary>Проход расписания срезов: 2 × H, для каждой строки пикселей — параметры среза (хвост строки).</summary>
    private void RunSlicePass(float block, float time, float origin, float q)
    {
        var slice = _slice!;
        slice.Bind();
        Gl.Disable(EnableCap.Blend);
        Gl.UseProgram(_sliceProg);
        Gl.Uniform1(_sliceBlockLoc, block);
        Gl.Uniform1(_sliceColsLoc, MathF.Ceiling(Width / block));
        Gl.Uniform1(_sliceTimeLoc, time);
        Gl.Uniform1(_sliceOriginLoc, origin);
        Gl.Uniform1(_sliceQLoc, q);
        GlUtil.DrawFullscreen(Gl);
    }

    protected override void BeforeDraw(in FrameInfo frame, FilterParams p)
    {
        Gl.ActiveTexture(TextureUnit.Texture1);
        Gl.BindTexture(TextureTarget.Texture2D, _hist!.Texture);
        SetInt("uHist", 1);
        Gl.ActiveTexture(TextureUnit.Texture2);
        Gl.BindTexture(TextureTarget.Texture2D, _slice!.Texture);
        SetInt("uSlice", 2);
        Gl.ActiveTexture(TextureUnit.Texture0);

        SetFloat("uQ", _q);
        SetFloat("uEpoch", _epoch);
        SetFloat("uFreezeOn", _freezeOn);
        SetFloat("uRefresh", _refresh);
        SetFloat("uBrk", _brk);
        SetFloat("uHistOk", _histOk ? 1f : 0f);
    }

    protected override void AfterDraw(RenderTarget output, in FrameInfo frame)
    {
        // История = выход этого кадра: для следующего кадра (заморозка, замёрзшие блоки).
        var hist = _hist!;
        Gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, output.Fbo);
        Gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, hist.Fbo);
        Gl.BlitFramebuffer(0, 0, output.Width, output.Height, 0, 0, hist.Width, hist.Height,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        output.Bind();
        _histOk = true;
    }

    // ---- Расписание эпизодов (детерминировано от времени) ----

    /// <summary>Описание эпизода: фаза и локальное время, плюс заранее выбранные окна заморозки и срыва (с).</summary>
    internal readonly record struct EpisodeInfo(
        float Phase, float LocalSec, float Dur, uint Seed,
        bool FreezeWanted, float FreezeStart, float FreezeLen, float RefreshLen,
        bool BreakdownWanted, float BrkStart, float BrkLen)
    {
        /// <summary>Тестовый эпизод: фаза 0..1 задана снаружи.</summary>
        public static EpisodeInfo Forced(float phase) => new(
            Phase: Math.Clamp(phase, 0f, 1f), LocalSec: Math.Clamp(phase, 0f, 1f) * ForcedLen, Dur: ForcedLen, Seed: 0x51u,
            FreezeWanted: true, FreezeStart: 0.5f, FreezeLen: 1f, RefreshLen: 0.5f,
            BreakdownWanted: false, BrkStart: 0f, BrkLen: 0f);
    }

    /// <summary>Эпизод, который идёт в момент t (с), по слоту floor(t / 6). Возвращает false, если эпизода нет.</summary>
    internal static bool Find(float t, float rate, out EpisodeInfo ep)
    {
        ep = default;
        if (t < 0f) return false;
        uint slot = (uint)MathF.Floor(t / SlotSec);
        float perSlot = Math.Clamp(rate, 0f, 6f) / 10f;   // эпизодов в минуту → доля слотов с эпизодом
        if (Rnd(7u, slot) >= perSlot) return false;

        float dur = 1f + 5f * Rnd(9u, slot);
        float start = slot * SlotSec + Rnd(8u, slot) * (SlotSec - dur);
        float lt = t - start;
        if (lt < 0f || lt >= dur) return false;

        bool freezeWanted = dur >= 3f && Rnd(10u, slot) < 0.5f;
        float refreshLen = 0.3f + 0.2f * Rnd(13u, slot);
        float freezeLen = 0.3f + MathF.Min(1.7f, dur * 0.4f - 0.3f) * Rnd(12u, slot);
        float freezeStart = (dur - freezeLen - refreshLen) * Rnd(11u, slot);

        bool brkWanted = dur >= 2.5f && Rnd(14u, slot) < 0.25f;
        float brkLen = 0.5f + Rnd(16u, slot);
        float brkStart = (dur - brkLen) * Rnd(15u, slot);

        ep = new EpisodeInfo(lt / dur, lt, dur, slot,
            freezeWanted, freezeStart, freezeLen, refreshLen,
            brkWanted, brkStart, brkLen);
        return true;
    }

    /// <summary>
    /// Сила, заморозка и срыв на момент эпизода. Огибающая: нарастание 20%, держится, спад 20%.
    /// refresh 0..1 — доля обновлённых блоков после заморозки (1 — всё новое).
    /// </summary>
    internal static void Evaluate(in EpisodeInfo e, float severity, bool freezeAllowed,
        out float q, out float freezeOn, out float refresh, out float brk)
    {
        float env = Smooth(e.Phase / 0.2f) * (1f - Smooth((e.Phase - 0.8f) / 0.2f));
        q = Math.Clamp(severity * env, 0f, 1f);

        freezeOn = 0f;
        refresh = 1f;
        if (freezeAllowed && e.FreezeWanted)
        {
            float lt = e.LocalSec;
            float fEnd = e.FreezeStart + e.FreezeLen;
            if (lt >= e.FreezeStart && lt < fEnd) freezeOn = 1f;
            else if (lt >= fEnd && lt < fEnd + e.RefreshLen) refresh = (lt - fEnd) / e.RefreshLen;
        }

        brk = 0f;
        if (e.BreakdownWanted)
        {
            float lt = e.LocalSec;
            float bEnd = e.BrkStart + e.BrkLen;
            if (lt >= e.BrkStart && lt < bEnd)
                brk = Math.Clamp(Math.Min((lt - e.BrkStart) / 0.15f, (bEnd - lt) / 0.15f), 0f, 1f);
        }
    }

    private static float Smooth(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    /// <summary>Хеш (как в vhs.frag, но на C#): детерминированный ряд значений 0..1 по двум целым.</summary>
    internal static float Rnd(uint a, uint b) => Hash(Hash(a * 0x9E3779B9u ^ b)) * (1f / 4294967296f);

    private static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;
        return x;
    }

    public override void Dispose()
    {
        _slice?.Dispose();
        _hist?.Dispose();
        _slice = null;
        _hist = null;
        if (_sliceProg != 0) Gl.DeleteProgram(_sliceProg);
        _sliceProg = 0;
        base.Dispose();
    }
}
