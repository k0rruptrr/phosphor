using System.Runtime.CompilerServices;
using Phosphor.Core;

// Внутренние хуки сцены для тестов. В изолированной сборке (OnlyScene) DroneScene.cs не компилируется,
// поэтому атрибут нужен здесь.
[assembly: InternalsVisibleTo("Phosphor.Scenes.Tests")]

namespace Phosphor.Scenes;

/// <summary>
/// Лава-лампа: колба-ракета по центру (около 80% высоты), на дне всегда лежит лужа воска, от неё отрываются
/// 3–6 капель разного размера: греются у лужи, всплывают, наверху остывают и тонут обратно.
/// Жидкость подсвечена снизу, на стекле блик; вокруг лампы мягкий ореол цвета жидкости.
/// Пары «воск в жидкости» (красный в жёлтой, синий в фиолетовой, зелёный в голубой): фиксированная или смена по кругу раз в 3–5 мин.
/// Звук: бас подогревает (капли всплывают быстрее), удар — лёгкая пульсация подсветки.
/// </summary>
public sealed class LavalampScene : IScene, IAudioReactive, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec BlobsSpec = new("blobs", SceneParamKind.Int, 5f, 3f, 6f, "число капель от лужи");
    public static readonly SceneParamSpec HeatSpec = new("heat", SceneParamKind.Float, 1f, 0.2f, 2f, "скорость конвекции");
    public static readonly SceneParamSpec SchemeSpec = new("scheme", SceneParamKind.Choice, 3f, 0f, 3f, "цветовая пара",
        new[] { "red", "blue", "green", "cycle" });
    private static readonly SceneParamSpec[] ParamList = { BlobsSpec, HeatSpec, SchemeSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    public string Id => "lavalamp";

    // Геометрия в условных единицах: высота лампы = 1 и занимает LampShare высоты холста, 0 — верх крышки.
    // Пропорции классической лампы (Mathmos): крышка 12% H, колба 58% H, основание 30% H.
    private const float LampShare = 0.8f;
    private const float CapEnd = 0.12f;         // низ крышки = верх колбы
    private const float FlaskBottom = 0.70f;    // низ колбы = верх основания (стык)
    private const float CapTopHalf = 0.06f;     // крышка — усечённый конус, плоский верх, шире снизу
    private const float CapBottomHalf = 0.085f;
    private const float JunctionHalf = 0.17f;   // полуширина стыка колбы и основания
    private const float PeakHalf = 0.2f;        // самая широкая часть колбы
    private const float TopHalf = 0.085f;       // верх колбы ~40% максимальной ширины
    private const float PeakS = 0.22f;         // где колба самая широкая: доля высоты колбы от стыка
    private const float BaseTopHalf = JunctionHalf;  // основание — усечённый конус, шире внизу
    private const float BaseBottomHalf = 0.275f;     // низ основания ~0.55 H в ширину

    private const float PoolH = 0.05f;          // высота лужи воска на дне
    private const float PoolSurfY = FlaskBottom - PoolH;
    private const float WaveAmp = 0.008f;        // волнение поверхности лужи
    private const float PoolEdge = 0.015f;       // ширина мягкого края лужи
    private const float TableY = 1f;             // линия стола — низ основания

    private const float HeatZone = 0.60f;        // ниже этой отметки капля греется (у лужи)
    private const float CoolZone = 0.25f;        // выше этой отметки капля остывает
    private const float Buoyancy = 0.3f;         // сила плавучести на (T − 0.5)
    private const float Drag = 2f;               // вязкость
    private const float TickDt = 1f / 50f;       // физика на 50 Гц
    private const float GlowAmount = 0.22f;      // яркость ореола у лампы (доля цвета жидкости)
    private const float GlowRadius = 0.87f;      // радиус ореола, ~1.5 высоты колбы
    private const float PairTime = 8f;           // плавная смена пары, с
    private const int MaxBlobs = 6;

    private static readonly Rgba Room = new(8, 7, 12);
    private static readonly Rgba Glass = new(210, 215, 235);
    private static readonly int[] Cycle = { 1, 2, 0 };   // синяя → зелёная → красная

    /// <summary>Цветовая пара: жидкость сверху и снизу, воск и его горячее ядро.</summary>
    private readonly record struct Pal(Rgba LiqTop, Rgba LiqBot, Rgba Wax, Rgba Hot)
    {
        public static Pal Mix(Pal a, Pal b, float t) => new(
            Rgba.Lerp(a.LiqTop, b.LiqTop, t), Rgba.Lerp(a.LiqBot, b.LiqBot, t),
            Rgba.Lerp(a.Wax, b.Wax, t), Rgba.Lerp(a.Hot, b.Hot, t));
    }

    // Индексы совпадают со значениями scheme: 0 red, 1 blue, 2 green.
    private static readonly Pal[] Pairs =
    {
        new(new Rgba(235, 170, 40), new Rgba(255, 205, 70), new Rgba(210, 25, 20), new Rgba(255, 120, 80)),
        new(new Rgba(80, 30, 130), new Rgba(120, 50, 170), new Rgba(30, 80, 235), new Rgba(90, 140, 255)),
        new(new Rgba(40, 120, 170), new Rgba(80, 180, 220), new Rgba(40, 210, 90), new Rgba(170, 255, 190)),
    };

    private RetroCanvas _canvas = null!;
    private RetroClock _clock = new(50f);
    private Random _rng = new(0);
    private float _speed = 1f, _heat = 1f;
    private bool _cycle;
    private int _seqPos, _prevIdx, _curIdx;
    private float _switchT, _nextSwitch;

    private int _w, _h;
    private float _s;      // пикселей холста на единицу высоты лампы
    private float _top;    // верх лампы в пикселях холста
    private float _t;      // время сцены, для волнения лужи и дыхания ореола

    private int _n;
    private readonly float[] _bx = new float[MaxBlobs], _by = new float[MaxBlobs];
    private readonly float[] _bvx = new float[MaxBlobs], _bvy = new float[MaxBlobs];
    private readonly float[] _br = new float[MaxBlobs], _bR = new float[MaxBlobs];   // базовый и текущий радиус
    private readonly float[] _bt = new float[MaxBlobs], _bph = new float[MaxBlobs];  // температура, фаза дрейфа
    private bool[] _wax = Array.Empty<bool>();          // пиксели воска (для проверки внутри колбы)
    private float[] _glowTab = Array.Empty<float>();    // спад ореола по пикселям (без пульсации)

    private float _bass, _tBass, _beat;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _rng = rng;
        _speed = MathF.Max(0f, options.Speed);
        _heat = Math.Clamp(options.GetFloat(HeatSpec.Name, HeatSpec.Default), HeatSpec.Min, HeatSpec.Max);
        int drops = Math.Clamp(options.GetInt(BlobsSpec.Name, (int)BlobsSpec.Default), (int)BlobsSpec.Min, (int)BlobsSpec.Max);

        int scheme = Array.IndexOf(SchemeSpec.Options!, options.GetString(SchemeSpec.Name, SchemeSpec.Options![(int)SchemeSpec.Default]));
        if (scheme < 0) scheme = (int)SchemeSpec.Default;
        _cycle = scheme == 3;
        _seqPos = 0;
        _prevIdx = _curIdx = _cycle ? Cycle[0] : scheme;
        _switchT = 0f;
        _nextSwitch = 180f + 120f * (float)_rng.NextDouble();

        _canvas = new RetroCanvas(cols, rows, CanvasMode.HalfBlock);
        _clock = new RetroClock(50f);
        _w = _canvas.Width;
        _h = _canvas.Height;
        _s = LampShare * _h;
        _top = (1f - LampShare) * 0.5f * _h;
        _t = 0f;
        _wax = new bool[_w * _h];
        _glowTab = new float[_w * _h];
        for (int py = 0; py < _h; py++)
        for (int x = 0; x < _w; x++)
        {
            float xu = (x + 0.5f - _w * 0.5f) / _s;
            float yu = (py + 0.5f - _top) / _s;
            float d = MathF.Sqrt(xu * xu + (yu - 0.5f) * (yu - 0.5f));
            float k = d < GlowRadius ? 1f - d / GlowRadius : 0f;
            _glowTab[py * _w + x] = k * k;              // плавный спад к нулю на радиусе ореола
        }

        _n = drops;
        for (int i = 0; i < _n; i++)
        {
            _br[i] = 0.024f + 0.018f * (float)_rng.NextDouble();    // капли разного размера
            _bph[i] = (float)(_rng.NextDouble() * MathF.Tau);
            _bx[i] = (float)(_rng.NextDouble() * 2 - 1) * 0.12f;
            _by[i] = 0.3f + 0.3f * (float)_rng.NextDouble();
            _bvx[i] = _bvy[i] = 0f;
            _bt[i] = 0.5f + 0.5f * (float)_rng.NextDouble();
            _bR[i] = _br[i] * (0.8f + 0.4f * _bt[i]);
        }

        _bass = _tBass = _beat = 0f;
        Render();
    }

    public void OnAudio(in AudioFrame audio)
    {
        _tBass = audio.Active ? audio.Bass : 0f;
        _beat = audio.Active ? audio.BeatPulse : 0f;
    }

    public void Update(float dt)
    {
        dt = Math.Clamp(dt, 0f, 0.1f);
        _bass += (_tBass - _bass) * (1f - MathF.Exp(-dt * 12f));

        int ticks = _clock.Advance(dt * _speed);
        for (int i = 0; i < ticks; i++) Step(TickDt, _clock.Time);

        UpdateScheme();
        Render();
    }

    public void Draw(CharGrid grid) => _canvas.Present(grid);

    /// <summary>Физика капель: нагрев у лужи и остывание наверху, плавучесть, вязкость, стенки колбы, мягкое расхождение.
    /// Лужа на дне неподвижна; капля, опустившаяся на неё, садится на поверхность и снова греется.</summary>
    private void Step(float dt, float t)
    {
        float heatK = _heat * (1f + 0.8f * _bass);          // бас подогревает
        float fHeat = 1f - MathF.Exp(-0.35f * heatK * dt);
        float fCool = 1f - MathF.Exp(-0.3f * _heat * dt);
        float dropBottom = PoolSurfY;

        for (int i = 0; i < _n; i++)
        {
            float y = _by[i];
            float T = _bt[i];
            if (y > HeatZone) T += (1f - T) * fHeat;
            else if (y < CoolZone) T -= T * fCool;
            _bt[i] = T;

            _bR[i] = _br[i] * (0.8f + 0.4f * T);            // горячая капля раздувается

            float force = -(T - 0.5f) * Buoyancy * heatK;   // горячее всплывает, холодное тонет
            _bvy[i] += (force - Drag * _bvy[i]) * dt;
            _by[i] += _bvy[i] * dt;

            float wander = MathF.Sin(t * 0.4f + _bph[i]) * 0.04f * _heat;
            _bvx[i] += (wander - Drag * _bvx[i]) * dt;
            _bx[i] += _bvx[i] * dt;
        }

        // Сильно перекрывшиеся капли раздвигаются: шейка рвётся, капли делятся.
        for (int i = 0; i < _n; i++)
        for (int j = i + 1; j < _n; j++)
        {
            float dx = _bx[j] - _bx[i], dy = _by[j] - _by[i];
            float d2 = dx * dx + dy * dy;
            float min = (_bR[i] + _bR[j]) * 0.75f;
            if (d2 >= min * min || d2 < 1e-10f) continue;
            float d = MathF.Sqrt(d2);
            float push = (min - d) * 0.15f / d;
            _bx[i] -= dx * push; _by[i] -= dy * push;
            _bx[j] += dx * push; _by[j] += dy * push;
        }

        // Стенки колбы; снизу капля не проходит сквозь лужу.
        for (int i = 0; i < _n; i++)
        {
            float R = _bR[i];
            float yMin = CapEnd + R * 0.7f, yMax = dropBottom - R * 0.35f;
            if (_by[i] < yMin) { _by[i] = yMin; _bvy[i] = 0f; }
            else if (_by[i] > yMax) { _by[i] = yMax; _bvy[i] = 0f; }

            float half = MathF.Max(0f, HalfWidth(_by[i]) - R * 0.7f);
            if (_bx[i] > half) { _bx[i] = half; _bvx[i] = 0f; }
            else if (_bx[i] < -half) { _bx[i] = -half; _bvx[i] = 0f; }
        }
    }

    /// <summary>Смена пары в режиме cycle: раз в 3–5 мин, плавный переход в PairTime секунд.</summary>
    private void UpdateScheme()
    {
        if (!_cycle) return;
        float t = _clock.Time;
        if (t < _nextSwitch) return;
        _seqPos = (_seqPos + 1) % Cycle.Length;
        _prevIdx = _curIdx;
        _curIdx = Cycle[_seqPos];
        _switchT = t;
        _nextSwitch = t + 180f + 120f * (float)_rng.NextDouble();
    }

    private void Render()
    {
        float t = _clock.Time;
        _t = t;
        float k = Math.Clamp((t - _switchT) / PairTime, 0f, 1f);
        k = k * k * (3f - 2f * k);
        var pal = Pal.Mix(Pairs[_prevIdx], Pairs[_curIdx], k);
        var glowCol = Rgba.Lerp(pal.LiqTop, pal.LiqBot, 0.5f);
        // Ореол дышит вместе с теплом; удар — лёгкая пульсация.
        float glowK = GlowAmount * (1f + 0.12f * MathF.Sin(t * 0.5f)) * (1f + 0.3f * _beat);
        float litK = 1f + 0.12f * _beat;

        var px = _canvas.Pixels;
        int w = _w;
        float inv = 1f / _s;
        float cx = w * 0.5f;
        for (int py = 0; py < _h; py++)
        {
            float yu = (py + 0.5f - _top) * inv;
            int row = py * w;
            for (int x = 0; x < w; x++)
            {
                float xu = (x + 0.5f - cx) * inv;
                float g = glowK * _glowTab[row + x];
                // Комната: Room + (glowCol − Room)·g, инлайн — большинство пикселей вне лампы.
                var room = new Rgba((byte)(Room.R + (glowCol.R - Room.R) * g + 0.5f),
                    (byte)(Room.G + (glowCol.G - Room.G) * g + 0.5f),
                    (byte)(Room.B + (glowCol.B - Room.B) * g + 0.5f));
                if (yu > TableY)
                {
                    px[row + x] = Table(xu, yu, room, glowCol, glowK, inv);
                    _wax[row + x] = false;
                    continue;
                }
                if (yu < 0f || MathF.Abs(xu) > BaseBottomHalf + 2f * inv)
                {
                    px[row + x] = room;
                    _wax[row + x] = false;
                    continue;
                }
                px[row + x] = Pixel(xu, yu, room, pal, litK, inv, out bool wax);
                _wax[row + x] = wax;
            }
        }
    }

    private Rgba Pixel(float xu, float yu, Rgba room, Pal pal, float litK, float inv, out bool wax)
    {
        wax = false;
        if (yu < 0f || yu > TableY) return room;
        float ax = MathF.Abs(xu);
        if (yu < CapEnd)
        {
            float ch = CapHalfAt(yu);
            return ax <= ch ? Metal(ax / ch) : room;
        }
        if (yu <= FlaskBottom)
        {
            float hw = HalfWidth(yu);
            if (ax <= hw) return Interior(xu, yu, hw, pal, litK, out wax);
            // Тонкий контур стекла в 1–2 пикселя.
            return ax <= hw + 1.5f * inv ? Rgba.Lerp(room, Glass, 0.35f) : room;
        }
        float hb = BaseTopHalf + (BaseBottomHalf - BaseTopHalf) * (yu - FlaskBottom) / (1f - FlaskBottom);
        if (ax > hb) return room;
        // Основание: металл, две тонкие риски вентиляции, тусклый отсвет лампы сверху.
        Rgba c = Metal(ax / hb);
        if (yu < FlaskBottom + 0.05f && ax < 0.12f)
            c = Rgba.Lerp(c, pal.LiqBot, 0.25f * (1f - ax / 0.12f));
        if (ax < hb * 0.6f && (MathF.Abs(yu - 0.78f) < 0.5f * inv || MathF.Abs(yu - 0.83f) < 0.5f * inv))
            c = Rgba.Lerp(c, new Rgba(20, 20, 24), 0.8f);
        return c;
    }

    /// <summary>Стол под лампой: тусклая линия через весь экран и пятно отсвета лампы на нём.</summary>
    private static Rgba Table(float xu, float yu, Rgba room, Rgba glowCol, float glowK, float inv)
    {
        if (MathF.Abs(yu - TableY) < 0.5f * inv) return new Rgba(46, 44, 52);
        float ex = xu / 0.45f, ey = (yu - TableY) / 0.09f;
        float e = ex * ex + ey * ey;
        float g = e < 1f ? 0.2f * glowK / GlowAmount * (1f - e) : 0f;
        return Rgba.Lerp(room, glowCol, g);
    }

    /// <summary>Жидкость с воском внутри колбы. Поле — лужа на дне плюс капли (сумма R²/d²); порог 1 — граница,
    /// три уровня у порога — мягкий край.</summary>
    private Rgba Interior(float xu, float yu, float hw, Pal pal, float litK, out bool wax)
    {
        float t01 = (yu - CapEnd) / (FlaskBottom - CapEnd);
        float r = xu / hw;
        float shade = 0.8f + 0.2f * (1f - r * r);            // цилиндр: края темнее
        // Жидкость темнее вверху и подсвечена снизу.
        var liq = Rgba.Lerp(pal.LiqTop, pal.LiqBot, t01).Scale(litK * (0.55f + 0.6f * t01) * shade);

        // Лужа: мягкая поверхность с волнением, ниже неё f ≥ 1.5.
        float surf = PoolSurfY + WaveAmp * MathF.Sin(_t * 1.1f + xu * 9f);
        float f = 1.5f * Math.Clamp((yu - surf) / PoolEdge + 0.5f, 0f, 1f);
        for (int i = 0; i < _n; i++)
        {
            float dx = xu - _bx[i], dy = yu - _by[i];
            float R = _bR[i];
            f += R * R / (dx * dx + dy * dy + 1e-6f);
        }

        Rgba c;
        wax = f >= 1f;
        if (wax)
            c = Rgba.Lerp(pal.Wax, pal.Hot, Math.Clamp((f - 1f) * 1.5f, 0f, 1f)).Scale(litK * shade);
        else if (f >= 0.85f) c = Rgba.Lerp(liq, pal.Wax, 0.66f);
        else if (f >= 0.7f) c = Rgba.Lerp(liq, pal.Wax, 0.33f);
        else c = liq;

        // Вертикальный блик на стекле у левого края.
        if (xu < 0f)
        {
            float bandD = MathF.Abs(xu + 0.78f * hw);
            if (bandD < 0.02f) c = Rgba.Lerp(c, Rgba.White, 0.35f * (1f - bandD / 0.02f));
        }
        return c;
    }

    /// <summary>
    /// Полуширина колбы на высоте y. s — доля высоты колбы от стыка (0 внизу, 1 вверху). От стыка колба расширяется
    /// до максимума на PeakS, затем плавно сужается до TopHalf. Обе ветки сходятся в PeakHalf без излома.
    /// </summary>
    private static float HalfWidth(float y)
    {
        float s = Math.Clamp((FlaskBottom - y) / (FlaskBottom - CapEnd), 0f, 1f);
        if (s <= PeakS)
            return JunctionHalf + (PeakHalf - JunctionHalf) * MathF.Sin(s / PeakS * MathF.PI * 0.5f);
        float u = (s - PeakS) / (1f - PeakS);
        return TopHalf + (PeakHalf - TopHalf) * MathF.Cos(u * MathF.PI * 0.5f);
    }

    /// <summary>Полуширина крышки на высоте y: короткий усечённый конус, шире снизу, верх плоский.</summary>
    private static float CapHalfAt(float y) =>
        CapTopHalf + (CapBottomHalf - CapTopHalf) * Math.Clamp(y / CapEnd, 0f, 1f);

    /// <summary>Металл крышки и основания: q = 0 в центре, 1 у края.</summary>
    private static Rgba Metal(float q)
    {
        float v = 40f + 150f * (1f - Math.Clamp(q * q, 0f, 1f));
        byte b = (byte)v;
        return new Rgba(b, b, (byte)(b + 6));
    }

    // Внутренние хуки для тестов.
    internal int BlobCount => _n;
    internal float BlobX(int i) => _bx[i];
    internal float BlobY(int i) => _by[i];

    /// <summary>Верх и низ колбы в условных единицах (высота лампы = 1).</summary>
    internal static (float Top, float Bottom) FlaskSpan => (CapEnd, FlaskBottom);

    /// <summary>Точка (в единицах лампы) внутри колбы.</summary>
    internal static bool InFlask(float xu, float yu) =>
        yu >= CapEnd && yu <= FlaskBottom && MathF.Abs(xu) <= HalfWidth(yu);

    /// <summary>Пиксели холста: воск внутри колбы, воск за контуром, всего пикселей колбы, воск в нижних 10% колбы.</summary>
    internal (int Inside, int Outside, int Flask, int BottomWax) WaxPixelCounts()
    {
        int inside = 0, outside = 0, flask = 0, bottom = 0;
        float bottomTop = FlaskBottom - 0.1f * (FlaskBottom - CapEnd);
        for (int py = 0; py < _h; py++)
        for (int x = 0; x < _w; x++)
        {
            float yu = (py + 0.5f - _top) / _s;
            float xu = (x + 0.5f - _w * 0.5f) / _s;
            bool isFlask = InFlask(xu, yu);
            if (isFlask) flask++;
            if (!_wax[py * _w + x]) continue;
            if (isFlask) inside++; else outside++;
            if (isFlask && yu >= bottomTop) bottom++;
        }
        return (inside, outside, flask, bottom);
    }
}
