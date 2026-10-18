using System.Runtime.CompilerServices;
using Phosphor.Core;

// Внутренние хуки для тестов. Атрибут дублируется в файлах сцен — AllowMultiple, это безопасно.
[assembly: InternalsVisibleTo("Phosphor.Scenes.Tests")]

namespace Phosphor.Scenes;

/// <summary>
/// Сейсмограф: станции друг под другом (2–8 каналов), трассы бегут справа налево и рисуются секстелями (2×3 подточки
/// на ячейку). Фон — микросейсмический шум из синусов 0.1–1 Гц. Раз в 40–120 с приходит землетрясение: на каждом канале
/// сначала P (быстрая мелкая волна), через несколько секунд S (крупнее), затем хвост поверхностных волн. В момент
/// вступления над трассой метка P или S, после события в правом верхнем углу карточка.
/// Звук: бас усиливает шум, сильный удар даёт толчок на всех каналах сразу.
/// </summary>
public sealed class SeismoScene : IScene, IAudioReactive, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec ChannelsSpec = new("channels", SceneParamKind.Int, 5f, 2f, 8f, "каналов (станций)");
    public static readonly SceneParamSpec QuakeSpec = new("quakeRate", SceneParamKind.Float, 0.4f, 0f, 1f, "частота землетрясений, 0 — нет");
    public static readonly SceneParamSpec GainSpec = new("gain", SceneParamKind.Float, 1f, 0.3f, 3f, "усиление трасс");
    private static readonly SceneParamSpec[] ParamList = { ChannelsSpec, QuakeSpec, GainSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    public string Id => "seismo";

    private const double SubPerSec = 10.0;      // подколонок трассы в секунду: окно ≈ 30–40 с
    private const double TwoPi = 2.0 * Math.PI;
    private const int LabelWidth = 16;           // колонка подписей; шапка "UTC hh:mm:ss" с отступом 3 колонки от края
    private const int CornerInset = 3;           // угловые зоны экрана скруглены ScreenPass: там ничего важного
    private const double NoTime = -1e9;          // «прихода не было»
    private const int BgSines = 7;               // синусов фона на канал
    private const float BgAmpRows = 0.1f;        // амплитуда одного синуса фона, строки (пик фона ≈ 0.3–0.5 строки)

    private static readonly string[] Stations = { "MOS", "OBN", "ARU", "KIV", "NRS", "YAK", "SKZ", "ZOB" };
    private static readonly string[] Comps = { "BHZ", "BHN", "BHE" };

    private static readonly Rgba TraceColor = Rgba.Hex(0xD0D4D0);
    private static readonly Rgba GridColor = Rgba.Hex(0x1F5A2E);
    private static readonly Rgba RulerColor = Rgba.Hex(0x3F8A50);
    private static readonly Rgba LabelColor = Rgba.Hex(0x2FB050);
    private static readonly Rgba PColor = Rgba.Hex(0x4DA6FF);
    private static readonly Rgba SColor = Rgba.Hex(0xFF9A3C);
    private static readonly Rgba CardBg = Rgba.Hex(0x06100A);

    private Random _rng = new(0);
    private int _cols, _rows, _labelW, _traceCols, _traceRows, _traceTop, _ch, _W2, _H3;
    private bool _header, _footer;
    private float _speed, _gain, _quakeRate;
    private double _t;                  // время от 00:00:00, сумма dt
    private long _colIndex;             // номер последней сгенерированной подколонки (время = _colIndex / SubPerSec)
    private string[] _labels = Array.Empty<string>();
    private int[] _bandR0 = Array.Empty<int>(), _bandR1 = Array.Empty<int>();   // строки ячеек полосы канала
    private float[] _fac = Array.Empty<float>();      // дальность канала: доля от дальности эпицентра
    // Фон: BgSines несоизмеримых синусов на канал, у каждого медленно плавает амплитуда.
    private float[] _nf = Array.Empty<float>(), _np = Array.Empty<float>();      // частоты 0.1–1.2 Гц и фазы
    private float[] _nmf = Array.Empty<float>(), _nmp = Array.Empty<float>();    // медленная модуляция амплитуды
    private float[] _noiseK = Array.Empty<float>();   // строка в долях полуполосы канала
    private float[] _hist = Array.Empty<float>();     // сырые значения трасс: канал × подколонка (правая — новая)
    private bool[] _trace = Array.Empty<bool>(), _grid = Array.Empty<bool>();
    private byte[] _mark = Array.Empty<byte>();       // 1 — метка P, 2 — метка S
    private int[] _pCell = Array.Empty<int>(), _sCell = Array.Empty<int>();   // ячейка метки над трассой, -1 нет

    // Землетрясение.
    private bool _qOn;
    private int _eventNo, _qD, _qDepth;
    private float _qMag;
    private double _qT0, _qEnd, _nextQuake;
    private double[] _qTp = Array.Empty<double>(), _qTs = Array.Empty<double>();
    private float[] _qAp = Array.Empty<float>(), _qAs = Array.Empty<float>(), _qTau = Array.Empty<float>();

    // Звук.
    private bool _audioActive;
    private float _bass, _bassSm, _jolt;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _rng = rng;
        _cols = Math.Max(1, cols);
        _rows = Math.Max(1, rows);
        _speed = MathF.Max(0.05f, options.Speed);
        int chWanted = Math.Clamp(options.GetInt(ChannelsSpec.Name, (int)ChannelsSpec.Default), (int)ChannelsSpec.Min, (int)ChannelsSpec.Max);
        _quakeRate = Math.Clamp(options.GetFloat(QuakeSpec.Name, QuakeSpec.Default), QuakeSpec.Min, QuakeSpec.Max);
        _gain = Math.Clamp(options.GetFloat(GainSpec.Name, GainSpec.Default), GainSpec.Min, GainSpec.Max);

        // Разметка: шапка со шкалой времени сверху, строка статуса снизу, между ними трассы; слева подписи каналов.
        _header = _rows >= 2;
        _footer = _rows >= 3;
        _labelW = _cols >= 40 ? LabelWidth : 0;
        _traceCols = Math.Max(1, _cols - _labelW);
        _traceTop = _header ? 1 : 0;
        _traceRows = Math.Max(1, _rows - _traceTop - (_footer ? 1 : 0));
        _W2 = 2 * _traceCols;
        _H3 = 3 * _traceRows;
        _ch = Math.Min(chWanted, _traceRows);

        _bandR0 = new int[_ch];
        _bandR1 = new int[_ch];
        _labels = new string[_ch];
        _fac = new float[_ch];
        _nf = new float[_ch * BgSines];
        _np = new float[_ch * BgSines];
        _nmf = new float[_ch * BgSines];
        _nmp = new float[_ch * BgSines];
        _noiseK = new float[_ch];
        _hist = new float[_ch * _W2];
        _trace = new bool[_W2 * _H3];
        _grid = new bool[_W2 * _H3];
        _mark = new byte[_W2 * _H3];
        _pCell = new int[_ch];
        _sCell = new int[_ch];
        _qTp = new double[_ch];
        _qTs = new double[_ch];
        _qAp = new float[_ch];
        _qAs = new float[_ch];
        _qTau = new float[_ch];

        int startStation = _rng.Next(Stations.Length);
        for (int c = 0; c < _ch; c++)
        {
            _bandR0[c] = c * _traceRows / _ch;
            _bandR1[c] = (c + 1) * _traceRows / _ch;
            _labels[c] = Stations[(startStation + c) % Stations.Length] + " " + Comps[_rng.Next(Comps.Length)];
            // Дальность растёт с номером канала: канал 0 — ближайшая станция.
            _fac[c] = (c + 0.5f * _rng.NextSingle()) / _ch;
            // Полуполоса = bandRows/2 строк; 1 строка = 2/bandRows доли полуполосы.
            _noiseK[c] = 2f / (_bandR1[c] - _bandR0[c]);
            for (int k = 0; k < BgSines; k++)
            {
                int i = c * BgSines + k;
                _nf[i] = 0.1f + 1.1f * _rng.NextSingle();
                _np[i] = MathF.Tau * _rng.NextSingle();
                _nmf[i] = 0.02f + 0.03f * _rng.NextSingle();
                _nmp[i] = MathF.Tau * _rng.NextSingle();
            }
            _pCell[c] = -1;
            _sCell[c] = -1;
            _qTp[c] = NoTime;
            _qTs[c] = NoTime;
            _qAp[c] = 0f;
            _qAs[c] = 0f;
            _qTau[c] = 1f;
        }

        _t = 0.0;
        _qOn = false;
        _eventNo = 0;
        _qD = 0;
        _qDepth = 0;
        _qMag = 0f;
        _qT0 = NoTime;
        _qEnd = NoTime;
        // Первое землетрясение: при частоте 1 — почти сразу, при 0 — никогда.
        _nextQuake = _quakeRate > 0f ? 1.0 + 5.0 * (1.0 - _quakeRate) * (0.5 + _rng.NextDouble()) : double.MaxValue;
        _audioActive = false;
        _bass = 0f;
        _bassSm = 0f;
        _jolt = 0f;

        // Трасса уже идёт до старта: заполняем всю ширину шумом (от -W2 до 0).
        _colIndex = -(long)_W2;
        while (_colIndex < 0)
        {
            _colIndex++;
            Generate(_colIndex);
        }
        RebuildRaster();
    }

    public void OnAudio(in AudioFrame audio)
    {
        _audioActive = audio.Active;
        if (!audio.Active)
        {
            _bass = 0f;
            return;
        }
        _bass = Math.Clamp(audio.Bass, 0f, 1f);
        // Сильный удар — толчок на всех каналах сразу.
        if (audio.Beat && _bass > 0.6f) _jolt = 1f;
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;
        dt = MathF.Min(dt, 0.1f);
        _t += dt * _speed;

        float k = MathF.Min(1f, dt * 6f);
        _bassSm += ((_audioActive ? _bass : 0f) - _bassSm) * k;
        _jolt *= MathF.Exp(-dt * 3f);

        if (_qOn)
        {
            if (_t >= _qEnd)
            {
                _qOn = false;
                _nextQuake = _t + (120.0 - 80.0 * _quakeRate) * (0.8 + 0.4 * _rng.NextDouble());
            }
        }
        else if (_t >= _nextQuake)
        {
            StartQuake(2.5f + 3.5f * _rng.NextSingle(), 50f + 350f * _rng.NextSingle());
        }

        // Подколонки появляются справа с частотой SubPerSec, трасса ползёт справа налево.
        while ((_colIndex + 1) / SubPerSec <= _t)
        {
            _colIndex++;
            Generate(_colIndex);
        }
        RebuildRaster();
    }

    public void Draw(CharGrid grid)
    {
        int w = _W2;
        for (int cy = 0; cy < grid.Rows; cy++)
        for (int cx = 0; cx < grid.Cols; cx++)
        {
            int tx = cx - _labelW, ty = cy - _traceTop;
            bool inTrace = tx >= 0 && tx < _traceCols && ty >= 0 && ty < _traceRows;
            if (!inTrace)
            {
                if (_header && cy == 0 && tx >= 0 && tx < _traceCols)
                {
                    // Шкала времени: большая риска каждые 5 с, малая каждую секунду.
                    long m0 = _colIndex - (w - 1 - 2 * tx);
                    if (Hit(m0, 50) || Hit(m0 + 1, 50)) grid.Put(cx, cy, '┴', RulerColor, Rgba.Black);
                    else if (Hit(m0, 10) || Hit(m0 + 1, 10)) grid.Put(cx, cy, '·', GridColor, Rgba.Black);
                    else grid.Put(cx, cy, ' ', RulerColor, Rgba.Black);
                }
                else grid.Put(cx, cy, ' ', GridColor, Rgba.Black);
                continue;
            }

            // Ячейка = 2×3 подточки. Приоритет: трасса, метка P/S, сетка.
            int bx = 2 * tx, by = 3 * ty;
            int tb = 0, pm = 0, sm = 0, gb = 0;
            for (int sy = 0; sy < 3; sy++)
            for (int sx = 0; sx < 2; sx++)
            {
                int idx = (by + sy) * w + bx + sx;
                int bit = 1 << (sy * 2 + sx);
                if (_trace[idx]) tb |= bit;
                if (_mark[idx] == 1) pm |= bit;
                else if (_mark[idx] == 2) sm |= bit;
                if (_grid[idx]) gb |= bit;
            }
            if (tb != 0) grid.Put(cx, cy, GlyphSet.Sextant(tb), TraceColor, Rgba.Black);
            else if (pm != 0) grid.Put(cx, cy, GlyphSet.Sextant(pm), PColor, Rgba.Black);
            else if (sm != 0) grid.Put(cx, cy, GlyphSet.Sextant(sm), SColor, Rgba.Black);
            else if (gb != 0) grid.Put(cx, cy, GlyphSet.Sextant(gb), GridColor, Rgba.Black);
            else grid.Put(cx, cy, ' ', GridColor, Rgba.Black);
        }

        // Подписи каналов и буквы P/S над трассой.
        for (int c = 0; c < _ch; c++)
        {
            if (_labelW > 0)
            {
                int row = _traceTop + (_bandR0[c] + _bandR1[c] - 1) / 2;
                PutStr(grid, 0, row, _labels[c], LabelColor, Rgba.Black);
            }
            if (_pCell[c] >= 0) grid.Put(_labelW + _pCell[c], _traceTop + _bandR0[c], 'P', PColor, Rgba.Black);
            if (_sCell[c] >= 0) grid.Put(_labelW + _sCell[c], _traceTop + _bandR0[c], 'S', SColor, Rgba.Black);
        }

        // Шапка: UTC hh:mm:ss от суммы dt.
        if (_header && _labelW > 0)
        {
            long secs = (long)_t % 86400;
            int x = PutStr(grid, CornerInset, 0, "UTC ", LabelColor, Rgba.Black);
            x = PutNum(grid, x, 0, (int)(secs / 3600), 2, LabelColor, Rgba.Black);
            grid.Put(x++, 0, ':', LabelColor, Rgba.Black);
            x = PutNum(grid, x, 0, (int)(secs / 60 % 60), 2, LabelColor, Rgba.Black);
            grid.Put(x++, 0, ':', LabelColor, Rgba.Black);
            PutNum(grid, x, 0, (int)(secs % 60), 2, LabelColor, Rgba.Black);
        }

        // Строка статуса.
        if (_footer)
        {
            int y = grid.Rows - 1;
            int x = PutStr(grid, CornerInset, y, "STATIONS ", RulerColor, Rgba.Black);
            x = PutNum(grid, x, y, _ch, 1, RulerColor, Rgba.Black);
            x = PutStr(grid, x, y, "  GAIN ", RulerColor, Rgba.Black);
            int g10 = (int)MathF.Round(_gain * 10f);
            x = PutNum(grid, x, y, g10 / 10, 1, RulerColor, Rgba.Black);
            grid.Put(x++, y, '.', RulerColor, Rgba.Black);
            x = PutNum(grid, x, y, g10 % 10, 1, RulerColor, Rgba.Black);
            PutStr(grid, x, y, "  SAMPLE 10 HZ", RulerColor, Rgba.Black);
        }

        // Карточка события в правом верхнем углу, 40 с после начала землетрясения.
        if (_header && _eventNo > 0 && _t - _qT0 < 40.0 && grid.Cols >= 60 && grid.Rows >= _traceTop + 3)
        {
            const int boxW = 42;
            int x0 = grid.Cols - boxW - CornerInset, y0 = _traceTop;   // 1 строка от верха, 3 колонки от правого края
            for (int yy = 0; yy < 3; yy++)
            for (int xx = 0; xx < boxW; xx++)
            {
                char ch;
                Rgba fg = RulerColor;
                if (yy == 0) ch = xx == 0 ? '┌' : xx == boxW - 1 ? '┐' : '─';
                else if (yy == 2) ch = xx == 0 ? '└' : xx == boxW - 1 ? '┘' : '─';
                else
                {
                    ch = xx == 0 || xx == boxW - 1 ? '│' : ' ';
                    fg = TraceColor;
                }
                grid.Put(x0 + xx, y0 + yy, ch, fg, CardBg);
            }
            int m10 = (int)MathF.Round(_qMag * 10f);
            int tx = PutStr(grid, x0 + 1, y0 + 1, "EVENT ", TraceColor, CardBg);
            tx = PutNum(grid, tx, y0 + 1, _eventNo, 2, TraceColor, CardBg);
            tx = PutStr(grid, tx, y0 + 1, "  M", TraceColor, CardBg);
            tx = PutNum(grid, tx, y0 + 1, m10 / 10, 1, TraceColor, CardBg);
            grid.Put(tx++, y0 + 1, '.', TraceColor, CardBg);
            tx = PutNum(grid, tx, y0 + 1, m10 % 10, 1, TraceColor, CardBg);
            tx = PutStr(grid, tx, y0 + 1, "  DEPTH ", TraceColor, CardBg);
            tx = PutNum(grid, tx, y0 + 1, _qDepth, 2, TraceColor, CardBg);
            tx = PutStr(grid, tx, y0 + 1, " KM  DIST ", TraceColor, CardBg);
            tx = PutNum(grid, tx, y0 + 1, _qD, 3, TraceColor, CardBg);
            PutStr(grid, tx, y0 + 1, " KM", TraceColor, CardBg);
        }
    }

    /// <summary>Сырое значение трассы канала в подколонке x (0 — самая старая). Для тестов.</summary>
    internal float TraceAt(int channel, int x) => _hist[channel * _W2 + x];

    /// <summary>Время подколонки x в секундах от 00:00:00. Для тестов.</summary>
    internal double TimeAt(int x) => (_colIndex - (_W2 - 1 - x)) / SubPerSec;

    /// <summary>Число подколонок трассы. Для тестов.</summary>
    internal int SubColumns => _W2;

    /// <summary>Видимое число каналов. Для тестов.</summary>
    internal int Channels => _ch;

    /// <summary>Текущий толчок от удара (1 в момент удара, затухает). Для тестов.</summary>
    internal float Jolt => _jolt;

    /// <summary>Время вступления P на канале (сек от 00:00:00) текущего землетрясения. Для тестов.</summary>
    internal double PArrival(int channel) => _qTp[channel];

    /// <summary>Время вступления S на канале текущего землетрясения. Для тестов.</summary>
    internal double SArrival(int channel) => _qTs[channel];

    /// <summary>Запустить землетрясение сейчас с заданной магнитудой и дальностью. Для тестов.</summary>
    internal void ForceQuake(float magnitude, float distanceKm) => StartQuake(magnitude, distanceKm);

    private void StartQuake(float mag, float distKm)
    {
        _qOn = true;
        _eventNo = _eventNo % 99 + 1;
        _qT0 = _t;
        _qMag = mag;
        _qD = (int)MathF.Round(distKm);
        _qDepth = 3 + _rng.Next(28);
        // Размах растёт с магнитудой (M2.5 — слабо, M6 — почти весь полуполоса на ближних станциях),
        // падает с расстоянием. P около 2–3 фонов, S и хвост — 70–90% полуполосы на ближних.
        float mS = 0.25f + 0.75f * Math.Clamp((mag - 2.5f) / 3.5f, 0f, 1f);
        float tailTau = 10f + 20f * Math.Clamp((mag - 2.5f) / 3.5f, 0f, 1f);
        double end = _qT0;
        for (int c = 0; c < _ch; c++)
        {
            float d = distKm * (0.6f + 0.8f * _fac[c]);
            float att = 1f / (1f + d / 400f);
            _qTp[c] = _qT0 + 0.5 + d / 50.0;   // P: быстрее
            _qTs[c] = _qT0 + 0.5 + d / 20.0;   // S: заметно позже, разрыв растёт с дальностью
            _qAp[c] = 0.7f * mS * att;   // P: пачка в 2–3 фона на ближних
            _qAs[c] = 1.2f * mS * att;   // S: ≥4 фонов при M4.5 вблизи, у края полосы при M5+
            _qTau[c] = tailTau;
            end = Math.Max(end, _qTs[c] + 2.5 * tailTau);
        }
        _qEnd = end;
    }

    /// <summary>Генерирует подколонку с номером k для всех каналов и сдвигает трассы влево.</summary>
    private void Generate(long col)
    {
        double tau = col / SubPerSec;
        float noiseMul = 1f + 1.5f * _bassSm;      // бас качает микросейсмический шум
        for (int c = 0; c < _ch; c++)
        {
            // Плавная волна без случайного дрожания: сумма трёх синусов, около 1 строки амплитуды.
            double v = 0;
            for (int k = 0; k < BgSines; k++)
            {
                int i = c * BgSines + k;
                double env = 0.6 + 0.4 * Math.Sin(TwoPi * _nmf[i] * tau + _nmp[i]);
                v += env * Math.Sin(TwoPi * _nf[i] * tau + _np[i]);
            }
            v *= BgAmpRows * _noiseK[c] * noiseMul;

            if (_qOn)
            {
                double tp = _qTp[c], ts = _qTs[c];
                if (tau > tp)
                {
                    double u = tau - tp;
                    v += _qAp[c] * Math.Min(1.0, u / 0.15) * Math.Exp(-u / 0.8) * Math.Sin(TwoPi * 2.5 * u);
                }
                if (tau > ts)
                {
                    double s = tau - ts;
                    v += _qAs[c] * Math.Min(1.0, s / 0.4) * Math.Exp(-s / 4.0) * Math.Sin(TwoPi * 1.2 * s);
                    // Хвост: длиннопериодные затухающие колебания 0.1–0.3 Гц, медленнее и шире самой пачки S.
                    double tail = 0.7 * Math.Sin(TwoPi * 0.2 * s) + 0.3 * Math.Sin(TwoPi * 0.13 * s + 1.0);
                    v += 0.7 * _qAs[c] * Math.Min(1.0, s / 1.5) * Math.Exp(-s / _qTau[c]) * tail;
                }
            }

            if (_jolt > 0.001f) v += _jolt * 0.3 * Math.Sin(TwoPi * 2.2 * tau);

            int b = c * _W2;
            Array.Copy(_hist, b + 1, _hist, b, _W2 - 1);
            _hist[b + _W2 - 1] = (float)v;
        }
    }

    /// <summary>Перестраивает растры трасс, сетки и меток из буфера истории. Без аллокаций.</summary>
    private void RebuildRaster()
    {
        Array.Clear(_trace);
        Array.Clear(_grid);
        Array.Clear(_mark);
        int w = _W2;

        // Сетка: вертикали каждые 5 с (пунктиром) и горизонталь по центру полосы.
        for (int x = 0; x < w; x++)
        {
            if (!Hit(_colIndex - (w - 1 - x), 50)) continue;
            for (int y = 0; y < _H3; y += 2) _grid[y * w + x] = true;
        }
        for (int c = 0; c < _ch; c++)
        {
            int top = 3 * _bandR0[c], bot = 3 * _bandR1[c] - 1, mid = (top + bot) / 2;
            for (int x = 0; x < w; x += 2) _grid[mid * w + x] = true;
        }

        // Трассы: соседние подколонки соединяются вертикальным штрихом.
        for (int c = 0; c < _ch; c++)
        {
            int top = 3 * _bandR0[c], bot = 3 * _bandR1[c] - 1;
            float mid = (top + bot) * 0.5f, half = (bot - top) * 0.5f * 0.92f;
            int prev = -1;
            for (int x = 0; x < w; x++)
            {
                float v = Math.Clamp(_hist[c * w + x] * _gain, -1f, 1f);
                int y = Math.Clamp((int)MathF.Round(mid - v * half), top, bot);
                int lo = prev < 0 ? y : Math.Min(prev, y);
                int hi = prev < 0 ? y : Math.Max(prev, y);
                for (int yy = lo; yy <= hi; yy++) _trace[yy * w + x] = true;
                prev = y;
            }
        }

        // Метки P и S в момент вступления, пока оно попадает в окно трассы.
        for (int c = 0; c < _ch; c++)
        {
            _pCell[c] = -1;
            _sCell[c] = -1;
            if (_eventNo == 0) continue;
            int top = 3 * _bandR0[c], bot = 3 * _bandR1[c] - 1;
            int xp = ArrivalColumn(_qTp[c]);
            int xs = ArrivalColumn(_qTs[c]);
            if (xp >= 0)
            {
                _pCell[c] = xp / 2;
                MarkColumn(xp, top, bot, 1);
            }
            if (xs >= 0)
            {
                _sCell[c] = xs / 2;
                MarkColumn(xs, top, bot, 2);
            }
        }
    }

    private int ArrivalColumn(double t)
    {
        long m = (long)Math.Round(t * SubPerSec);
        long x = (_W2 - 1) - (_colIndex - m);
        return x >= 0 && x < _W2 ? (int)x : -1;
    }

    private void MarkColumn(int x, int top, int bot, byte kind)
    {
        for (int y = top; y <= bot; y += 2) _mark[y * _W2 + x] = kind;
    }

    private static bool Hit(long m, long period) => ((m % period) + period) % period == 0;

    private static int PutStr(CharGrid g, int x, int y, string s, Rgba fg, Rgba bg)
    {
        for (int i = 0; i < s.Length; i++) g.Put(x + i, y, s[i], fg, bg);
        return x + s.Length;
    }

    private static int PutNum(CharGrid g, int x, int y, int v, int digits, Rgba fg, Rgba bg)
    {
        int p = 1;
        for (int i = 1; i < digits; i++) p *= 10;
        for (int i = 0; i < digits; i++)
        {
            g.Put(x + i, y, (char)('0' + (v / p) % 10), fg, bg);
            p /= 10;
        }
        return x + digits;
    }
}
