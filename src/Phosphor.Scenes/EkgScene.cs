using Phosphor.Core;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Phosphor.Scenes.Tests")]

namespace Phosphor.Scenes;

/// <summary>
/// Прикроватный монитор: ECG II (зелёная), PLETH (голубая, пульсовая волна с задержкой ~0.25 с после R) и RESP (жёлтая)
/// бегут стирающей развёрткой: луч пишет слева направо, перед ним чёрный зазор, справа ещё видна прошлая проходка.
/// Справа крупные HR и SPO2, NIBP и TEMP. Раз в 1–3 мин эпизод: экстрасистолы, тахикардия или LEADS OFF.
/// Звук: при музыке R-зубцы встают на удар, HR = BPM (40–180).
/// </summary>
public sealed class EkgScene : IScene, IAudioReactive, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec SweepSpec = new("sweep", SceneParamKind.Float, 25f, 12.5f, 50f, "скорость развёртки, мм/с");
    public static readonly SceneParamSpec EventsSpec = new("events", SceneParamKind.Float, 0.5f, 0f, 1f, "частота эпизодов");
    public static readonly SceneParamSpec GridSpec = new("grid", SceneParamKind.Bool, 0f, 0f, 1f, "миллиметровая сетка под ECG");
    private static readonly SceneParamSpec[] ParamList = { SweepSpec, EventsSpec, GridSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    public string Id => "ekg";

    private static readonly Rgba EcgCol = Rgba.Hex(0x39FF6A);
    private static readonly Rgba PlethCol = Rgba.Hex(0x3AD8FF);
    private static readonly Rgba RespCol = Rgba.Hex(0xFFB23A);
    private static readonly Rgba AlarmCol = Rgba.Hex(0xFFE000);
    private static readonly Rgba HeartCol = Rgba.Hex(0xFF3B3B);
    private static readonly Rgba HeartDim = Rgba.Hex(0x3A1212);
    private static readonly Rgba LabelCol = Rgba.Hex(0x6E8A74);
    private static readonly Rgba TextCol = Rgba.Hex(0xA6C8AE);
    private static readonly Rgba RuleCol = Rgba.Hex(0x1C3324);
    private static readonly Rgba GridCol = Rgba.Hex(0x0F3D20);

    private const int Ecg = 0, Pleth = 1, Resp = 2;
    private const float Look = 0.35f;            // ритм генерируется на 0.35 с вперёд: P-зубец виден до R
    private const int GapCols = 3;               // чёрный зазор перед головкой луча, пиксели
    private const double ScreenMm = 150.0;       // ширина экрана монитора, мм (6 с при 25 мм/с)
    private static readonly Rgba LimitCol = Rgba.Hex(0x2F5E3B);   // пределы тревоги, тускло
    private const int BeatRing = 16;             // степень двойки
    private const int RespRate = 14;             // вдохов/мин

    private struct Beat { public double T; public bool Pvc; }

    private readonly Beat[] _beats = new Beat[BeatRing];
    private long _beatCount, _flashSeq;
    private Random _rng = new(1);
    private double _simT;
    private float _speed = 1f, _sweep = 25f, _events = 0.5f;
    private bool _grid;
    private int _cols, _rows, _labelW, _panelW, _waveCols, _wpx, _rule1, _rule2;
    private readonly int[] _bandRows = new int[3], _bandTop = new int[3], _bandPx = new int[3], _prevY = new int[3];
    private int[] _colMin = Array.Empty<int>(), _colMax = Array.Empty<int>();
    private double _secPerCol;
    private long _colN;
    private bool _lastColLeads;
    private double _leadsA, _leadsB;

    // Генератор ритма (идёт впереди луча).
    private double _nextBeatT;
    private float _hrCur;
    private int _epKind;                        // 0 нет, 1 экстрасистолы, 2 тахикардия, 3 LEADS OFF
    private double _epEnd, _epHr, _nextEpisodeT;
    private bool _audioActive, _audioBeatPending;
    private float _audioBpm;

    // Витальные показатели.
    private double _heartUntil;
    private float _spo2, _spo2Target;
    private double _spo2Next;
    private bool _nibpMeasuring;
    private double _nibpNext, _nibpMeasUntil, _nibpLastT;
    private int _sys, _dia;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _rng = rng;
        _speed = MathF.Max(0f, options.Speed);
        _sweep = Math.Clamp(options.GetFloat(SweepSpec.Name, SweepSpec.Default), SweepSpec.Min, SweepSpec.Max);
        _events = Math.Clamp(options.GetFloat(EventsSpec.Name, EventsSpec.Default), EventsSpec.Min, EventsSpec.Max);
        _grid = options.GetBool(GridSpec.Name, false);
        _cols = cols;
        _rows = rows;

        // Раскладка: слева подписи, справа панель с цифрами, между ними — бегущие развёртки.
        _labelW = cols >= 60 ? 7 : 0;
        _panelW = cols >= 60 ? Math.Min(24, cols / 4) : 0;
        _waveCols = Math.Max(1, cols - _labelW - (_panelW > 0 ? _panelW + 1 : 0));
        _wpx = _waveCols * 2;

        // Доли высоты: ECG ~45%, PLETH ~25%, RESP ~20%, остальное — разделители между полосами.
        int h = Math.Max(3, rows - 2);
        int sep = h >= 30 ? 3 : h >= 12 ? 1 : 0;
        _bandRows[Ecg] = Math.Max(1, (int)(h * 0.45));
        _bandRows[Pleth] = Math.Max(1, (int)(h * 0.25));
        _bandRows[Resp] = Math.Max(1, h - _bandRows[Ecg] - _bandRows[Pleth] - 2 * sep);
        _bandTop[Ecg] = 2;
        _bandTop[Pleth] = _bandTop[Ecg] + _bandRows[Ecg] + sep;
        _bandTop[Resp] = _bandTop[Pleth] + _bandRows[Pleth] + sep;
        _rule1 = sep > 0 ? _bandTop[Pleth] - 1 - sep / 2 : -1;
        _rule2 = sep > 0 ? _bandTop[Resp] - 1 - sep / 2 : -1;
        for (int b = 0; b < 3; b++)
        {
            _bandPx[b] = _bandRows[b] * 3;
            _prevY[b] = -1;
        }

        _colMin = new int[3 * _wpx];
        _colMax = new int[3 * _wpx];
        Array.Fill(_colMin, -1);
        Array.Fill(_colMax, -1);
        _secPerCol = ScreenMm / _sweep / _wpx;
        _colN = 0;

        Array.Clear(_beats);
        _beatCount = 0;
        _flashSeq = 0;
        _simT = 0;
        _nextBeatT = 0.5;
        _hrCur = 77f;
        _epKind = 0;
        _epEnd = 0;
        _epHr = 0;
        _nextEpisodeT = _events > 0f ? 0.3 + 25.0 * (1.0 - _events) : double.PositiveInfinity;
        _leadsA = -1;
        _leadsB = -1;
        _lastColLeads = false;
        _audioActive = false;
        _audioBeatPending = false;
        _audioBpm = 0f;
        _heartUntil = 0;
        _spo2 = 98f;
        _spo2Target = 98f;
        _spo2Next = 15;
        _nibpMeasuring = false;
        _nibpNext = 20;
        _nibpMeasUntil = 0;
        _nibpLastT = 0;
        _sys = 118;
        _dia = 76;
    }

    public void OnAudio(in AudioFrame audio)
    {
        _audioActive = audio.Active;
        _audioBpm = audio.Active ? audio.Bpm : 0f;
        if (audio.Active && audio.Beat) _audioBeatPending = true;
    }

    public void Update(float dt)
    {
        dt = Math.Clamp(dt, 0f, 0.1f);
        if (dt <= 0f) return;
        double step = dt * _speed;
        _simT += step;

        if (_audioBeatPending)
        {
            _audioBeatPending = false;
            AnchorToBeat();
        }
        GenerateBeats();
        WriteColumns();
        FlashBeats();
        UpdateVitals(step);
    }

    public void Draw(CharGrid grid)
    {
        grid.Clear(Rgba.Black);
        bool blinkOn = ((long)(_simT * 2) & 1) == 0;

        // Служебная строка: BED 07  ADULT  ECG II  25 MM/S, сердечко мигает на каждом R.
        grid.Text(3, 0, "BED 07  ADULT  ECG II  ", LabelCol);   // отступ 3 колонки: скругление угла монитора
        int x = PutTenths(grid, 26, 0, (int)Math.Round(_sweep * 10), LabelCol);
        grid.Text(x, 0, " MM/S", LabelCol);
        if (_lastColLeads && blinkOn) grid.Text(36, 0, "LEADS OFF", AlarmCol);
        grid.Put(_cols - 2, 0, '♥', _simT < _heartUntil ? HeartCol : HeartDim);

        for (int cx = 0; cx < _cols; cx++) grid.Put(cx, 1, '─', RuleCol);
        if (_rule1 >= 0) DrawRule(grid, _rule1);
        if (_rule2 >= 0) DrawRule(grid, _rule2);

        if (_labelW > 0)
        {
            grid.Text(0, _bandTop[Ecg], "ECG II", EcgCol);
            grid.Text(0, _bandTop[Pleth], "PLETH", PlethCol);
            grid.Text(0, _bandTop[Resp], "RESP", RespCol);
        }
        if (_panelW > 0)
        {
            for (int y = 2; y < _rows; y++) grid.Put(_cols - _panelW - 1, y, '│', RuleCol);
            DrawPanel(grid, blinkOn);
        }

        DrawBand(grid, Ecg, EcgCol);
        DrawBand(grid, Pleth, PlethCol);
        DrawBand(grid, Resp, RespCol);
    }

    /// <summary>Значение HR на экране (для тестов).</summary>
    internal int ShownHr => (int)Math.Round(_hrCur);

    /// <summary>Эпизод LEADS OFF с текущего момента на seconds секунд (для тестов).</summary>
    internal void ForceLeadsOff(float seconds)
    {
        _leadsA = _simT;
        _leadsB = _simT + seconds;
    }

    // ---- Ритм ----

    /// <summary>
    /// Удар музыки: непройденные удары (ещё не нарисованные) сбрасываются, и ритм начинается заново с этого момента,
    /// так что R-пик встаёт на головку луча ровно на удар.
    /// </summary>
    private void AnchorToBeat()
    {
        if (!_audioActive) return;
        double a = _simT;
        while (_beatCount > 0 && _beats[(_beatCount - 1) & (BeatRing - 1)].T > a - 0.1) _beatCount--;
        if (_flashSeq > _beatCount) _flashSeq = _beatCount;
        _nextBeatT = a;
    }

    private void GenerateBeats()
    {
        while (_nextBeatT <= _simT + Look)
        {
            double t = _nextBeatT;
            if (_epKind == 0 && t >= _nextEpisodeT) StartEpisode(t);
            else if (_epKind != 0 && t >= _epEnd) EndEpisode(t);

            bool pvcNext = _epKind == 1 && _rng.NextDouble() < 0.4;
            AddBeat(t, false);
            _hrCur = HrAt(t);
            double rr = 60.0 / _hrCur * (1.0 + 0.03 * (_rng.NextDouble() - 0.5));
            if (pvcNext)
            {
                AddBeat(t + 0.55 * rr, true);       // преждевременный желудочковый, потом компенсаторная пауза
                _nextBeatT = t + 2.0 * rr;
            }
            else
            {
                _nextBeatT = t + rr;
            }
        }
    }

    private void StartEpisode(double t)
    {
        double u = _rng.NextDouble();
        if (u < 0.4)
        {
            _epKind = 1;
            _epEnd = t + 6 + 4 * _rng.NextDouble();
        }
        else if (u < 0.7)
        {
            _epKind = 2;
            _epHr = 120 + 15 * _rng.NextDouble();
            _epEnd = t + 8 + 4 * _rng.NextDouble();
        }
        else
        {
            _epKind = 3;
            _epEnd = t + 3 + 2 * _rng.NextDouble();
            _leadsA = t;
            _leadsB = _epEnd;
        }
    }

    private void EndEpisode(double t)
    {
        _epKind = 0;
        _nextEpisodeT = t + (180.0 - 120.0 * _events) * (0.7 + 0.6 * _rng.NextDouble());
    }

    private float HrAt(double t)
    {
        if (_epKind == 2) return (float)_epHr;
        if (_audioActive && _audioBpm > 0f) return Math.Clamp(_audioBpm, 40f, 180f);
        return (float)(77.0 + 16.0 * Math.Sin(Math.Tau * t / 97.0) + 3.0 * Math.Sin(Math.Tau * t / 23.0));
    }

    private void AddBeat(double t, bool pvc)
    {
        _beats[_beatCount & (BeatRing - 1)] = new Beat { T = t, Pvc = pvc };
        _beatCount++;
    }

    private void FlashBeats()
    {
        double limit = _simT;
        while (_flashSeq < _beatCount)
        {
            var bt = _beats[_flashSeq & (BeatRing - 1)];
            if (bt.T > limit) break;
            _heartUntil = _simT + 0.2;
            _flashSeq++;
        }
    }

    // ---- Развёртка ----

    private void WriteColumns()
    {
        double target = _simT;
        while (_colN * _secPerCol <= target)
        {
            int pos = (int)(_colN % _wpx);
            double tc = _colN * _secPerCol;
            bool leads = tc >= _leadsA && tc < _leadsB;
            _lastColLeads = leads;
            for (int b = 0; b < 3; b++) WriteBand(b, pos, tc, leads);
            for (int g = 1; g <= GapCols; g++)
            {
                int p = (pos + g) % _wpx;
                for (int b = 0; b < 3; b++) _colMin[b * _wpx + p] = -1;
            }
            _colN++;
        }
    }

    /// <summary>Колонка пикселей: отрезок от предыдущего отсчёта до текущих (вертикальный штрих, линия без разрывов).</summary>
    private void WriteBand(int b, int pos, double tc, bool leads)
    {
        int pb = _bandPx[b];
        int prev = _prevY[b];
        int mn = prev >= 0 ? prev : int.MaxValue;
        int mx = prev >= 0 ? prev : int.MinValue;
        int y = 0;
        for (int k = 0; k < 4; k++)
        {
            y = PixelY(b, tc - _secPerCol * (3 - k) / 3.0, pb, leads);
            if (y < mn) mn = y;
            if (y > mx) mx = y;
        }
        _prevY[b] = y;
        _colMin[b * _wpx + pos] = mn;
        _colMax[b * _wpx + pos] = mx;
    }

    private int PixelY(int b, double t, int pb, bool leads)
    {
        double f;
        switch (b)
        {
            // Размах каждой кривой ~70% полосы; дыхание спокойнее (меньше амплитуда).
            case Ecg:
                f = pb * 0.75 - (leads ? 0.0 : EcgAt(t)) * pb * 0.65;
                break;
            case Pleth:
                f = pb * 0.92 - PlethAt(t) * pb * 0.7;
                break;
            default:
                f = pb * 0.5 - Math.Sin(Math.Tau * RespRate / 60.0 * t) * pb * 0.35;
                break;
        }
        return Math.Clamp((int)Math.Round(f), 0, pb - 1);
    }

    private double EcgAt(double t)
    {
        double sum = 0;
        for (long i = Math.Max(0, _beatCount - BeatRing); i < _beatCount; i++)
        {
            var bt = _beats[i & (BeatRing - 1)];
            double d = t - bt.T;
            if (d < -0.3 || d > 0.7) continue;
            if (bt.Pvc)
                // Экстрасистола: широкий QRS, глубокий S, инвертированный T.
                sum += 1.1 * G(d, 0.0, 0.02) - 0.5 * G(d, 0.05, 0.02) - 0.35 * G(d, 0.28, 0.09);
            else
                // Учебный PQRST: ровная изолиния, узкий QRS (~0.07 с), пологие P и T.
                sum += 0.10 * G(d, -0.16, 0.03) - 0.10 * G(d, -0.025, 0.009) + G(d, 0.0, 0.009)
                       - 0.18 * G(d, 0.025, 0.009) + 0.25 * G(d, 0.24, 0.055);
        }
        return sum;
    }

    private double PlethAt(double t)
    {
        double sum = 0;
        for (long i = Math.Max(0, _beatCount - BeatRing); i < _beatCount; i++)
        {
            var bt = _beats[i & (BeatRing - 1)];
            if (bt.Pvc) continue;                   // при экстрасистоле пульсовой волны нет
            double d = t - bt.T;
            if (d < 0 || d > 1.0) continue;
            sum += G(d, 0.25, 0.08) + 0.35 * G(d, 0.45, 0.07);
        }
        return sum;
    }

    private static double G(double x, double mu, double s)
    {
        double z = (x - mu) / s;
        return Math.Exp(-0.5 * z * z);
    }

    private void UpdateVitals(double step)
    {
        if (_simT >= _spo2Next)
        {
            _spo2Target = 95 + _rng.Next(5);
            _spo2Next = _simT + 15 + 15 * _rng.NextDouble();
        }
        _spo2 += (_spo2Target - _spo2) * (float)(1.0 - Math.Exp(-step * 0.3));

        if (_nibpMeasuring)
        {
            if (_simT >= _nibpMeasUntil)
            {
                _nibpMeasuring = false;
                _sys = 110 + _rng.Next(21);
                _dia = 70 + _rng.Next(13);
                _nibpLastT = _simT;
            }
        }
        else if (_simT >= _nibpNext)
        {
            _nibpMeasuring = true;
            _nibpMeasUntil = _simT + 6;
            _nibpNext = _simT + 55 + 10 * _rng.NextDouble();
        }
    }

    // ---- Рисование ----

    private void DrawBand(CharGrid g, int b, Rgba fg)
    {
        int top = _bandTop[b];
        int baseIdx = b * _wpx;
        bool gridOn = _grid && b == Ecg;
        int gridStep = Math.Max(2, _waveCols / 30);     // 5 мм при 150 мм на экране
        for (int cy = 0; cy < _bandRows[b]; cy++)
        {
            int gy = top + cy;
            if (gy >= _rows) break;
            for (int cx = 0; cx < _waveCols; cx++)
            {
                int bits = 0;
                for (int dx = 0; dx < 2; dx++)
                {
                    int px = cx * 2 + dx;
                    int mn = _colMin[baseIdx + px];
                    if (mn < 0) continue;
                    int mx = _colMax[baseIdx + px];
                    for (int dy = 0; dy < 3; dy++)
                    {
                        int y = cy * 3 + dy;
                        if (y >= mn && y <= mx) bits |= 1 << (dy * 2 + dx);
                    }
                }
                int gx = _labelW + cx;
                if (bits != 0) g.Put(gx, gy, GlyphSet.Sextant(bits), fg, Rgba.Black);
                else if (gridOn && cx % gridStep == 0 && cy % 2 == 0) g.Put(gx, gy, '·', GridCol, Rgba.Black);
            }
        }
    }

    private void DrawPanel(CharGrid grid, bool blinkOn)
    {
        int px0 = _cols - _panelW + 1;
        int pw = _panelW - 2;

        // HR: крупные цифры, пределы тревоги тускло справа (120 над 50).
        grid.Text(px0, 2, "HR", LabelCol);
        DrawBig(grid, px0, 3, (int)Math.Round(_hrCur), 3, EcgCol);
        PutInt(grid, px0 + 12, 3, 120, LimitCol);
        PutInt(grid, px0 + 12, 7, 50, LimitCol);
        DrawRule(grid, 8, px0 - 1);

        // SPO2: пределы 100 над 90.
        grid.Text(px0, 9, "SPO2", PlethCol);
        DrawBig(grid, px0, 10, (int)Math.Round(_spo2), 2, PlethCol);
        PutInt(grid, px0 + 9, 10, 100, LimitCol);
        PutInt(grid, px0 + 9, 14, 90, LimitCol);
        DrawRule(grid, 15, px0 - 1);

        // NIBP: время последнего замера (от суммы dt, старт 14:30), систолическое крупно, диастолическое и MAP рядом.
        grid.Text(px0, 16, "NIBP", TextCol);
        int total = 52200 + (int)_nibpLastT;
        int hh = total / 3600 % 24, mm = total / 60 % 60;
        PutTwo(grid, px0 + pw - 5, 16, hh, LabelCol);
        grid.Put(px0 + pw - 3, 16, ':', LabelCol);
        PutTwo(grid, px0 + pw - 2, 16, mm, LabelCol);
        DrawBig(grid, px0, 17, _sys, 3, TextCol);
        grid.Put(px0 + 12, 18, '/', TextCol);
        PutInt(grid, px0 + 13, 18, _dia, TextCol);
        grid.Put(px0 + 12, 19, '(', TextCol);
        PutInt(grid, px0 + 13, 19, (_sys + 2 * _dia) / 3, TextCol);
        grid.Put(px0 + 16, 19, ')', TextCol);
        if (_nibpMeasuring && blinkOn) grid.Text(px0, 21, "MEASURING", AlarmCol);
        DrawRule(grid, 23, px0 - 1);

        grid.Text(px0, 24, "RESP", RespCol);
        PutInt(grid, px0 + 6, 24, RespRate, RespCol);

        grid.Text(px0, 25, "TEMP", TextCol);
        int tenths = (int)Math.Round((36.6 + 0.15 * Math.Sin(_simT / 200.0)) * 10.0);
        PutTenths(grid, px0 + 6, 25, tenths, TextCol);
    }

    /// <summary>Тонкий разделитель-линия поперёк полосы или панели.</summary>
    private void DrawRule(CharGrid g, int row, int x0 = -1)
    {
        int from = x0 >= 0 ? x0 : _labelW;
        int to = x0 >= 0 ? _cols : _labelW + _waveCols;
        for (int x = from; x < to; x++) g.Put(x, row, '─', RuleCol);
    }

    /// <summary>Два разряда с ведущим нулём.</summary>
    private static void PutTwo(CharGrid g, int x, int y, int v, Rgba fg)
    {
        v = Math.Clamp(v, 0, 99);
        g.Put(x, y, (char)('0' + v / 10), fg);
        g.Put(x + 1, y, (char)('0' + v % 10), fg);
    }

    private static readonly int[] Font =
    {
        7, 5, 5, 5, 7,   // 0
        2, 6, 2, 2, 7,   // 1
        7, 1, 7, 4, 7,   // 2
        7, 1, 7, 1, 7,   // 3
        5, 5, 7, 1, 1,   // 4
        7, 4, 7, 1, 7,   // 5
        7, 4, 7, 5, 7,   // 6
        7, 1, 1, 1, 1,   // 7
        7, 5, 7, 5, 7,   // 8
        7, 5, 7, 1, 7,   // 9
    };

    private static readonly int[] Pow10 = { 1, 10, 100 };

    /// <summary>Крупные цифры 3×5 из блоков: count разрядов, ведущие нули не рисуются.</summary>
    private static void DrawBig(CharGrid g, int x, int y, int v, int count, Rgba fg)
    {
        v = Math.Clamp(v, 0, count == 3 ? 999 : 99);
        bool lead = true;
        for (int i = count - 1; i >= 0; i--)
        {
            int d = v / Pow10[i] % 10;
            if (i > 0 && d == 0 && lead) continue;
            lead = false;
            int x0 = x + (count - 1 - i) * 4;
            for (int r = 0; r < 5; r++)
            {
                int row = Font[d * 5 + r];
                for (int c = 0; c < 3; c++)
                    if (((row >> (2 - c)) & 1) != 0) g.Put(x0 + c, y + r, '█', fg, Rgba.Black);
            }
        }
    }

    /// <summary>Целое 0..999 по одной цифре в ячейку. Возвращает x после последней цифры.</summary>
    private static int PutInt(CharGrid g, int x, int y, int v, Rgba fg)
    {
        v = Math.Clamp(v, 0, 999);
        if (v >= 100) g.Put(x++, y, (char)('0' + v / 100), fg);
        if (v >= 10) g.Put(x++, y, (char)('0' + v / 10 % 10), fg);
        g.Put(x++, y, (char)('0' + v % 10), fg);
        return x;
    }

    /// <summary>Число в десятых: 366 → «36.6», 250 → «25». Возвращает x после последней цифры.</summary>
    private static int PutTenths(CharGrid g, int x, int y, int tenths, Rgba fg)
    {
        tenths = Math.Clamp(tenths, 0, 9999);
        x = PutInt(g, x, y, tenths / 10, fg);
        if (tenths % 10 != 0)
        {
            g.Put(x++, y, '.', fg);
            g.Put(x++, y, (char)('0' + tenths % 10), fg);
        }
        return x;
    }
}
