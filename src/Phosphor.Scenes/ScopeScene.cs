using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// «Осциллограф»: луч в режиме XY рисует фигуры Лиссажу на сетке 8×10 делений. Луч — линии на подсетке
/// секстелей (2×3 на ячейку), с послесвечением (буфер яркости с затуханием persist). Скорость луча задаёт яркость:
/// медленные места ярче. Звук: басы — амплитуда X, середина — Y, высокие — дрожь, удар — вспышка и смена отношения
/// частот. Иногда (раз в 20–40 с) вместо Лиссажу луч рисует вращающийся куб (5–8 с).
/// </summary>
public sealed class ScopeScene : IScene, IAudioReactive, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec PersistSpec = new("persist", SceneParamKind.Float, 0.85f, 0.5f, 0.98f, "послесвечение луча");
    public static readonly SceneParamSpec TraceSpec = new("traceDots", SceneParamKind.Int, 1200f, 200f, 3000f, "точек луча за кадр");
    public static readonly SceneParamSpec FiguresSpec = new("figures", SceneParamKind.Bool, 1f, 0f, 1f, "фигуры из отрезков (куб) раз в 20–40 с");
    private static readonly SceneParamSpec[] ParamList = { PersistSpec, TraceSpec, FiguresSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    public string Id => "scope";

    /// <summary>Сейчас луч рисует фигуру (куб), а не Лиссажу. Для тестов.</summary>
    public bool ShowingFigure => _inFigure;

    /// <summary>Шаг деления сетки в подпикселях: горизонталь и вертикаль (для тестов квадратности делений).</summary>
    public (float Horizontal, float Vertical) Division => (_U, _V);

    // Отношения частот X:Y для Лиссажу (целые — фигура замкнута).
    private static readonly int[] RatioA = { 1, 2, 3, 5, 3, 4 };
    private static readonly int[] RatioB = { 2, 3, 4, 4, 2, 5 };

    private const float Gain = 0.035f;          // яркость одного прохода луча
    private const float RefSpeed = 300f;        // скорость (подпиксели/с), при которой яркость = Gain
    private const float MinSpeed = 12f;         // предел яркости в точках разворота
    private const float BrightK = 5f;           // множитель яркости → 0..1
    private const float Threshold = 0.18f;      // порог подпикселя для вывода
    private const float AutoRatioMin = 8f, AutoRatioRange = 4f;
    private const float FigGapMin = 20f, FigGapRange = 20f;
    private const float FigLenMin = 5f, FigLenRange = 3f;
    private const string LabelText = "CH1 1V/DIV  CH2 1V/DIV  TIME XY";

    private static readonly Rgba TraceColor = Rgba.Hex(0x33FF66);
    private static readonly Rgba GridColor = Rgba.Hex(0x1F7A3A);
    private static readonly Rgba LabelColor = Rgba.Hex(0x2FB050);

    // Куб: вершины и рёбра.
    private static readonly float[] CubeX = { -1, 1, 1, -1, -1, 1, 1, -1 };
    private static readonly float[] CubeY = { -1, -1, 1, 1, -1, -1, 1, 1 };
    private static readonly float[] CubeZ = { -1, -1, -1, -1, 1, 1, 1, 1 };
    private static readonly int[] EdgeA = { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3 };
    private static readonly int[] EdgeB = { 1, 2, 3, 0, 5, 6, 7, 4, 4, 5, 6, 7 };
    private readonly float[] _px = new float[8], _py = new float[8];

    private int _W, _H, _W2, _H3, _scopeRows, _labelRow;
    private float _U, _V, _yPerX = 1f;
    private float[] _lum = Array.Empty<float>();
    private bool[] _gridCol = Array.Empty<bool>(), _gridRow = Array.Empty<bool>();
    private Random _rng = new(0);
    private float _speed = 1f, _persist = 0.85f;
    private int _traceDots = 1200;
    private bool _figuresOn = true;

    private float _t;
    private int _ratioIdx;
    private float _autoTimer, _lastSwitch;
    private bool _inFigure;
    private float _figTimer, _figT, _nextFig;

    // Звук.
    private bool _audioActive, _beatPending;
    private float _bass, _mid, _high, _kick;
    private float _bassSm, _midSm, _highSm;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _rng = rng;
        _speed = MathF.Max(0.05f, options.Speed);
        _persist = Math.Clamp(options.GetFloat(PersistSpec.Name, PersistSpec.Default), PersistSpec.Min, PersistSpec.Max);
        _traceDots = Math.Clamp(options.GetInt(TraceSpec.Name, (int)TraceSpec.Default), (int)TraceSpec.Min, (int)TraceSpec.Max);
        _figuresOn = options.GetBool(FiguresSpec.Name, FiguresSpec.Default > 0.5f);

        // Строка подписей — внизу, если есть место; остальное — область осциллографа.
        int r = Math.Max(1, rows);
        bool label = r >= 4;
        _labelRow = label ? r - 1 : -1;
        _scopeRows = label ? r - 1 : r;
        _W = Math.Max(1, cols);
        _H = _scopeRows;
        _W2 = 2 * _W;
        _H3 = 3 * _H;
        _lum = new float[_W2 * _H3];

        // Шрифт: ячейка высота/ширина = aspect. Подпиксель секстеля: ширина w/2, высота aspect*w/3.
        // _yPerX — сколько вертикальных подпикселей равно одному горизонтальному физически (3/(2·aspect)).
        // Деления сетки квадратные на экране: U — горизонтальный шаг, V — вертикальный.
        float aspect = Math.Clamp(options.CellAspect, 0.5f, 4f);
        _yPerX = 3f / (2f * aspect);
        _U = MathF.Min(_W2 / 10f, _H3 / (8f * _yPerX));
        _V = _U * _yPerX;
        float gcx = _W2 * 0.5f, gcy = _H3 * 0.5f;
        _gridCol = new bool[_W];
        _gridRow = new bool[_H];
        for (int k = 0; k <= 10; k++)
        {
            int c = (int)MathF.Floor((gcx + (k - 5) * _U) / 2f);
            if ((uint)c < (uint)_W) _gridCol[c] = true;
        }
        for (int j = 0; j <= 8; j++)
        {
            int row = (int)MathF.Floor((gcy + (j - 4) * _V) / 3f);
            if ((uint)row < (uint)_H) _gridRow[row] = true;
        }

        _t = 0f;
        _ratioIdx = 0;
        _autoTimer = AutoRatioMin + AutoRatioRange * _rng.NextSingle();
        _lastSwitch = -10f;
        _inFigure = false;
        _figTimer = 0f;
        _figT = 0f;
        _nextFig = FigGapMin + FigGapRange * _rng.NextSingle();

        _audioActive = false;
        _beatPending = false;
        _bass = _mid = _high = _kick = 0f;
        _bassSm = _midSm = _highSm = 0f;
    }

    public void OnAudio(in AudioFrame audio)
    {
        if (!audio.Active)
        {
            _audioActive = false;
            _bass = _mid = _high = _kick = 0f;
            return;
        }
        _audioActive = true;
        _bass = Clamp01(audio.Bass);
        _mid = Clamp01(audio.Mid);
        _high = Clamp01(audio.High);
        _kick = Clamp01(audio.BeatPulse);
        if (audio.Beat) _beatPending = true;
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;
        dt = MathF.Min(dt, 0.1f);
        _t += dt * _speed;

        // Звук (без музыки — тихие LFO).
        float k = MathF.Min(1f, dt * 8f);
        _bassSm += ((_audioActive ? _bass : 0f) - _bassSm) * k;
        _midSm += ((_audioActive ? _mid : 0f) - _midSm) * k;
        _highSm += ((_audioActive ? _high : 0f) - _highSm) * k;
        if (!_audioActive) _kick = 0f;

        // Смена отношения частот: по таймеру и на ударе (с паузой, чтобы не мелькало).
        _autoTimer -= dt;
        if (_autoTimer <= 0f)
        {
            SwitchRatio();
            _autoTimer = AutoRatioMin + AutoRatioRange * _rng.NextSingle();
        }
        if (_beatPending)
        {
            _beatPending = false;
            if (_t - _lastSwitch > 1.5f) SwitchRatio();
        }

        // Фигуры из отрезков.
        if (_figuresOn)
        {
            if (_inFigure)
            {
                _figT += dt;
                _figTimer -= dt;
                if (_figTimer <= 0f)
                {
                    _inFigure = false;
                    _nextFig = FigGapMin + FigGapRange * _rng.NextSingle();
                }
            }
            else
            {
                _nextFig -= dt;
                if (_nextFig <= 0f)
                {
                    _inFigure = true;
                    _figTimer = FigLenMin + FigLenRange * _rng.NextSingle();
                    _figT = 0f;
                }
            }
        }

        // Послесвечение: затухание за реальное время, не за кадры.
        float decay = MathF.Pow(_persist, dt * 60f);
        var lum = _lum;
        for (int i = 0; i < lum.Length; i++) lum[i] *= decay;

        float flash = 1f + 0.8f * _kick;
        if (_inFigure) DrawCube(flash);
        else DrawLissajous(flash);
    }

    public void Draw(CharGrid grid)
    {
        var cells = grid.Cells;
        int gc = grid.Cols;
        int rows = Math.Min(_scopeRows, grid.Rows), cols = Math.Min(_W, grid.Cols);
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                int bits = 0, n = 0;
                float sum = 0f;
                for (int i = 0; i < 6; i++)
                {
                    int px = 2 * x + (i & 1), py = 3 * y + (i >> 1);
                    float v = MathF.Min(1f, _lum[py * _W2 + px] * BrightK);
                    if (v > Threshold)
                    {
                        bits |= 1 << i;
                        sum += v;
                        n++;
                    }
                }
                if (n > 0)
                {
                    cells[y * gc + x] = new Cell(GlyphSet.Sextant(bits), TraceColor.Scale(sum / n), Rgba.Black);
                }
                else if ((_gridCol[x] && (y & 1) == 0) || (_gridRow[y] && (x % 3) == 0))
                {
                    cells[y * gc + x] = new Cell('·', GridColor, Rgba.Black);
                }
                else
                {
                    cells[y * gc + x] = new Cell(' ', Rgba.Black, Rgba.Black);
                }
            }
        }

        if (_labelRow >= 0 && _labelRow < grid.Rows)
        {
            for (int x = 0; x < grid.Cols; x++) cells[_labelRow * gc + x] = new Cell(' ', Rgba.Black, Rgba.Black);
            for (int i = 0; i < LabelText.Length && i + 1 < grid.Cols; i++)
                cells[_labelRow * gc + i + 1] = new Cell(LabelText[i], LabelColor, Rgba.Black);
        }
    }

    // ----- луч -----

    private void SwitchRatio()
    {
        // Соседнее отношение: без повтора текущего.
        _ratioIdx = (_ratioIdx + 1 + _rng.Next(RatioA.Length - 1)) % RatioA.Length;
        _lastSwitch = _t;
    }

    /// <summary>Лиссажу: замкнутая кривая из traceDots точек за кадр; отрезки между точками — без разрывов.</summary>
    private void DrawLissajous(float flash)
    {
        float a = RatioA[_ratioIdx], b = RatioB[_ratioIdx];
        float cx = _W2 * 0.5f, cy = _H3 * 0.5f;
        // Амплитуда X растёт от басов, Y — от середины; без звука — спокойные 45% и 50%.
        float ampX = (0.45f + 0.55f * _bassSm) * (1f + 0.04f * MathF.Sin(_t * 0.3f));
        float ampY = 0.5f + 0.5f * _midSm;
        float ax = ampX * 4f * _U, ay = ampY * 3.2f * _V;
        float phi = _t * 0.25f;
        float jit = _highSm * 0.15f;

        int n = _traceDots;
        float du = MathF.Tau / n;
        float prevX = 0f, prevY = 0f, prevV = 0f;
        for (int i = 0; i <= n; i++)
        {
            float u = i * du;
            float su = MathF.Sin(a * u + phi), cu = MathF.Cos(a * u + phi);
            float sv = MathF.Sin(b * u), cv = MathF.Cos(b * u);
            float x = cx + ax * su;
            float y = cy + ay * sv + ay * jit * MathF.Sin(7f * u + _t * 9f);
            float vx = ax * a * cu, vy = ay * b * cv;
            float v = MathF.Sqrt(vx * vx + vy * vy);
            if (i > 0) Stroke(prevX, prevY, x, y, prevV, v, Gain * flash);
            prevX = x; prevY = y; prevV = v;
        }
    }

    /// <summary>Вращающийся куб: 12 рёбер, луч идёт с постоянной скоростью (ровная яркость).</summary>
    private void DrawCube(float flash)
    {
        float cx = _W2 * 0.5f, cy = _H3 * 0.5f;
        float size = MathF.Min(_W2, _H3 / _yPerX) * 0.2f;
        float ay = _figT * 0.9f, ax = _figT * 0.6f;
        float cosY = MathF.Cos(ay), sinY = MathF.Sin(ay), cosX = MathF.Cos(ax), sinX = MathF.Sin(ax);
        for (int i = 0; i < 8; i++)
        {
            float x = CubeX[i], y = CubeY[i], z = CubeZ[i];
            float x1 = x * cosY + z * sinY;
            float z1 = -x * sinY + z * cosY;
            float y1 = y * cosX - z1 * sinX;
            float z2 = y * sinX + z1 * cosX;
            float f = size * 3f / (z2 + 3f);
            _px[i] = cx + x1 * f;
            _py[i] = cy + y1 * f * _yPerX;
        }
        for (int e = 0; e < EdgeA.Length; e++)
        {
            int p = EdgeA[e], q = EdgeB[e];
            Stroke(_px[p], _py[p], _px[q], _py[q], RefSpeed, RefSpeed, Gain * flash * 2f);
        }
    }

    /// <summary>
    /// Отрезок в подпикселях (Брезенхем, без разрывов). Каждый пройденный подпиксель получает яркость
    /// Gain * RefSpeed / скорость: луч медленно проходит — точка ярче.
    /// </summary>
    private void Stroke(float x0f, float y0f, float x1f, float y1f, float v0, float v1, float gain)
    {
        float sp = MathF.Max(MinSpeed, (v0 + v1) * 0.5f);
        float dep = gain * RefSpeed / sp;
        int x = (int)MathF.Round(x0f), y = (int)MathF.Round(y0f);
        int xe = (int)MathF.Round(x1f), ye = (int)MathF.Round(y1f);
        int dx = Math.Abs(xe - x), dy = -Math.Abs(ye - y);
        int sx = x < xe ? 1 : -1, sy = y < ye ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            if ((uint)x < (uint)_W2 && (uint)y < (uint)_H3) _lum[y * _W2 + x] += dep;
            if (x == xe && y == ye) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
