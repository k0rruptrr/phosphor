using System.Globalization;
using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// Вращающийся 3D-тор из символов в духе donut.c (Andy Sloane, a1k0n.net/2011/07/20/donut-math.html).
/// Z-буфер по 1/z, освещение по нормали, яркость → символ рампы.
/// </summary>
public sealed class TorusScene : IScene, IAudioReactive, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec RateXSpec = new("rateX", SceneParamKind.Float, 0.9f, 0f, 4f, "вращение вокруг X, рад/с");
    public static readonly SceneParamSpec RateZSpec = new("rateZ", SceneParamKind.Float, 0.5f, 0f, 4f, "вращение вокруг Z, рад/с");
    private static readonly SceneParamSpec[] ParamList = { RateXSpec, RateZSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    private const float R1 = 1f;
    private const float R2Base = 2f;
    private const float K2 = 5f;

    /// <summary>Вертикальная проекция: ячейка 10×18 px, поэтому вертикаль умножается на 0.55.</summary>
    private const float YScale = 0.55f;

    /// <summary>Доля меньшей стороны экрана, которую занимает тор.</summary>
    private const float FillFraction = 0.7f;

    /// <summary>Максимальный экранный шаг между соседними выборками, в ячейках.</summary>
    private const float MaxSpacing = 1.0f;

    /// <summary>Максимальный шаг по φ, рад (ограничение, чтобы не пропускать узкие видимые дуги).</summary>
    private const float MaxPhiStep = 0.3f;


    private const float TwoPi = MathF.PI * 2f;
    private const float TransitionTime = 1.5f;   // «спотыкание»: плавная смена скоростей
    private const float BreathTime = 3f;         // «дыхание» R2: длительность цикла
    private const float BreathAmount = 0.15f;
    private const float BeatDuration = 0.2f;
    private const float BeatLift = 0.3f;
    private const float InvSqrt2 = 0.70710678f;
    private const float BassSmoothing = 8f;
    private const float KickSpin = 8f;       // рад/с: кик вращения в момент удара…
    private const float KickTau = 0.06f;     // …затухающий с этим временем
    private const float PumpAmount = 0.6f;   // «пульс» R2 на ударе: +60%, затухает быстро
    private const float PumpTau = 0.08f;

    /// <summary>Сдвиг для быстрого floor через (int): координаты заведомо > -Offset.</summary>
    private const float FloorOffset = 1024f;

    /// <summary>Число уровней яркости для таблицы цветов.</summary>
    private const int Levels = 64;

    /// <summary>Размер таблицы тригонометрии по φ (степень двойки не обязательна).</summary>
    private const int PhiTable = 4096;

    /// <summary>Число оттенков для rainbow (φ-индекс >> 4).</summary>
    private const int Hues = 256;

    private const string Ramp = ".,-~:;=!*#$@";
    private const string Caption = "TORUS R1=1.00 R2=";

    private static readonly Rgba DarkTurquoise = Rgba.Hex(0x008B8B);

    private static readonly float[] CosTab = BuildCos();
    private static readonly float[] SinTab = BuildSin();

    private enum ColorMode { Gradient, Fixed, Rainbow }

    public string Id => "torus";

    private int _cols, _rows;
    private Random _rng = new(0);
    private ColorMode _mode;
    private Rgba _caption;
    private Rgba[] _fg = Array.Empty<Rgba>();   // [hue * Levels + level], для Rainbow — по hue, иначе только по level

    private float _k1, _ky;
    private int _nTheta;
    private float[] _cosT = Array.Empty<float>(), _sinT = Array.Empty<float>();

    // Буферы кадра: выделяются в Init, Draw только перезаписывает.
    private float[] _zbuf = Array.Empty<float>();
    private float[] _lum = Array.Empty<float>();
    private int[] _tag = Array.Empty<int>();

    // Состояние вращения.
    private float _baseRateA, _baseRateB;
    private float _rateA, _rateB;     // текущие скорости, рад/с
    private float _angA, _angB;       // текущие углы, рад, в [0, 2π)
    private float _fromA, _fromB, _toA, _toB, _transT;
    private float _nextStumble;
    private float _breathT;
    private float _r2 = R2Base;

    // Звук.
    private float _bassTarget, _bass, _beatLeft;
    private float _spinKick;                 // добавка к скорости вращения после удара, рад/с
    private float _pump;                     // пульс R2 после удара: 1 в момент удара, затухает к 0
    private int _spinSign = 1;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = cols;
        _rows = rows;
        _rng = rng;

        string pal = (options.Palette ?? "default").ToLowerInvariant();
        Rgba fixedColor = Rgba.Black;
        switch (pal)
        {
            case "green":
            case "amber":
            case "mono":
                _mode = ColorMode.Fixed;
                fixedColor = Palettes.Base(pal);
                _caption = fixedColor.Scale(0.45f);
                break;
            case "rainbow":
                _mode = ColorMode.Rainbow;
                _caption = DarkTurquoise.Scale(0.45f);
                break;
            default:
                _mode = ColorMode.Gradient;
                _caption = DarkTurquoise.Scale(0.45f);
                break;
        }

        // Масштаб: тор ≈ FillFraction меньшей стороны. Сторона в пикселях: ячейка 10×18.
        float side = MathF.Min(cols * 10f, rows * 18f);
        _k1 = FillFraction * side / 13f;
        _ky = YScale * _k1;

        // Шаг по θ (вокруг трубы): фиксированный, по худшему случаю — ближний край, ooz = 0.5.
        float oozMax = 1f / (K2 - R2Base - R1);
        float dTheta = MaxSpacing / (_k1 * oozMax * R1);
        _nTheta = Math.Clamp((int)MathF.Ceiling(TwoPi / dTheta), 24, 800);

        _cosT = new float[_nTheta];
        _sinT = new float[_nTheta];
        for (int i = 0; i < _nTheta; i++)
        {
            float t = TwoPi * i / _nTheta;
            _cosT[i] = MathF.Cos(t);
            _sinT[i] = MathF.Sin(t);
        }

        // Таблица цветов по уровням яркости (L = level / (Levels-1)), с учётом затемнения 0.35 + 0.65 L.
        int hues = _mode == ColorMode.Rainbow ? Hues : 1;
        _fg = new Rgba[hues * Levels];
        for (int h = 0; h < hues; h++)
        {
            Rgba hue = _mode == ColorMode.Rainbow ? Rgba.Hsv(h * 360f / Hues, 0.8f, 1f) : Rgba.Black;
            for (int q = 0; q < Levels; q++)
            {
                float l = q / (float)(Levels - 1);
                Rgba baseColor = _mode switch
                {
                    ColorMode.Gradient => Rgba.Lerp(DarkTurquoise, Rgba.White, l),
                    ColorMode.Rainbow => hue,
                    _ => fixedColor,
                };
                _fg[h * Levels + q] = baseColor.Scale(0.35f + 0.65f * l);
            }
        }

        int cells = cols * rows;
        _zbuf = new float[cells];
        _lum = new float[cells];
        _tag = new int[cells];

        float speed = MathF.Max(0f, options.Speed);
        _baseRateA = Math.Clamp(options.GetFloat(RateXSpec.Name, RateXSpec.Default), RateXSpec.Min, RateXSpec.Max) * speed;
        _baseRateB = Math.Clamp(options.GetFloat(RateZSpec.Name, RateZSpec.Default), RateZSpec.Min, RateZSpec.Max) * speed;
        _rateA = _toA = _fromA = _baseRateA;
        _rateB = _toB = _fromB = _baseRateB;
        _transT = TransitionTime;

        _angA = rng.NextSingle() * TwoPi;
        _angB = rng.NextSingle() * TwoPi;
        _nextStumble = 40f + rng.NextSingle() * 30f;
        _breathT = BreathTime;
        _r2 = R2Base;

        _bassTarget = 0f;
        _bass = 0f;
        _beatLeft = 0f;
        _spinKick = 0f;
        _spinSign = 1;
        _pump = 0f;
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;

        _bass += (_bassTarget - _bass) * (1f - MathF.Exp(-BassSmoothing * dt));
        _beatLeft = MathF.Max(0f, _beatLeft - dt);
        _spinKick *= MathF.Exp(-dt / KickTau);
        _pump *= MathF.Exp(-dt / PumpTau);

        _nextStumble -= dt;
        if (_nextStumble <= 0f) StartStumble();

        if (_transT < TransitionTime)
        {
            _transT = MathF.Min(TransitionTime, _transT + dt);
            float k = Smooth(_transT / TransitionTime);
            _rateA = _fromA + (_toA - _fromA) * k;
            _rateB = _fromB + (_toB - _fromB) * k;
        }

        _angA = Wrap(_angA + (_rateA + _spinKick) * dt);
        _angB = Wrap(_angB + (_rateB + _spinKick) * dt);

        if (_breathT < BreathTime) _breathT += dt;
        float breath = _breathT < BreathTime
            ? 1f + BreathAmount * MathF.Sin(TwoPi * _breathT / BreathTime)
            : 1f;
        _r2 = R2Base * breath * (1f + BreathAmount * _bass) * (1f + PumpAmount * _pump);
    }

    public void Draw(CharGrid grid)
    {
        grid.Clear(Rgba.Black);
        Array.Fill(_zbuf, 0f);

        int cols = _cols, rows = _rows;
        float cx = cols * 0.5f + FloorOffset, cy = rows * 0.5f + FloorOffset;
        float k1 = _k1, ky = _ky;

        // Поворот M = Rz(B) * Rx(A). Столбцы M — образы базисных векторов.
        float sa = MathF.Sin(_angA), ca = MathF.Cos(_angA);
        float sb = MathF.Sin(_angB), cb = MathF.Cos(_angB);
        float exX = cb, exY = sb, exZ = 0f;
        float eyX = -sb * ca, eyY = cb * ca, eyZ = sa;
        float ezX = sb * sa, ezY = -cb * sa, ezZ = ca;

        float[] zbuf = _zbuf, lum = _lum;
        int[] tag = _tag;
        float[] cosT = CosTab, sinT = SinTab;

        // Адаптивный шаг по φ: экранный шаг ≈ K1 · ooz · r · dφ ≤ MaxSpacing  →  dφ = MaxSpacing / (K1 · ooz · r).
        // В единицах таблицы: dIdx = dφ · PhiTable / 2π.
        float stepScale = MaxSpacing * PhiTable / (TwoPi * k1);
        float maxIdx = MaxPhiStep * PhiTable / TwoPi;

        for (int i = 0; i < _nTheta; i++)
        {
            float ct = _cosT[i], st = _sinT[i];
            float r = _r2 + R1 * ct;   // расстояние от оси тора до точки трубы
            float rs = R1 * st;        // высота точки трубы над плоскостью тора
            float invR = 1f / r;

            // Коэффициенты при cos(φ) и sin(φ) и постоянные части — на весь ряд φ.
            float ax = r * exX, ay = r * exY, az = r * exZ;
            float bx = r * ezX, by = r * ezY, bz = r * ezZ;
            float ox = rs * eyX, oy = rs * eyY, oz = rs * eyZ + K2;
            float pY = ct * exY, qY = ct * ezY, cY = st * eyY;
            float pZ = ct * exZ, qZ = ct * ezZ, cZ = st * eyZ;

            int p = 0;
            while (p < PhiTable)
            {
                float cp = CosTab[p], sp = SinTab[p];

                float vx = cp * ax + sp * bx + ox;
                float vy = cp * ay + sp * by + oy;
                float vz = cp * az + sp * bz + oz;
                float ooz = 1f / vz;

                // Шаг от ooz и радиуса текущей точки (в индексах таблицы), с ограничениями.
                float step = stepScale * vz * invR;   // 1/ooz = vz
                if (step < 1f) step = 1f;
                else if (step > maxIdx) step = maxIdx;

                float nZ = cp * pZ + sp * qZ + cZ;
                if (nZ < 0f)   // обратная сторона пропускается
                {
                    int x = (int)(cx + k1 * ooz * vx) - (int)FloorOffset;
                    int y = (int)(cy - ky * ooz * vy) - (int)FloorOffset;
                    if ((uint)x < (uint)cols && (uint)y < (uint)rows)
                    {
                        int idx = y * cols + x;
                        // Ранний выход: точка за z-буфером — освещение не считаем.
                        if (ooz > zbuf[idx])
                        {
                            zbuf[idx] = ooz;
                            float nY = cp * pY + sp * qY + cY;
                            float l = (nY - nZ) * InvSqrt2;
                            lum[idx] = l < 0f ? 0f : (l > 1f ? 1f : l);
                            tag[idx] = p >> 4;   // индекс оттенка для rainbow
                        }
                    }
                }

                p += (int)step;
            }
        }

        bool beat = _beatLeft > 0f;
        float lift = beat ? BeatLift : 0f;
        int rampBoost = beat ? 2 : 0;
        bool rainbow = _mode == ColorMode.Rainbow;
        Rgba[] fg = _fg;
        for (int idx = 0; idx < zbuf.Length; idx++)
        {
            if (zbuf[idx] <= 0f) continue;

            float l = lum[idx] + lift;
            if (l > 1f) l = 1f;
            int q = (int)(l * (Levels - 1) + 0.5f);
            // Ступень рампы по яркости уровня: 12 ступеней, +2 на ударе.
            int k = (q * Ramp.Length) / (Levels - 1) + rampBoost;
            if (k > Ramp.Length - 1) k = Ramp.Length - 1;
            int fi = rainbow ? tag[idx] * Levels + q : q;
            grid.Cells[idx] = new Cell(Ramp[k], fg[fi], Rgba.Black);
        }

        // Техническая подпись: тусклая, строка rows-2, отступ 2 от левого края.
        if (rows >= 10)
        {
            Span<char> buf = stackalloc char[64];
            int n = Append(buf, 0, Caption);
            n = AppendF2(buf, n, _r2);
            n = Append(buf, n, " A=");
            n = AppendF2(buf, n, _angA);
            n = Append(buf, n, " B=");
            n = AppendF2(buf, n, _angB);
            for (int i = 0; i < n; i++) grid.Put(2 + i, rows - 2, buf[i], _caption);
        }
    }

    public void OnAudio(in AudioFrame audio)
    {
        if (!audio.Active)
        {
            _bassTarget = 0f;
            _spinKick = 0f;
            _pump = 0f;
            return;
        }
        _bassTarget = Math.Clamp(audio.Bass, 0f, 1f);
        if (!audio.Beat) return;
        _beatLeft = BeatDuration;
        _spinSign = -_spinSign;
        _spinKick = KickSpin * _spinSign;
        _pump = 1f;
    }

    private void StartStumble()
    {
        _fromA = _rateA;
        _fromB = _rateB;
        _toA = _baseRateA * RandomFactor();
        _toB = _baseRateB * RandomFactor();
        _transT = 0f;
        _breathT = 0f;
        _nextStumble = 40f + _rng.NextSingle() * 30f;
    }

    private float RandomFactor() => 0.5f + 1.5f * _rng.NextSingle();

    private static float Wrap(float a)
    {
        a %= TwoPi;
        return a < 0f ? a + TwoPi : a;
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);

    private static float[] BuildCos()
    {
        var t = new float[PhiTable];
        for (int i = 0; i < PhiTable; i++) t[i] = MathF.Cos(TwoPi * i / PhiTable);
        return t;
    }

    private static float[] BuildSin()
    {
        var t = new float[PhiTable];
        for (int i = 0; i < PhiTable; i++) t[i] = MathF.Sin(TwoPi * i / PhiTable);
        return t;
    }

    private static int Append(Span<char> buf, int pos, string s)
    {
        s.AsSpan().CopyTo(buf.Slice(pos));
        return pos + s.Length;
    }

    private static int AppendF2(Span<char> buf, int pos, float v)
    {
        v.TryFormat(buf.Slice(pos), out int written, "F2", CultureInfo.InvariantCulture);
        return pos + written;
    }
}
