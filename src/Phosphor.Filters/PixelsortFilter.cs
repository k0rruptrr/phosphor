using System.Runtime.CompilerServices;
using Phosphor.Core;
using Phosphor.Gfx;
using Silk.NET.OpenGL;

[assembly: InternalsVisibleTo("Phosphor.Filters.Tests")]

namespace Phosphor.Filters;

/// <summary>
/// Сортировка пикселей (глитч-арт). В зоне эпизода (полоса строк или столбцов) каждый отрезок яркого пикселя
/// (яркость ≥ threshold) сортируется по яркости odd-even transposition проходами. Отрезок начинается на границе
/// маски (переход из тёмного в яркое), режется на куски по span от своего начала. Состояние переносится между кадрами.
/// Эпизоды по расписанию: слот 8 с, хеш решает, есть ли эпизод (rate в минуту), длительность 2–6 с.
/// Внутри: зона и проходы нарастают за 0.5 с, держатся, в конце кросс-фейд ко входу за 0.3 с. Вне эпизода выход = вход.
/// </summary>
public sealed class PixelsortFilter : ShaderFilter
{
    private static readonly IReadOnlyList<ParamSpec> _params = new ParamSpec[]
    {
        new ParamSpec("intensity", 0.8f, 0f, 1f, null, "Доля кадра под сортировкой: полоса строк (столбцов) в эпизоде; вместе с ней растёт число проходов в кадре", "доля кадра", Clean: 0f, TextOff: true),
        new ParamSpec("threshold", 0.45f, 0f, 1f, null, "Порог яркости: пиксели темнее порога не участвуют в сортировке и остаются на месте; отрезки начинаются на границе порога", "порог яркости"),
        new ParamSpec("span", 160f, 16f, 512f, null, "Длина куска в пикселях: отрезок режется на куски по этой длине, отсчёт от начала отрезка", "длина отрезка"),
        new ParamSpec("rate", 0.4f, 0f, 6f, null, "Частота эпизодов, в среднем в минуту (0 — эпизодов нет)", "эпизодов в минуту"),
        new ParamSpec("dir", 4f, 0f, 4f, new[] { "down", "up", "right", "left", "random" }, "Куда стекает светлое: down и up — сортировка столбцов, right и left — строк; random — своё направление в каждом эпизоде", "направление"),
    };

    private const int Passes = 16;          // проходов сортировки за кадр на полной силе эпизода (×(0.5 + intensity/2))
    private const float SlotSec = 8f;       // слот расписания
    private const float MinDur = 2f;        // длительность эпизода: 2..6 с
    private const float DurRange = 4f;
    private const float RampSec = 0.5f;     // нарастание зоны и проходов
    private const float FadeSec = 0.3f;     // кросс-фейд ко входу в конце эпизода
    private const float ForceDur = 4f;      // длительность эпизода при ForceEpisode
    private const long NoEpisode = long.MinValue;
    private const long ForcedId = -2;

    /// <summary>Эпизод на момент t: Active, Tau — секунды от его начала, Dur — длительность, Id — номер (смена = свежее состояние), Dir 0..3, Seed.</summary>
    internal readonly record struct Episode(bool Active, float Tau, float Dur, long Id, int Dir, int Seed);

    /// <summary>Тестовый хук: −1 — по расписанию; 0..1 — эпизод всё время, фаза (φ × 4 с).</summary>
    internal float ForceEpisode = -1f;

    /// <summary>Программа и кеш её uniform-локаций.</summary>
    private sealed class Prog : IDisposable
    {
        private readonly GL _gl;
        private readonly Dictionary<string, int> _loc = new(StringComparer.Ordinal);
        public uint Id { get; private set; }

        public Prog(GL gl, string suffix, string debugName)
        {
            _gl = gl;
            Id = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex,
                GlUtil.LoadResource(typeof(PixelsortFilter).Assembly, "Phosphor.Filters.Shaders.pixelsort_" + suffix + ".frag"), debugName);
        }

        public int Loc(string name)
        {
            if (!_loc.TryGetValue(name, out var l)) _loc[name] = l = _gl.GetUniformLocation(Id, name);
            return l;
        }

