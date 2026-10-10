using System.Runtime.Intrinsics;
using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// Бесконечный медленный зум в множество Мандельброта (демосценовая заставка), автопилот ведёт центр к границе.
/// Пиксели на RetroCanvas (HalfBlock, цвет точный). Раскраска по сглаженным итерациям, внутренность чёрная.
/// Границы рисуются по оценке расстояния до множества (DE = |z|·ln|z| / |dz|): пиксель у границы красится
/// цветом края палитры, а не по итерациям. Так тонкие филаменты читаются как линии, а не как снег.
/// Переход к следующей точке — затемнение и проявление. Звук: удар — вспышка яркости и рывок прокрутки цвета.
/// Расчёт параллельный по строкам.
/// </summary>
public sealed class MandelbrotScene : IScene, IAudioReactive, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec ColorSpec = new("colorSpeed", SceneParamKind.Float, 0.04f, 0f, 0.5f, "скорость смены палитры, циклов/с");
    private static readonly SceneParamSpec[] ParamList = { ColorSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    private float _colorSpeed;

    private enum Phase { Zoom, FadeOut, FadeIn }

    private const int PaletteCount = 4;
    private const int LutSize = 1024;
    private const int MaxIterCap = 1500;
    private const double StartScale = 2.5;       // ширина видимой области в начале зума, в единицах комплексной плоскости
    private const double EndScale = 1e-11;       // предел double: дальше пикселизация
    private const float DoublingTime = 7f;       // секунд на ×2
    private const float FadeTime = 1.2f;         // затемнение / проявление при смене точки, с
    private const float ColorPeriod = 24f;       // итераций на один цикл палитры
    private const float KickMin = 0.06f, KickMax = 0.10f; // рывок прокрутки на удар, циклов палитры
    private const float KickRate = 8f;           // скорость отыгрывания рывка, 1/с
    private const float FlashTau = 0.15f;        // затухание вспышки, с
    private const float FlashGain = 0.6f;        // прирост яркости во вспышке
    private const float RotationAmp = 0.25f;     // максимальный наклон плоскости, рад
    private const float AutoPeriod = 0.25f;      // автопилот: поиск цели по буферу, с
    private const float CenterTau = 0.8f;        // автопилот: постоянная плавного движения центра, с (сходимость ~2–3 с)
    private const float FlatLimit = 1.5f;        // однотонный кадр дольше этого — досрочный переход, с
    private const double ScaleFloor = 1e-12;     // предел double относительно |c| — страховка досрочного перехода
    private const double EdgeFactor = 0.5;       // граница: DE < EdgeFactor · размер пикселя (в комплексных единицах)
    private const int NoiseJump = 96;            // скачок яркости (R+G+B) между соседями, с которого пара — «крапинка»

    /// <summary>
    /// Порог однотонности: std яркости пикселя (R+G+B, 0..765) по кадру. Однотонное поле даёт 0;
    /// любая видимая граница между цветами палитры даёт десятки. 20 ≈ 2.6 % диапазона.
    /// </summary>
    public const float FlatLumaStd = 20f;

    /// <summary>
    /// Переключатель рисования границ по DE. По умолчанию включён. Выключен — раскраска только по итерациям
    /// и граница по соседству с внутренностью (так снимали «снежную» контрольную версию для порога).
    /// Только для измерений; в игре всегда true.
    /// </summary>
    public static bool DistanceEdges = true;

    /// <summary>Маршрут: стартовые точки-семена направления (автопилот подтягивает центр к границе). Палитра = индекс % PaletteCount.</summary>
    private static readonly (double X, double Y)[] Route =
    {
        (-0.743643887037151, 0.131825904205330), // Seahorse valley
        (0.2925, 0.0149),                        // Elephant valley
        (-1.25066, 0.02012),                     // молнии на антенне
        (-0.761574, -0.0847596),                 // спираль
        (-0.088, 0.654301),                      // Triple spiral valley
        (-1.401155, 0.0),                        // точка Фейгенбаума
        (-1.768778833, -0.001738996),            // мини-брот
        (-0.7463, 0.1102),                       // хвост морского конька
    };

    /// <summary>Цвет края (тёмный) для каждой палитры: огонь — тёмно-коричневый, лёд — тёмно-синий и т.п.</summary>
    private static readonly uint[] EdgeHex = { 0x3A1400, 0x002A5A, 0x1A0A40, 0x202020 };

    /// <summary>Длительность зума от StartScale до EndScale, с.</summary>
    private static readonly float ZoomEnd = DoublingTime * (float)Math.Log2(StartScale / EndScale);

    public string Id => "mandelbrot";

    /// <summary>Индекс текущей точки маршрута.</summary>
    public int PointIndex => _point;

    /// <summary>Во сколько раз видимая область уже уменьшена относительно начала зума (1 в начале точки).</summary>
    public double Zoom => StartScale / Scale;

    /// <summary>Текущая ширина видимой области, единицы комплексной плоскости.</summary>
    public double Scale => Math.Max(EndScale, StartScale * Math.Pow(2.0, -_zoomTime / DoublingTime));

    /// <summary>Время зума внутри текущей точки, с (симуляционное).</summary>
    public float ZoomTime => _zoomTime;

    /// <summary>Длительность зума одной точки до EndScale, с.</summary>
    public static float ZoomDurationSeconds => ZoomEnd;

    /// <summary>Число точек маршрута.</summary>
    public static int RouteLength => Route.Length;

    /// <summary>Центр вида (автопилот), комплексная плоскость.</summary>
    public (double X, double Y) Center => (_cx, _cy);

    private RetroCanvas _canvas = null!;
    private Rgba[] _px = Array.Empty<Rgba>();
    private double[] _mu = Array.Empty<double>(); // сглаженные итерации, -1 — внутри (живут между кадрами)
    private double[] _de = Array.Empty<double>(); // оценка расстояния до множества, для внешних пикселей
    private Rgba[] _lut = Array.Empty<Rgba>();   // PaletteCount палитр по LutSize цветов
    private Rgba[] _edge = Array.Empty<Rgba>();  // цвет края для каждой палитры
    private Random _rng = new(0);
    private float _speed = 1f;

    // Делегат кешируется. Parallel.For сам аллоцирует внутренние объекты на каждом вызове (задачи, состояние) —
    // принято как есть, строгого «ноль аллокаций» в Update/Draw здесь нет.
    private readonly Action<int> _rowAction;

    private int _point;
    private Phase _phase;
    private float _zoomTime;                     // время зума внутри текущей точки, с (в «симуляционных» секундах)
    private float _phaseT;                       // время внутри фазы перехода, с
    private float _brightness;                   // 0..1, затемнение/проявление
    private float _t;                            // общее время, для вращения
    private float _offset;                       // сдвиг палитры, циклы (0..1)
    private float _kick;                         // рывок прокрутки, ещё не отыгранный, циклы
    private float _flash;                        // вспышка от удара, 1 → 0

    // Автопилот: центр вида тянется к цели, цель — плотность границы в центральной области
    private double _cx, _cy;                     // текущий центр вида
    private double _tCx, _tCy;                   // цель центра
    private float _autoT;                        // таймер поиска цели, с
    private float _flatT;                        // сколько подряд однотонных кадров в зуме, с

    // Параметры кадра, выставляются в Render до Parallel.For
    private double _fCx, _fCy, _fCos = 1, _fSin, _fPix;
    private int _fMaxIter;
    private float _fBright, _fOffset;
    private int _fLutBase, _fPalette;
    private bool _fFull;
    private int _fParity;

    private bool _full = true;                   // следующий кадр — полный пересчёт
    private int _parity;                         // чётность строк, пересчитываемых в этом кадре

    public MandelbrotScene() => _rowAction = RenderRow;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _colorSpeed = Math.Clamp(options.GetFloat(ColorSpec.Name, ColorSpec.Default), ColorSpec.Min, ColorSpec.Max);
        _rng = rng;
        _speed = MathF.Max(0f, options.Speed);
        _canvas = new RetroCanvas(cols, rows, CanvasMode.HalfBlock);
        _px = _canvas.Pixels;
        _mu = new double[_canvas.Width * _canvas.Height];
        _de = new double[_canvas.Width * _canvas.Height];
        _full = true;
        _parity = 0;
        BuildLut();

        _point = 0;
        _phase = Phase.Zoom;
        _zoomTime = 0f;
        _phaseT = 0f;
        _brightness = 1f;
        _t = 0f;
        _offset = rng.NextSingle();
        _kick = 0f;
        _flash = 0f;
        ResetAim();
        Render();
    }

    private void BuildLut()
    {
        var fire = new[]
        {
            Rgba.Hex(0x000000), Rgba.Hex(0x500000), Rgba.Hex(0xC02000), Rgba.Hex(0xFF8000),
            Rgba.Hex(0xFFF0A0), Rgba.Hex(0xFF8000), Rgba.Hex(0xC02000), Rgba.Hex(0x500000),
        };
        var ice = new[]
        {
            Rgba.Hex(0x001030), Rgba.Hex(0x0060A0), Rgba.Hex(0x40C0FF), Rgba.Hex(0xE0FBFF),
            Rgba.Hex(0x40C0FF), Rgba.Hex(0x0060A0),
        };
        var copper = new[]
        {
            Rgba.Hex(0x2000A0), Rgba.Hex(0x4040FF), Rgba.Hex(0x80C0FF), Rgba.Hex(0xFFFFFF),
            Rgba.Hex(0xFFD040), Rgba.Hex(0xFF8000), Rgba.Hex(0xC02000), Rgba.Hex(0x600000),
            Rgba.Hex(0xC02000), Rgba.Hex(0xFF8000), Rgba.Hex(0xFFD040), Rgba.Hex(0xFFFFFF),
            Rgba.Hex(0x80C0FF), Rgba.Hex(0x4040FF),
        };
        var psy = new Rgba[8];
        for (int i = 0; i < psy.Length; i++) psy[i] = Rgba.Hsv(i * 45f, 0.9f, 1f);

        var pals = new[] { fire, ice, copper, psy };
        _lut = new Rgba[PaletteCount * LutSize];
        _edge = new Rgba[PaletteCount];
        for (int p = 0; p < PaletteCount; p++)
        {
            _edge[p] = Rgba.Hex(EdgeHex[p]);
            for (int i = 0; i < LutSize; i++)
            {
                var c = RetroPalettes.Gradient(pals[p], i / (float)LutSize);
                _lut[p * LutSize + i] = p == 2 ? RetroPalettes.Amiga12(c) : c; // коппер — 12-битная палитра Amiga
            }
        }
    }

    public void OnAudio(in AudioFrame audio)
    {
        if (!audio.Active) return;                    // тишина — сцена ведёт себя как без звука
        if (!audio.Beat) return;
        _flash = 1f;
        _kick += KickMin + _rng.NextSingle() * (KickMax - KickMin);
    }

    public void Update(float dt)
    {
        dt = Math.Clamp(dt, 0f, 0.1f) * _speed;
        if (dt <= 0f) return;                         // стоп — состояние и кадр не меняются
        _t += dt;

        // вспышка и рывок прокрутки цвета (зум не трогаем)
        _flash *= MathF.Exp(-dt / FlashTau);
        float push = _kick * (1f - MathF.Exp(-dt * KickRate));
        _kick -= push;
        _offset += dt * _colorSpeed + push;
        _offset -= MathF.Floor(_offset);

        // автопилот: цель — по буферу прошлого кадра, центр плавно тянется к ней
        _autoT += dt;
        if (_autoT >= AutoPeriod)
        {
            _autoT = 0f;
            SearchTarget();
        }
        float k = 1f - MathF.Exp(-dt / CenterTau);
        _cx += (_tCx - _cx) * k;
        _cy += (_tCy - _cy) * k;

        switch (_phase)
        {
            case Phase.Zoom:
                _zoomTime += dt;
                if (_zoomTime >= ZoomEnd)
                {
                    _zoomTime = ZoomEnd;
                    _phase = Phase.FadeOut;
                    _phaseT = 0f;
                }
                break;
            case Phase.FadeOut:
                _phaseT += dt;
                if (_phaseT >= FadeTime)
                {
                    _point = (_point + 1) % Route.Length;
                    _zoomTime = 0f;
                    _full = true;                // новая точка — без старых значений
                    ResetAim();
                    _phase = Phase.FadeIn;
                    _phaseT = 0f;
                }
                break;
            case Phase.FadeIn:
                _phaseT += dt;
                if (_phaseT >= FadeTime)
                {
                    _phase = Phase.Zoom;
                    _phaseT = 0f;
                }
                break;
        }

        _brightness = _phase switch
        {
            Phase.FadeOut => 1f - Smooth(_phaseT / FadeTime),
            Phase.FadeIn => Smooth(_phaseT / FadeTime),
            _ => 1f,
        };

        Render();

        if (_phase == Phase.Zoom) Guard(dt);
    }

    /// <summary>Страховка: однотонный кадр дольше FlatLimit или предел double — досрочный переход к следующей точке.</summary>
    private void Guard(float dt)
    {
        _flatT = LumaStd() < FlatLumaStd ? _flatT + dt : 0f;
        double r = Math.Max(1.0, Math.Sqrt(_cx * _cx + _cy * _cy));
        if (_flatT >= FlatLimit || Scale < ScaleFloor * r)
        {
            _zoomTime = ZoomEnd;
            _phase = Phase.FadeOut;
            _phaseT = 0f;
            _flatT = 0f;
        }
    }

    /// <summary>Std яркости (R+G+B) по последнему кадру, без аллокаций.</summary>
    private double LumaStd()
    {
        long sum = 0, sq = 0;
        var px = _px;
        for (int i = 0; i < px.Length; i++)
        {
            int l = Luma(px[i]);
            sum += l;
            sq += l * l;
        }
        double n = px.Length;
        double mean = sum / n;
        return Math.Sqrt(Math.Max(0.0, sq / n - mean * mean));
    }

    private static int Luma(Rgba c) => c.R + c.G + c.B;

    /// <summary>
    /// Пиксель — граница. С DE: внешний пиксель с DE &lt; EdgeFactor·размер пикселя. Без DE (измерение): внешний
    /// пиксель, соседний с внутренностью.
    /// </summary>
    private bool IsEdgeAt(int x, int y, int w, int h)
    {
        int i = y * w + x;
        if (_mu[i] < 0) return false;
        if (DistanceEdges) return _de[i] < EdgeFactor * _fPix;
        if (x > 0 && _mu[i - 1] < 0) return true;
        if (x + 1 < w && _mu[i + 1] < 0) return true;
        if (y > 0 && _mu[i - w] < 0) return true;
        if (y + 1 < h && _mu[i + w] < 0) return true;
        return false;
    }

    /// <summary>Сброс автопилота на семя текущей точки маршрута.</summary>
    private void ResetAim()
    {
        var (sx, sy) = Route[_point];
        _cx = _tCx = sx;
        _cy = _tCy = sy;
        _autoT = 0f;
        _flatT = 0f;
    }

    /// <summary>
    /// Цель автопилота: в центральных 60 % кадра ищем окно 5×5 с наибольшей долей граничных пикселей
    /// (при условии, что есть внешние и граничные или внутренние), вес к центру.
    /// Координаты по буферу _mu, построенному для вида _fCx/_fCy.
    /// </summary>
    private void SearchTarget()
    {
        int w = _canvas.Width, h = _canvas.Height;
        int x0 = w / 5, x1 = w - w / 5, y0 = h / 5, y1 = h - h / 5;
        double best = 0;
        int bx = -1, by = -1;

        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            int edges = 0, inside = 0, outside = 0;
            for (int dy = -2; dy <= 2; dy++)
            {
                int ny = Math.Clamp(y + dy, 0, h - 1);
                for (int dx = -2; dx <= 2; dx++)
                {
                    int nx = Math.Clamp(x + dx, 0, w - 1);
                    if (_mu[ny * w + nx] < 0) inside++;
                    else if (IsEdgeAt(nx, ny, w, h)) edges++;
                    else outside++;
                }
            }
            if (outside == 0 || (edges == 0 && inside == 0)) continue;

            double ex = (x - w * 0.5) / (w * 0.3), ey = (y - h * 0.5) / (h * 0.3);
            double r = Math.Min(1.0, Math.Sqrt(ex * ex + ey * ey));
            double score = edges * (1.0 - 0.5 * r);
            if (score > best)
            {
                best = score;
                bx = x;
                by = y;
            }
        }

        if (bx < 0) return;                           // в центре нет границы — цель не меняем
        double ox = (bx + 0.5 - w * 0.5) * _fPix, oy = (by + 0.5 - h * 0.5) * _fPix;
        _tCx = _fCx + ox * _fCos - oy * _fSin;
        _tCy = _fCy + ox * _fSin + oy * _fCos;
    }

    /// <summary>
    /// Доля «одиночных крапинок»: из внешних непограничных пикселей — те, у которых |ΔL| &gt; NoiseJump
    /// от всех четырёх соседей. Снег — много крапинок; контрастные связные края — почти нет.
    /// Считается по текущему кадру (после Update/Draw).
    /// </summary>
    public double SpeckleShare()
    {
        int w = _canvas.Width, h = _canvas.Height;
        var px = _px;
        int count = 0, speck = 0;
        for (int y = 1; y < h - 1; y++)
        for (int x = 1; x < w - 1; x++)
        {
            int i = y * w + x;
            if (_mu[i] < 0 || IsEdgeAt(x, y, w, h)) continue;
            count++;
            int l = Luma(px[i]);
            if (Math.Abs(l - Luma(px[i - 1])) > NoiseJump && Math.Abs(l - Luma(px[i + 1])) > NoiseJump
                && Math.Abs(l - Luma(px[i - w])) > NoiseJump && Math.Abs(l - Luma(px[i + w])) > NoiseJump)
                speck++;
        }
        return count == 0 ? 0 : speck / (double)count;
    }

    private static float Smooth(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    private void Render()
    {
        var (cx, cy) = (_cx, _cy);
        double ang = RotationAmp * Math.Sin(_t * 0.07);
        _fCos = Math.Cos(ang);
        _fSin = Math.Sin(ang);
        _fCx = cx;
        _fCy = cy;
        _fPix = Scale / _canvas.Width;
        // 64 + 40·log2(zoom), потолок MaxIterCap; log2(zoom) = _zoomTime / DoublingTime
        _fMaxIter = Math.Min(MaxIterCap, 64 + (int)(40f * _zoomTime / DoublingTime));
        _fBright = _brightness * (1f + FlashGain * _flash);
        _fOffset = _offset;
        _fPalette = _point % PaletteCount;
        _fLutBase = _fPalette * LutSize;
        _fFull = _full;
        _fParity = _parity;
        _full = false;
        _parity ^= 1;

        Parallel.For(0, _canvas.Height, _rowAction);
    }

    /// <summary>
    /// Строка: пересчёт на чередование (полу-кадр, как interlace) — только строки текущей четности,
    /// остальные берут сырые значения с прошлого кадра. Зум за кадр ≈ 0.14 %, рассинхрон под разрешением
    /// пикселя; раскраска и границы пересчитываются каждый кадр (вспышка, прокрутка, размер пикселя).
    /// Полный пересчёт — после смены точки и при Init.
    /// </summary>
    private void RenderRow(int y)
    {
        int w = _canvas.Width;
        int rowBase = y * w;
        if (_fFull || (y & 1) == _fParity) ComputeRow(y, rowBase, w);
        for (int x = 0; x < w; x++) Shade(rowBase + x);
    }

    private void ComputeRow(int y, int rowBase, int w)
    {
        double oy = (y + 0.5 - _canvas.Height * 0.5) * _fPix;
        double halfW = w * 0.5;
        int maxIter = _fMaxIter;
        double pix = _fPix, cos = _fCos, sin = _fSin, bx = _fCx, by = _fCy;
        int x = 0;

        if (Vector256.IsHardwareAccelerated)
        {
            // две группы по четыре (восемь лучей в цикле) — независимые цепочки, лучше загружают ядро
            Span<double> cxs = stackalloc double[8], cys = stackalloc double[8];
            Span<double> mus = stackalloc double[8], des = stackalloc double[8];
            for (; x + 8 <= w; x += 8)
            {
                for (int k = 0; k < 8; k++)
                {
                    double ox = (x + k + 0.5 - halfW) * pix;
                    double cx = bx + ox * cos - oy * sin, cy = by + ox * sin + oy * cos;
                    cxs[k] = cx;
                    cys[k] = cy;
                    mus[k] = IsInterior(cx, cy) ? -1 : -2;   // -2 = считать
                }
                Escape8(cxs, cys, mus, des, maxIter);
                for (int k = 0; k < 8; k++)
                {
                    _mu[rowBase + x + k] = mus[k];
                    _de[rowBase + x + k] = des[k];
                }
            }
            for (; x + 4 <= w; x += 4)
            {
                for (int k = 0; k < 4; k++)
                {
                    double ox = (x + k + 0.5 - halfW) * pix;
                    double cx = bx + ox * cos - oy * sin, cy = by + ox * sin + oy * cos;
                    cxs[k] = cx;
                    cys[k] = cy;
                    mus[k] = IsInterior(cx, cy) ? -1 : -2;
                }
                Escape4(cxs[..4], cys[..4], mus[..4], des[..4], maxIter);
                for (int k = 0; k < 4; k++)
                {
                    _mu[rowBase + x + k] = mus[k];
                    _de[rowBase + x + k] = des[k];
                }
            }
        }

        for (; x < w; x++)
        {
            double ox = (x + 0.5 - halfW) * pix;
            _mu[rowBase + x] = Escape(bx + ox * cos - oy * sin, by + ox * sin + oy * cos, maxIter, out double de);
            _de[rowBase + x] = de;
        }
    }

    /// <summary>Цвет пикселя i: внутренность — чёрная; граница (DE) — цвет края палитры; иначе — по итерациям.</summary>
    private void Shade(int i)
    {
        double m = _mu[i];
        if (m < 0)
        {
            _px[i] = Rgba.Black;
            return;
        }
        if (DistanceEdges && _de[i] < EdgeFactor * _fPix)
        {
            _px[i] = _edge[_fPalette].Scale(_fBright);
            return;
        }
        float t = (float)m / ColorPeriod + _fOffset;
        t -= MathF.Floor(t);
        int idx = (int)(t * LutSize) & (LutSize - 1);
        _px[i] = _lut[_fLutBase + idx].Scale(_fBright);
    }

    /// <summary>Кардиоида и главная луковица: точка заведомо внутри множества.</summary>
    private static bool IsInterior(double cx, double cy)
    {
        double y2 = cy * cy;
        double xq = cx - 0.25;
        double q = xq * xq + y2;
        if (q * (q + xq) <= 0.25 * y2) return true;
        double xb = cx + 1.0;
        return xb * xb + y2 <= 0.0625;
    }

    /// <summary>
    /// Восемь точек: два независимых вектора Vector256 в одном цикле. Параллельно — производная dz = 2·z·dz + 1
    /// для оценки расстояния DE = |z|·ln|z| / |dz| (считается в момент убегания).
    /// </summary>
    private static void Escape8(ReadOnlySpan<double> cxs, ReadOnlySpan<double> cys, Span<double> mu, Span<double> de, int maxIter)
    {
        var aliveA = Vector256.Create(
            mu[0] == -2 ? -1L : 0L, mu[1] == -2 ? -1L : 0L, mu[2] == -2 ? -1L : 0L, mu[3] == -2 ? -1L : 0L);
        var aliveB = Vector256.Create(
            mu[4] == -2 ? -1L : 0L, mu[5] == -2 ? -1L : 0L, mu[6] == -2 ? -1L : 0L, mu[7] == -2 ? -1L : 0L);
        var cxA = Vector256.Create(cxs[..4]);
        var cyA = Vector256.Create(cys[..4]);
        var cxB = Vector256.Create(cxs[4..8]);
        var cyB = Vector256.Create(cys[4..8]);
        var xA = Vector256<double>.Zero;
        var yA = Vector256<double>.Zero;
        var xB = Vector256<double>.Zero;
        var yB = Vector256<double>.Zero;
        var drA = Vector256<double>.Zero;
        var diA = Vector256<double>.Zero;
        var drB = Vector256<double>.Zero;
        var diB = Vector256<double>.Zero;
        var sxA = Vector256<double>.Zero;
        var syA = Vector256<double>.Zero;
        var sxB = Vector256<double>.Zero;
        var syB = Vector256<double>.Zero;
        var two = Vector256.Create(2.0);
        var one = Vector256.Create(1.0);
        var bail = Vector256.Create(256.0);
        int power = 1, lam = 0;

        for (int i = 0; i < maxIter && (aliveA | aliveB) != Vector256<long>.Zero; i++)
        {
            var x2A = xA * xA;
            var yyA = yA * yA;
            var r2A = x2A + yyA;
            var x2B = xB * xB;
            var yyB = yB * yB;
            var r2B = x2B + yyB;
            var escA = Vector256.GreaterThan(r2A, bail).AsInt64() & aliveA;
            var escB = Vector256.GreaterThan(r2B, bail).AsInt64() & aliveB;
            if ((escA | escB) != Vector256<long>.Zero)
            {
                WriteEscaped(escA, r2A, drA, diA, i, mu, de, 0);
                WriteEscaped(escB, r2B, drB, diB, i, mu, de, 4);
                aliveA &= ~escA;
                aliveB &= ~escB;
            }

            // dz_{n+1} = 2·z_n·dz_n + 1 (до обновления z)
            var ndrA = two * (xA * drA - yA * diA) + one;
            var ndiA = two * (xA * diA + yA * drA);
            var ndrB = two * (xB * drB - yB * diB) + one;
            var ndiB = two * (xB * diB + yB * drB);
            drA = ndrA; diA = ndiA;
            drB = ndrB; diB = ndiB;

            yA = two * xA * yA + cyA;
            xA = x2A - yyA + cxA;
            yB = two * xB * yB + cyB;
            xB = x2B - yyB + cxB;

            var eqA = Vector256.Equals(xA, sxA).AsInt64() & Vector256.Equals(yA, syA).AsInt64() & aliveA;
            var eqB = Vector256.Equals(xB, sxB).AsInt64() & Vector256.Equals(yB, syB).AsInt64() & aliveB;
            if ((eqA | eqB) != Vector256<long>.Zero)
            {
                WriteInterior(eqA, mu, de, 0);
                WriteInterior(eqB, mu, de, 4);
                aliveA &= ~eqA;
                aliveB &= ~eqB;
            }

            if (++lam == power)
            {
                sxA = xA; syA = yA;
                sxB = xB; syB = yB;
                power <<= 1;
                lam = 0;
            }
        }

        for (int k = 0; k < 8; k++)
            if (mu[k] == -2) { mu[k] = -1; de[k] = 0; }
    }

    private static void WriteEscaped(Vector256<long> esc, Vector256<double> r2, Vector256<double> dr, Vector256<double> di,
        int i, Span<double> mu, Span<double> de, int offset)
    {
        if (esc == Vector256<long>.Zero) return;
        uint bits = Vector256.ExtractMostSignificantBits(esc);
        for (int k = 0; k < 4; k++)
        {
            if (((bits >> k) & 1) == 0) continue;
            double r = Math.Sqrt(r2.GetElement(k));
            double dm = Math.Sqrt(dr.GetElement(k) * dr.GetElement(k) + di.GetElement(k) * di.GetElement(k));
            mu[offset + k] = i + 1 - Math.Log2(Math.Log2(r));
            de[offset + k] = r * Math.Log(r) / dm;
        }
    }

    private static void WriteInterior(Vector256<long> eq, Span<double> mu, Span<double> de, int offset)
    {
        if (eq == Vector256<long>.Zero) return;
        uint bits = Vector256.ExtractMostSignificantBits(eq);
        for (int k = 0; k < 4; k++)
            if (((bits >> k) & 1) != 0) { mu[offset + k] = -1; de[offset + k] = 0; }
    }

    /// <summary>
    /// Четыре точки сразу (та же логика, что Escape8). mu[k] = -2 — считать; после расчёта: сглаженное число
    /// итераций или -1 (внутри). de — оценка расстояния для убежавших.
    /// </summary>
    private static void Escape4(ReadOnlySpan<double> cxs, ReadOnlySpan<double> cys, Span<double> mu, Span<double> de, int maxIter)
    {
        var alive = Vector256.Create(
            mu[0] == -2 ? -1L : 0L, mu[1] == -2 ? -1L : 0L, mu[2] == -2 ? -1L : 0L, mu[3] == -2 ? -1L : 0L);
        var cx = Vector256.Create(cxs);
        var cy = Vector256.Create(cys);
        var x = Vector256<double>.Zero;
        var y = Vector256<double>.Zero;
        var dr = Vector256<double>.Zero;
        var di = Vector256<double>.Zero;
        var sx = Vector256<double>.Zero;
        var sy = Vector256<double>.Zero;
        var two = Vector256.Create(2.0);
        var one = Vector256.Create(1.0);
        var bail = Vector256.Create(256.0);
        int power = 1, lam = 0;

        for (int i = 0; i < maxIter && alive != Vector256<long>.Zero; i++)
        {
            var x2 = x * x;
            var yy = y * y;
            var r2 = x2 + yy;
            var esc = Vector256.GreaterThan(r2, bail).AsInt64() & alive;
            if (esc != Vector256<long>.Zero)
            {
                WriteEscaped(esc, r2, dr, di, i, mu, de, 0);
                alive &= ~esc;
            }

            var ndr = two * (x * dr - y * di) + one;
            var ndi = two * (x * di + y * dr);
            dr = ndr;
            di = ndi;

            y = two * x * y + cy;
            x = x2 - yy + cx;

            var eq = Vector256.Equals(x, sx).AsInt64() & Vector256.Equals(y, sy).AsInt64() & alive;
            if (eq != Vector256<long>.Zero)
            {
                WriteInterior(eq, mu, de, 0);
                alive &= ~eq;
            }

            if (++lam == power)
            {
                sx = x;
                sy = y;
                power <<= 1;
                lam = 0;
            }
        }

        for (int k = 0; k < 4; k++)
            if (mu[k] == -2) { mu[k] = -1; de[k] = 0; }   // не убежала за maxIter — внутри
    }

    /// <summary>
    /// Время убегания для c: сглаженное число итераций (≥ 0) или -1, если внутри. Вместе с ним — оценка расстояния
    /// de (для внешних). Кардиоида и луковица отсекаются заранее, цикл ловится детектором Брента.
    /// </summary>
    private static double Escape(double cx, double cy, int maxIter, out double de)
    {
        de = 0;
        if (IsInterior(cx, cy)) return -1;

        double x = 0, y = 0, dr = 0, di = 0, sx = 0, sy = 0;
        int power = 1, lam = 0;
        for (int i = 0; i < maxIter; i++)
        {
            double x2 = x * x, yy = y * y;
            if (x2 + yy > 256.0)
            {
                double r = Math.Sqrt(x2 + yy);
                de = r * Math.Log(r) / Math.Sqrt(dr * dr + di * di);
                return i + 1 - Math.Log2(Math.Log2(r));
            }
            double ndr = 2.0 * (x * dr - y * di) + 1.0;
            di = 2.0 * (x * di + y * dr);
            dr = ndr;
            y = 2.0 * x * y + cy;
            x = x2 - yy + cx;
            if (x == sx && y == sy) return -1;         // точка попала в притягивающий цикл — внутри
            if (++lam == power)
            {
                sx = x; sy = y;
                power <<= 1;
                lam = 0;
            }
        }
        return -1;
    }

    public void Draw(CharGrid grid) => _canvas.Present(grid);
}