        public void Dispose()
        {
            if (Id != 0) _gl.DeleteProgram(Id);
            Id = 0;
        }
    }

    private Prog? _reset, _copy, _start, _scan, _sort;
    private RenderTarget? _stateA;       // состояние: rgb — отсортированный цвет, alpha — начало отрезка (или −1)
    private RenderTarget? _stateB;
    private RenderTarget? _prevIn;       // прошлый вход: rgb, alpha — его яркость
    private RenderTarget? _cur;          // актуальное состояние
    private uint _stateTex;              // что BeforeDraw подключает на юнит 1
    private long _lastId = NoEpisode;    // номер прошлого активного эпизода
    private int _phase;                  // чётность следующего прохода (сквозная между кадрами)

    // Параметры текущего кадра.
    private int _horiz, _desc, _span, _len, _zoneLo, _zoneHi;
    private float _lo, _mix;

    public override string Id => "pixelsort";

    protected override string FragmentResource => "Phosphor.Filters.Shaders.pixelsort.frag";

    public override IReadOnlyList<ParamSpec> Params => _params;

    public override void Init(GL gl, int width, int height)
    {
        base.Init(gl, width, height);
        _reset = new Prog(gl, "reset", Id + ".reset");
        _copy = new Prog(gl, "copy", Id + ".copy");
        _start = new Prog(gl, "start", Id + ".start");
        _scan = new Prog(gl, "scan", Id + ".scan");
        _sort = new Prog(gl, "sort", Id + ".sort");
        _stateA = new RenderTarget(gl, width, height);
        _stateB = new RenderTarget(gl, width, height);
        _prevIn = new RenderTarget(gl, width, height);
        _cur = _stateA;
    }

    public override void Resize(int width, int height)
    {
        base.Resize(width, height);
        _stateA?.Resize(width, height);
        _stateB?.Resize(width, height);
        _prevIn?.Resize(width, height);
        _lastId = NoEpisode;   // содержимое пересоздано: следующий эпизод начнётся со свежего состояния
    }

    public override void Apply(uint inputTex, RenderTarget output, in FrameInfo frame, FilterParams p)
    {
        var ep = ForceEpisode >= 0f
            ? ForcedEpisode(ForceEpisode)
            : Schedule(frame.Time, p["rate"]);

        if (!ep.Active)
        {
            _lastId = NoEpisode;
            _mix = 0f;
            _zoneLo = _zoneHi = 0;
            _stateTex = inputTex;
            base.Apply(inputTex, output, frame, p);
            return;
        }

        bool fresh = ep.Id != _lastId;
        _lastId = ep.Id;

        int sel = (int)MathF.Round(p["dir"]);
        int dir = sel >= 0 && sel <= 3 ? sel : ep.Dir;
        _horiz = dir >= 2 ? 1 : 0;
        _desc = dir == 0 || dir == 3 ? 1 : 0;
        _span = (int)MathF.Round(p["span"]);
        _lo = p["threshold"];
        float intensity = p["intensity"];
        _len = _horiz == 1 ? Width : Height;            // длина оси сортировки
        int lines = _horiz == 1 ? Height : Width;        // число строк (столбцов)

        float ramp = MathF.Min(1f, ep.Tau / RampSec);
        _mix = MathF.Min(1f, (ep.Dur - ep.Tau) / FadeSec);
        int zoneLen = (int)MathF.Round(intensity * lines * ramp);
        int room = lines - zoneLen;
        _zoneLo = Math.Min(room, (int)(Hash01(unchecked((uint)ep.Seed) ^ 0x7u) * (room + 1)));
        _zoneHi = _zoneLo + zoneLen;
        int passes = (int)MathF.Round(Passes * ramp * (0.5f + 0.5f * intensity));

        // 1. Сброс пикселей, чей вход заметно изменился (всё, если эпизод начался).
        var dst = Other();
        ResetPass(inputTex, _cur!, dst, fresh);
        _cur = dst;

        // 2. Копия входа: яркость для следующего кадра.
        CopyPass(inputTex, _prevIn!);

        // 3. Начало отрезка каждого пикселя: префиксный максимум по индексам границ маски (log2 шагов).
        dst = Other();
        StartPass(_cur!, dst);
        _cur = dst;
        for (int step = 1; step < _len; step <<= 2)
        {
            dst = Other();
            ScanPass(_cur!, dst, step);
            _cur = dst;
        }

        // 4. Проходы сортировки, ping-pong.
        for (int k = 0; k < passes; k++)
        {
            dst = Other();
            SortPass(_cur!, dst, _phase);
            _phase ^= 1;
            _cur = dst;
        }

        // 5. Смешивание с входом по зоне и силе эпизода.
        _stateTex = _cur!.Texture;
        base.Apply(inputTex, output, frame, p);
    }

    private RenderTarget Other() => _cur == _stateA ? _stateB! : _stateA!;

    private void BindTex(int unit, uint tex)
    {
        Gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
        Gl.BindTexture(TextureTarget.Texture2D, tex);
    }

    private void ResetPass(uint inputTex, RenderTarget src, RenderTarget dst, bool fresh)
    {
        var pr = _reset!;
        dst.Bind();
        Gl.Disable(EnableCap.Blend);
        Gl.UseProgram(pr.Id);
        BindTex(0, inputTex);
        BindTex(1, src.Texture);
        BindTex(2, _prevIn!.Texture);
        Gl.Uniform1(pr.Loc("uInput"), 0);
        Gl.Uniform1(pr.Loc("uPrev"), 1);
        Gl.Uniform1(pr.Loc("uPrevIn"), 2);
        Gl.Uniform1(pr.Loc("uFresh"), fresh ? 1 : 0);
        GlUtil.DrawFullscreen(Gl);
        Gl.ActiveTexture(TextureUnit.Texture0);
    }

    private void CopyPass(uint inputTex, RenderTarget dst)
    {
        var pr = _copy!;
        Gl.UseProgram(pr.Id);
        BindTex(0, inputTex);
        Gl.Uniform1(pr.Loc("uInput"), 0);
        dst.Bind();
        Gl.Disable(EnableCap.Blend);
        GlUtil.DrawFullscreen(Gl);
        Gl.ActiveTexture(TextureUnit.Texture0);
    }

    private void StartPass(RenderTarget src, RenderTarget dst)
    {
        var pr = _start!;
        Gl.UseProgram(pr.Id);
        BindTex(0, src.Texture);
        Gl.Uniform1(pr.Loc("uPrev"), 0);
        Gl.Uniform1(pr.Loc("uHoriz"), _horiz);
        Gl.Uniform1(pr.Loc("uLo"), _lo);
        dst.Bind();
        Gl.Disable(EnableCap.Blend);
        GlUtil.DrawFullscreen(Gl);
        Gl.ActiveTexture(TextureUnit.Texture0);
    }

    private void ScanPass(RenderTarget src, RenderTarget dst, int step)
    {
        var pr = _scan!;
        Gl.UseProgram(pr.Id);
        BindTex(0, src.Texture);
        Gl.Uniform1(pr.Loc("uPrev"), 0);
        Gl.Uniform1(pr.Loc("uHoriz"), _horiz);
        Gl.Uniform1(pr.Loc("uStep"), step);
        dst.Bind();
        Gl.Disable(EnableCap.Blend);
        GlUtil.DrawFullscreen(Gl);
        Gl.ActiveTexture(TextureUnit.Texture0);
    }

    private void SortPass(RenderTarget src, RenderTarget dst, int phase)
    {
        var pr = _sort!;
        Gl.UseProgram(pr.Id);
        BindTex(0, src.Texture);
        Gl.Uniform1(pr.Loc("uPrev"), 0);
        Gl.Uniform1(pr.Loc("uHoriz"), _horiz);
        Gl.Uniform1(pr.Loc("uDesc"), _desc);
        Gl.Uniform1(pr.Loc("uPhase"), phase);
        Gl.Uniform1(pr.Loc("uLen"), _len);
        Gl.Uniform1(pr.Loc("uSpan"), _span);
        Gl.Uniform1(pr.Loc("uLo"), _lo);
        Gl.Uniform1(pr.Loc("uZoneLo"), _zoneLo);
        Gl.Uniform1(pr.Loc("uZoneHi"), _zoneHi);
        dst.Bind();
        Gl.Disable(EnableCap.Blend);
        GlUtil.DrawFullscreen(Gl);
        Gl.ActiveTexture(TextureUnit.Texture0);
    }

    protected override void BeforeDraw(in FrameInfo frame, FilterParams p)
    {
        Gl.ActiveTexture(TextureUnit.Texture1);
        Gl.BindTexture(TextureTarget.Texture2D, _stateTex);
        SetInt("uState", 1);
        SetInt("uHoriz", _horiz);
        SetInt("uZoneLo", _zoneLo);
        SetInt("uZoneHi", _zoneHi);
        SetFloat("uMix", _mix);
        Gl.ActiveTexture(TextureUnit.Texture0);
    }

    /// <summary>
    /// Расписание: слот 8 с; хеш слота решает, есть ли эпизод (вероятность rate/60 × 8 на слот),
    /// длительность 2–6 с, начало — хеш внутри слота. Детерминировано от t.
    /// </summary>
    internal static Episode Schedule(float t, float rate)
    {
        long k = (long)Math.Floor(t / SlotSec);
        uint h = Hash(unchecked((uint)k) ^ 0x5bd1e995u);
        if (!(Hash01(h) < rate / 60f * SlotSec))
            return new Episode(false, 0f, 0f, NoEpisode, 0, 0);
        float dur = MinDur + DurRange * Hash01(h ^ 0x2u);
        float off = (SlotSec - dur) * Hash01(h ^ 0x3u);
        float tau = t - (k * SlotSec + off);
        int dir = Math.Min(3, (int)(Hash01(h ^ 0x1u) * 4f));
        return new Episode(tau >= 0f && tau < dur, tau, dur, k, dir, unchecked((int)h));
    }

    private static Episode ForcedEpisode(float phase)
    {
        uint h = Hash(0xABCDu);
        int dir = Math.Min(3, (int)(Hash01(h ^ 0x1u) * 4f));
        return new Episode(true, phase * ForceDur, ForceDur, ForcedId, dir, unchecked((int)h));
    }

    /// <summary>Хеш целого (как в шейдере), для детерминированного расписания.</summary>
    internal static uint Hash(uint x)
    {
        unchecked
        {
            x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16;
        }
        return x;
    }

    private static float Hash01(uint x) => Hash(x) * (1f / 4294967296f);

    public override void Dispose()
    {
        _stateA?.Dispose();
        _stateB?.Dispose();
        _prevIn?.Dispose();
        _stateA = null;
        _stateB = null;
        _prevIn = null;
        _cur = null;
        _reset?.Dispose();
        _copy?.Dispose();
        _start?.Dispose();
        _scan?.Dispose();
        _sort?.Dispose();
        _reset = _copy = _start = _scan = _sort = null;
        base.Dispose();
    }
}
