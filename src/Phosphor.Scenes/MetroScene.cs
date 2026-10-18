using Phosphor.Core;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Phosphor.Scenes.Tests")]

namespace Phosphor.Scenes;

/// <summary>
/// Схема линий метро на табло диспетчерской: линии на субпиксельном холсте секстелей (2×3 на ячейку),
/// толщиной в 2 субпикселя, с пересадками, станциями и поездами. Поезда одного направления идут по одной
/// колее, встречного — по другой, поэтому каждый поезд разворачивается на конечной сам. Раз в несколько минут
/// схема стирается по линиям и рисуется заново. Звук не нужен: сцена живёт своим ходом.
/// </summary>
public sealed class MetroScene : IScene, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec LinesSpec = new("lines", SceneParamKind.Int, 4f, 3f, 6f, "число линий");
    public static readonly SceneParamSpec TrainsSpec = new("trains", SceneParamKind.Int, 4f, 1f, 5f, "поездов на линию");
    public static readonly SceneParamSpec LabelsSpec = new("labels", SceneParamKind.Bool, 1f, 0f, 1f, "подписи станций");
    private static readonly SceneParamSpec[] ParamList = { LinesSpec, TrainsSpec, LabelsSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    public string Id => "metro";

    private const int MaxLines = 6;
    private const int MaxTrains = 5;
    private const int MaxHubs = 4;
    private const int BodyLen = 4;          // длина поезда, субпикселей
    private const int TrainSpacing = 8;     // минимальный шаг между поездами одного направления
    private const int NameCap = 12;
    private const int ConfCap = 8192;
    private const int MaxStrayClusters = 2; // точечных перекрёстков вне узлов на схему
    private const int MaxStrayPx = 16;      // субпикселей, занятых двумя линиями вне узлов, на всю схему
    private const int HubCoreX = 4, HubCoreY = 5;   // зона узла в субпикселях: здесь пересечения допустимы
    private const int LabelGap = 2;         // пустых ячеек между подписями на одной строке
    private const int MaxLineForeign = 12;  // субпикселей чужих линий, которые трасса может пересечь (перекрёстки)
    private const float HubSepPx = 10f;     // минимальное расстояние между пересадками, колонок
    private const int OuterTries = 400;
    private const int LineTries = 40;
    private const float VMax = 20f;         // субпикселей в секунду (≈10 ячеек/с по горизонтали)
    private const float Accel = 12f, Decel = 24f;
    private const float BrakeRange = 24f, GapSubs = 7f, GapRange = 30f;
    private const float RebuildSec = 180f, EraseSec = 2.4f, BuildSec = 3f;
    private const float SelectSec = 10f;
    private const float TickerSpeed = 8f;
    private const char StationGlyph = '●', RingGlyph = '○';
    private const string NormalMsg = "+++ SERVICE NORMAL ON ALL LINES +++";
    private const string DelayMsg = "+++ LINE ";
    private const string DelayTail = " DELAYED · SIGNAL FAILURE +++";

    private static readonly Rgba[] LineColors =
    {
        new(230, 50, 50), new(50, 110, 230), new(40, 180, 90),
        new(245, 140, 30), new(150, 80, 220), new(40, 190, 200),
    };
    private static readonly Rgba Ink = new(200, 200, 200);
    private static readonly Rgba Dim = new(110, 110, 110);
    private static readonly Rgba LabelFg = new(150, 150, 150);
    private static readonly string[] SylA = { "KO", "ZA", "MI", "RE", "TOR", "SKA", "NO", "PEL", "BRA", "LU", "DA", "OR", "TI", "VE", "KRA", "SA" };
    private static readonly string[] SylB = { "VAL", "RO", "NE", "MO", "BEL", "TIN", "DOR", "SK", "LAK", "ZEN", "VA", "GRA", "PO", "KOV" };

    private enum Phase { Running, Erase, Build }

    /// <summary>Одна схема: линии как цепочки субпикселей, станции, подписи, поезда.</summary>
    private sealed class Scheme
    {
        public int Lines, Transfers, StationCount, LabelCount;
        public readonly int[] Len = new int[MaxLines];
        public readonly int[][] PX, PY;            // центральная линия линии k, субпиксельные координаты
        public readonly bool[][] Station;
        public readonly int[] StnCell, StnIdx, StnLine;
        public readonly bool[] StnShared;
        public readonly int[] LabelCell;
        public readonly char[] LabelChar;
        public readonly char[] NameBuf;
        public readonly byte[] NameLen;
        public readonly int[] TrainCount = new int[MaxLines];
        public readonly double[] TrU = new double[MaxLines * MaxTrains];
        public readonly float[] TrV = new float[MaxLines * MaxTrains];
        public readonly float[] TrWait = new float[MaxLines * MaxTrains];
        public readonly bool[] TrAt = new bool[MaxLines * MaxTrains];

        public readonly int[] LineMask = new int[MaxLines];   // бит j — линия проходит через узел j
        public readonly int[] HubSx = new int[MaxHubs], HubSy = new int[MaxHubs];
        public int HubCount, StrayClusters, StrayPixels;
        public readonly int[] WordX, WordY, WordLen;   // подписи (слова) — для проверки зазоров
        public int WordCount;

        public Scheme(int cells, int pathCap)
        {
            WordX = new int[cells];
            WordY = new int[cells];
            WordLen = new int[cells];
            PX = new int[MaxLines][];
            PY = new int[MaxLines][];
            Station = new bool[MaxLines][];
            for (int k = 0; k < MaxLines; k++)
            {
                PX[k] = new int[pathCap];
                PY[k] = new int[pathCap];
                Station[k] = new bool[pathCap];
            }
            int stCap = MaxLines * pathCap;
            StnCell = new int[stCap];
            StnIdx = new int[stCap];
            StnLine = new int[stCap];
            StnShared = new bool[stCap];
            LabelCell = new int[cells];
            LabelChar = new char[cells];
            NameBuf = new char[cells * NameCap];
            NameLen = new byte[cells];
        }
    }

    private int _cols, _rows;
    private Random _rng = new(0);
    private float _speed = 1f;
    private int _linesWant = 4, _trainsWant = 3;
    private bool _labelsOn = true, _hudOn, _trainsVisible = true, _geomOK;
    private int _hudX;
    private int _bx0, _bx1, _by0, _by1;        // границы схемы в ячейках, включительно
    private int _pathCap, _subW, _subH;
    private Scheme _cur = new(0, 1), _next = new(0, 1);
    private int _schemeNo;
    private Phase _phase;
    private float _phaseT, _toRebuild;
    private double _clock;
    private float _eventT;
    private readonly float[] _delayLeft = new float[MaxLines];
    private char[] _tick = Array.Empty<char>();
    private int _tickLen, _tickState = -1;     // номер линии с задержкой или -1
    private float _tickOff;
    private bool _tickAlert;
    private int _sel;
    private float _selT;
    private int _depN;
    private readonly int[] _depLine = new int[4], _depDest = new int[4], _depMin = new int[4];

    // Рабочие буферы (выделяются в Init, в Update и Draw не аллоцируются).
    private int[] _subOwner = Array.Empty<int>();
    private int[] _confX = new int[ConfCap], _confY = new int[ConfCap];
    private int _confN;
    private bool[] _taken = Array.Empty<bool>();
    private bool[] _labDone = Array.Empty<bool>();
    private bool[] _labOcc = Array.Empty<bool>();
    private int[] _bits = Array.Empty<int>();
    private Rgba[] _bcol = Array.Empty<Rgba>();
    private double[] _cum = Array.Empty<double>();
    private readonly double[] _vx = new double[64], _vy = new double[64];
    private int _vn;
    private int _pl;
    private Scheme _bs = null!;
    private int _bk;
    private int _nT;
    private readonly double[] _hubPx = new double[MaxHubs], _hubPy = new double[MaxHubs];
    private readonly int[] _hubSx = new int[MaxHubs], _hubSy = new int[MaxHubs], _hubUse = new int[MaxHubs];
    private readonly int[] _lineMask = new int[MaxLines];
    private readonly int[] _strayX = new int[ConfCap], _strayY = new int[ConfCap], _stk = new int[ConfCap];
    private readonly bool[] _strayMark = new bool[ConfCap];

    /// <summary>Тестовый хук: рисовать ли поезда.</summary>
    internal bool TrainsVisible { get => _trainsVisible; set => _trainsVisible = value; }
    internal int LineCountNow => _cur.Lines;
    internal int TransfersNow => _cur.Transfers;
    internal int StationCountNow => _cur.StationCount;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = cols;
        _rows = rows;
        _rng = rng;
        _speed = MathF.Max(0f, options.Speed);
        _linesWant = Math.Clamp(options.GetInt(LinesSpec.Name, (int)LinesSpec.Default), (int)LinesSpec.Min, (int)LinesSpec.Max);
        _trainsWant = Math.Clamp(options.GetInt(TrainsSpec.Name, (int)TrainsSpec.Default), (int)TrainsSpec.Min, (int)TrainsSpec.Max);
        _labelsOn = options.GetBool(LabelsSpec.Name, LabelsSpec.Default > 0.5f);
        _trainsVisible = true;

        // Разметка: сверху заголовок, снизу бегущая строка, справа панель табло.
        _hudOn = cols >= 90 && rows >= 24;
        int hudW = _hudOn ? Math.Clamp(cols / 4, 28, 44) : 0;
        _hudX = cols - hudW;
        int mapX1 = _hudOn ? _hudX - 3 : cols - 2;
        _bx0 = 2; _bx1 = mapX1; _by0 = 2; _by1 = rows - 3;
        _geomOK = _bx1 - _bx0 >= 40 && _by1 - _by0 >= 12;

        _pathCap = 4 * (2 * cols + 3 * rows) + 64;
        _subW = 2 * cols;
        _subH = 3 * rows;
        int cells = cols * rows;
        _subOwner = new int[_subW * _subH];
        _taken = new bool[cells];
        _labDone = new bool[cells];
        _labOcc = new bool[cells];
        _bits = new int[cells];
        _bcol = new Rgba[cells];
        _cum = new double[_pathCap];
        _tick = new char[cols + 80];
        _cur = new Scheme(cells, _pathCap);
        _next = new Scheme(cells, _pathCap);
        _confN = 0;

        _schemeNo = 0;
        _phase = Phase.Running;
        _phaseT = 0f;
        _toRebuild = RebuildSec;
        _clock = 0.0;
        _eventT = 60f + rng.NextSingle() * 60f;
        Array.Clear(_delayLeft);
        _tickOff = 0f;
        _tickState = -1;
        RefreshTicker();
        _sel = 0;
        _selT = SelectSec;
        _depN = 0;

        BuildScheme(_cur);
        InitTrains(_cur);
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;
        float t = dt * _speed;
        if (!(t > 0f)) return;

        _clock += t;
        if (_tickLen > 0) _tickOff = (_tickOff + TickerSpeed * t) % _tickLen;
        for (int k = 0; k < MaxLines; k++)
            if (_delayLeft[k] > 0f) _delayLeft[k] = MathF.Max(0f, _delayLeft[k] - t);

        switch (_phase)
        {
            case Phase.Running:
                _toRebuild -= t;
                if (_toRebuild <= 0f)
                {
                    BuildScheme(_next);
                    _depN = 0;
                    _phase = Phase.Erase;
                    _phaseT = 0f;
                }
                break;
            case Phase.Erase:
                _phaseT += t;
                if (_phaseT >= EraseSec)
                {
                    (_cur, _next) = (_next, _cur);
                    InitTrains(_cur);
                    _phase = Phase.Build;
                    _phaseT = 0f;
                }
                break;
            case Phase.Build:
                _phaseT += t;
                if (_phaseT >= BuildSec)
                {
                    _phase = Phase.Running;
                    _toRebuild = RebuildSec;
                    _phaseT = 0f;
                }
                break;
        }

        if (_phase == Phase.Running)
        {
            _eventT -= t;
            if (_eventT <= 0f && _cur.Lines > 0)
            {
                _delayLeft[_rng.Next(_cur.Lines)] = 20f + _rng.NextSingle() * 20f;
                _eventT = 120f + _rng.NextSingle() * 120f;
            }
            for (int k = 0; k < _cur.Lines; k++) UpdateLine(_cur, k, t);

            if (_cur.StationCount > 0)
            {
                _selT -= t;
                if (_selT <= 0f)
                {
                    _selT = SelectSec;
                    _sel = _rng.Next(_cur.StationCount);
                }
                if (_sel >= _cur.StationCount) _sel = 0;
                ComputeDepartures();
            }
        }

        int delayed = -1;
        for (int k = 0; k < MaxLines; k++)
            if (_delayLeft[k] > 0f) { delayed = k; break; }
        if (delayed != _tickState)
        {
            _tickState = delayed;
            RefreshTicker();
        }
    }

    public void Draw(CharGrid grid)
    {
        grid.Clear(Rgba.Black);
        grid.Text(3, 0, "METRO NETWORK", Dim);
        int hx = _hudOn ? _hudX + 1 : _cols - 12;
        grid.Text(hx, 0, "SCHEME", Dim);
        Num(grid, hx + 7, 0, _schemeNo % 100, 2, Dim);

        DrawSchematic(grid);
        if (_hudOn) DrawHud(grid);
        DrawTicker(grid);
    }

    /// <summary>Тестовый хук: поезда — ключ, линия, направление, на станции ли, позиция, длина петли, скорость.</summary>
    internal (int Key, int Line, bool Forward, bool AtStation, double U, int M, float V)[] TrainInfo()
    {
        var s = _cur;
        var list = new List<(int, int, bool, bool, double, int, float)>();
        for (int k = 0; k < s.Lines; k++)
        {
            int L = s.Len[k], M = 2 * (L - 1);
            for (int ti = 0; ti < s.TrainCount[k]; ti++)
            {
                int id = k * MaxTrains + ti;
                double u = s.TrU[id];
                list.Add((id, k, u <= L - 1, AtStation(s, k, L, M, u), u, M, s.TrV[id]));
            }
        }
        return list.ToArray();
    }

    /// <summary>Тестовый хук: число пар поездов одного направления, занявших одну колею в одной точке (должно быть 0).</summary>
    internal int SharedTrainCells()
    {
        var s = _cur;
        var owner = new Dictionary<long, int>();
        int bad = 0;
        for (int k = 0; k < s.Lines; k++)
        {
            int L = s.Len[k], M = 2 * (L - 1);
            for (int ti = 0; ti < s.TrainCount[k]; ti++)
            {
                int id = k * MaxTrains + ti, me = id + 1;
                double u = s.TrU[id];
                for (int q = 0; q < BodyLen; q++)
                {
                    double pos = u - q;
                    int p = TrainIdx(pos, L, M);
                    int wrapped = (int)Math.Floor(pos) % M;
                    if (wrapped < 0) wrapped += M;
                    bool fwd = wrapped <= L - 1;
                    long key = ((long)k * 2 + (fwd ? 0 : 1)) * 100000 + p;
                    if (owner.TryGetValue(key, out int who) && who != me) bad++;
                    else owner[key] = me;
                }
            }
        }
        return bad;
    }

    /// <summary>Тестовый хук: узлы пересадок в пикселях (колонки, строки × 1.8) и число линий в каждом.</summary>
    internal (double X, double Y, int Lines)[] HubsNow()
    {
        var s = _cur;
        var res = new (double, double, int)[s.HubCount];
        for (int h = 0; h < s.HubCount; h++)
        {
            int cnt = 0;
            for (int k = 0; k < s.Lines; k++) if (((s.LineMask[k] >> h) & 1) != 0) cnt++;
            res[h] = (s.HubSx[h] * 0.5, s.HubSy[h] * 0.6, cnt);
        }
        return res;
    }

    /// <summary>Тестовый хук: третьи линии в радиусе 3 ячеек от узла (должно быть 0).</summary>
    internal int ThirdLineViolations()
    {
        var s = _cur;
        int bad = 0;
        for (int h = 0; h < s.HubCount; h++)
            for (int k = 0; k < s.Lines; k++)
                if (((s.LineMask[k] >> h) & 1) == 0 && LineNear(s, k, s.HubSx[h], s.HubSy[h])) bad++;
        return bad;
    }

    /// <summary>Тестовый хук: число точечных перекрёстков вне узлов.</summary>
    internal int StrayClustersNow => _cur.StrayClusters;

    /// <summary>Тестовый хук: субпиксели, занятые двумя разными линиями вне зоны узлов (пересчёт с нуля).</summary>
    internal int SharedPixelsOutsideHubs()
    {
        var s = _cur;
        var owner = new int[_subW * _subH];
        var counted = new bool[_subW * _subH];
        int bad = 0;
        for (int k = 0; k < s.Lines; k++)
            for (int i = 0; i < s.Len[k]; i++)
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int x = s.PX[k][i] + dx, y = s.PY[k][i] + dy;
                        if (x < 0 || y < 0 || x >= _subW || y >= _subH) continue;
                        int idx = y * _subW + x;
                        if (owner[idx] == 0) owner[idx] = k + 1;
                        else if (owner[idx] != k + 1 && !counted[idx] && !AtHubCore(s, x, y))
                        {
                            counted[idx] = true;
                            bad++;
                        }
                    }
        return bad;
    }

    /// <summary>Точка в зоне узла текущей попытки генератора.</summary>
    private bool AtCurHubCore(int x, int y)
    {
        for (int h = 0; h < _nT; h++)
            if (Math.Abs(x - _hubSx[h]) <= HubCoreX && Math.Abs(y - _hubSy[h]) <= HubCoreY) return true;
        return false;
    }

    private static bool AtHubCore(Scheme s, int x, int y)
    {
        for (int h = 0; h < s.HubCount; h++)
            if (Math.Abs(x - s.HubSx[h]) <= HubCoreX && Math.Abs(y - s.HubSy[h]) <= HubCoreY) return true;
        return false;
    }

    /// <summary>Тестовый хук: имена станций (по одному на клетку).</summary>
    internal string[] StationNamesNow()
    {
        var s = _cur;
        var seen = new HashSet<int>();
        var list = new List<string>();
        for (int e = 0; e < s.StationCount; e++)
        {
            int cell = s.StnCell[e];
            if (!seen.Add(cell)) continue;
            var chars = new char[s.NameLen[cell]];
            for (int q = 0; q < chars.Length; q++) chars[q] = s.NameBuf[cell * NameCap + q];
            list.Add(new string(chars));
        }
        return list.ToArray();
    }

    /// <summary>Тестовый хук: подписи (слова) — позиция и длина.</summary>
    internal (int X, int Y, int Len)[] LabelWordsNow()
    {
        var s = _cur;
        var res = new (int, int, int)[s.WordCount];
        for (int w = 0; w < s.WordCount; w++) res[w] = (s.WordX[w], s.WordY[w], s.WordLen[w]);
        return res;
    }

    internal int StrayPixelsNow => _cur.StrayPixels;

    /// <summary>Тестовый хук: сколько пар соседних точек центральной линии не связны (должно быть 0).</summary>
    internal int CenterlineGaps()
    {
        var s = _cur;
        int gaps = 0;
        for (int k = 0; k < s.Lines; k++)
            for (int i = 1; i < s.Len[k]; i++)
                if (Math.Abs(s.PX[k][i] - s.PX[k][i - 1]) > 1 || Math.Abs(s.PY[k][i] - s.PY[k][i - 1]) > 1) gaps++;
        return gaps;
    }

    // ---------- Генератор схемы ----------

    private void BuildScheme(Scheme s)
    {
        _schemeNo++;
        Array.Clear(s.NameLen);
        Array.Clear(s.TrainCount);
        s.Lines = 0;
        s.Transfers = 0;
        s.HubCount = 0;
        s.StationCount = 0;
        s.LabelCount = 0;
        if (!_geomOK) return;

        int n = _linesWant;
        for (int outer = 0; outer < OuterTries; outer++)
        {
            Array.Clear(_subOwner);
            Array.Clear(_hubUse);
            _confN = 0;
            s.Lines = 0;
            MakeHubs(n);
            bool ok = true;
            for (int k = 0; k < n && ok; k++)
                if (BuildLine(s, k)) s.Lines++;
                else ok = false;
            if (ok && HubsOk(s, n)) break;
        }
        Finish(s);
    }

    /// <summary>
    /// Пересадки: 2–4 узла около центра, попарно разнесены не меньше чем на HubSepPx колонок.
    /// Узел j соединяет ровно две линии (пары 2j и 2j+1 по кругу), так что у каждой линии 1–2 узла.
    /// </summary>
    private void MakeHubs(int n)
    {
        _nT = Math.Clamp((n + 1) / 2, 2, 4);
        double x0 = _bx0 + 4, x1 = _bx1 - 4, y0 = (_by0 + 2) * 1.8, y1 = (_by1 - 2) * 1.8;
        double cx = (x0 + x1) * 0.5, cy = (y0 + y1) * 0.5;
        double w = x1 - x0, h = y1 - y0;
        for (int j = 0; j < _nT; j++)
        {
            double hx = cx, hy = cy;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                hx = cx + (_rng.NextSingle() * 2 - 1) * w * 0.30;
                hy = cy + (_rng.NextSingle() * 2 - 1) * h * 0.30;
                if (FarFromHubs(j, hx, hy)) break;
            }
            _hubPx[j] = hx;
            _hubPy[j] = hy;
            _hubSx[j] = (int)Math.Round(hx * 2);
            _hubSy[j] = (int)Math.Round(hy / 0.6);
        }
        Array.Clear(_lineMask);
        for (int j = 0; j < _nT; j++)
        {
            int a = (2 * j) % n, b = (2 * j + 1) % n;
            _lineMask[a] |= 1 << j;
            if (b != a) _lineMask[b] |= 1 << j;
        }
    }

    private bool FarFromHubs(int j, double hx, double hy)
    {
        for (int o = 0; o < j; o++)
        {
            double dx = hx - _hubPx[o], dy = hy - _hubPy[o];
            if (dx * dx + dy * dy < HubSepPx * HubSepPx) return false;
        }
        return true;
    }

    /// <summary>Линия k проходит в радиусе 3 ячеек (6 × 9 субпикселей) от узла с координатами (hsx, hsy)?</summary>
    private static bool LineNear(Scheme s, int k, int hsx, int hsy)
    {
        int L = s.Len[k];
        for (int i = 0; i < L; i++)
            if (Math.Abs(s.PX[k][i] - hsx) <= 6 && Math.Abs(s.PY[k][i] - hsy) <= 9) return true;
        return false;
    }

    /// <summary>
    /// Проверки схемы: узлы разнесены, каждый узел — ровно две линии, третьей линии в радиусе 3 ячеек нет,
    /// точечных перекрёстков вне узлов не больше MaxStrayClusters (и не ближе 4 ячеек к узлу).
    /// </summary>
    private bool HubsOk(Scheme s, int n)
    {
        for (int h = 0; h < _nT; h++)
        {
            int cnt = 0;
            for (int k = 0; k < n; k++)
                if (((_lineMask[k] >> h) & 1) != 0) cnt++;
            if (cnt != 2) return false;
        }
        for (int a = 0; a < _nT; a++)
            for (int b = a + 1; b < _nT; b++)
            {
                double dx = _hubPx[a] - _hubPx[b], dy = _hubPy[a] - _hubPy[b];
                if (dx * dx + dy * dy < HubSepPx * HubSepPx) return false;
            }
        for (int h = 0; h < _nT; h++)
            for (int k = 0; k < n; k++)
                if (((_lineMask[k] >> h) & 1) == 0 && LineNear(s, k, _hubSx[h], _hubSy[h])) return false;

        // Конфликтные пиксели у узлов — это сами пересадки; остальные — точечные перекрёстки.
        int m = 0;
        for (int c = 0; c < _confN; c++)
        {
            bool atHub = false;
            for (int h = 0; h < _nT && !atHub; h++)
                if (Math.Abs(_confX[c] - _hubSx[h]) <= HubCoreX && Math.Abs(_confY[c] - _hubSy[h]) <= HubCoreY) atHub = true;
            if (!atHub && m < ConfCap)
            {
                _strayX[m] = _confX[c];
                _strayY[m] = _confY[c];
                _strayMark[m] = false;
                m++;
            }
        }
        // Две линии не делят коридор: вне узлов общих субпикселей не больше MaxStrayPx.
        if (m > MaxStrayPx) return false;
        int clusters = 0;
        for (int i = 0; i < m; i++)
        {
            if (_strayMark[i]) continue;
            clusters++;
            if (clusters > MaxStrayClusters) return false;
            int sp = 0;
            _stk[sp++] = i;
            _strayMark[i] = true;
            while (sp > 0)
            {
                int cur = _stk[--sp];
                for (int j = 0; j < m; j++)
                {
                    if (_strayMark[j]) continue;
                    if (Math.Abs(_strayX[j] - _strayX[cur]) <= 2 && Math.Abs(_strayY[j] - _strayY[cur]) <= 2)
                    {
                        _strayMark[j] = true;
                        _stk[sp++] = j;
                    }
                }
            }
        }

        s.Transfers = _nT;
        s.HubCount = _nT;
        s.StrayClusters = clusters;
        s.StrayPixels = m;
        for (int h = 0; h < _nT; h++)
        {
            s.HubSx[h] = _hubSx[h];
            s.HubSy[h] = _hubSy[h];
        }
        Array.Copy(_lineMask, s.LineMask, MaxLines);
        return true;
    }

    /// <summary>Линия от края к противоположному через один или два пересадочных узла; 2–4 поворота.</summary>
    /// <summary>Несколько попыток для одной линии: новые концы и трасса, пока не найдётся место без параллельных коридоров.</summary>
    private bool BuildLine(Scheme s, int k)
    {
        for (int t = 0; t < LineTries; t++)
            if (BuildLineOnce(s, k)) return true;
        return false;
    }

    private bool BuildLineOnce(Scheme s, int k)
    {
        double x0 = _bx0 + 4, x1 = _bx1 - 4, y0 = (_by0 + 2) * 1.8, y1 = (_by1 - 2) * 1.8;
        double w = x1 - x0, h = y1 - y0;
        int o = _rng.Next(3);
        double fa = 0.15 + 0.7 * _rng.NextSingle(), fb = 0.15 + 0.7 * _rng.NextSingle();
        double sx, sy, ex, ey;
        switch (o)
        {
            case 0: sx = x0; sy = y0 + h * fa; ex = x1; ey = y0 + h * fb; break;
            case 1: sx = x0 + w * fa; sy = y0; ex = x0 + w * fb; ey = y1; break;
            default: sx = x0; sy = y0 + h * fa * 0.4; ex = x1; ey = y1 - h * fb * 0.4; break;
        }
        if (_rng.Next(2) == 0) { (sx, ex) = (ex, sx); (sy, ey) = (ey, sy); }

        // Пересадки линии по маске: одна или две.
        int h1 = -1, h2 = -1;
        for (int j = 0; j < _nT; j++)
            if (((_lineMask[k] >> j) & 1) != 0)
            {
                if (h1 < 0) h1 = j;
                else h2 = j;
            }
        if (h2 >= 0)
        {
            double dx = ex - sx, dy = ey - sy;
            double p1 = (_hubPx[h1] - sx) * dx + (_hubPy[h1] - sy) * dy;
            double p2 = (_hubPx[h2] - sx) * dx + (_hubPy[h2] - sy) * dy;
            if (p2 < p1) (h1, h2) = (h2, h1);
        }

        _vn = 0;
        AddV(sx, sy);
        double cxp = sx, cyp = sy;
        for (int step = 0; step < 3; step++)
        {
            double tx, ty;
            if (step == 0 && h1 >= 0) { tx = _hubPx[h1]; ty = _hubPy[h1]; }
            else if (step == 1 && h2 >= 0) { tx = _hubPx[h2]; ty = _hubPy[h2]; }
            else if (step == 2 || (step == 1 && h2 < 0) || (step == 0 && h1 < 0)) { tx = ex; ty = ey; }
            else continue;
            Octi(cxp, cyp, tx, ty, _rng.Next(2) == 0);
            cxp = tx; cyp = ty;
            if (tx == ex && ty == ey) break;
        }

        int turns = 0;
        for (int i = 1; i + 1 < _vn; i++)
        {
            double d1x = _vx[i] - _vx[i - 1], d1y = _vy[i] - _vy[i - 1];
            double d2x = _vx[i + 1] - _vx[i], d2y = _vy[i + 1] - _vy[i];
            double cross = d1x * d2y - d1y * d2x, dot = d1x * d2x + d1y * d2y;
            if (Math.Abs(cross) > 1e-6 || dot < 0) turns++;
        }
        if (turns < 2 || turns > 4) return false;

        // Растеризация в субпиксели.
        _bs = s;
        _bk = k;
        _pl = 0;
        s.Len[k] = 0;
        int sxi = SubX(_vx[0]), syi = SubY(_vy[0]);
        if (!PushPt(sxi, syi)) return false;
        for (int i = 0; i + 1 < _vn; i++)
            if (!RasterSeg(SubX(_vx[i]), SubY(_vy[i]), SubX(_vx[i + 1]), SubY(_vy[i + 1]))) return false;
        int L = _pl;
        if (L < 8) return false;
        s.Len[k] = L;
        for (int i = 0; i < L; i++)
            if (s.PX[k][i] < 2 * _bx0 || s.PX[k][i] > 2 * _bx1 || s.PY[k][i] < 3 * _by0 || s.PY[k][i] > 3 * _by1 + 1)
                return false;

        // Станции: концы, пересадки, затем каждые 6–10 ячеек по пути.
        double cum = 0;
        for (int i = 0; i < L; i++)
        {
            if (i > 0)
            {
                double ddx = (s.PX[k][i] - s.PX[k][i - 1]) * 0.5, ddy = (s.PY[k][i] - s.PY[k][i - 1]) * 0.6;
                cum += Math.Sqrt(ddx * ddx + ddy * ddy);
            }
            _cum[i] = cum;
        }
        int hubA = -1, hubB = -1;
        if (h1 >= 0) hubA = NearestIdx(k, L, _hubSx[h1], _hubSy[h1]);
        if (h2 >= 0) hubB = NearestIdx(k, L, _hubSx[h2], _hubSy[h2]);
        for (int i = 0; i < L; i++) s.Station[k][i] = false;
        s.Station[k][0] = true;
        s.Station[k][L - 1] = true;
        if (hubA >= 0) s.Station[k][hubA] = true;
        if (hubB >= 0) s.Station[k][hubB] = true;
        double last = 0, target = 6 + _rng.NextSingle() * 4;
        for (int i = 1; i < L - 1; i++)
        {
            if (i == hubA || i == hubB)
            {
                last = _cum[i];
                target = 6 + _rng.NextSingle() * 4;
                continue;
            }
            if (_cum[i] - last >= target)
            {
                s.Station[k][i] = true;
                last = _cum[i];
                target = 6 + _rng.NextSingle() * 4;
            }
        }

        // Коридор чужой линии: трасса не идёт параллельно по занятым субпикселям (перекрёсток — допустим, несколько пикселей).
        int foreign = 0;
        for (int i = 0; i < L; i++)
            for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                {
                    int x = s.PX[k][i] + dx, y = s.PY[k][i] + dy;
                    if (x < 0 || y < 0 || x >= _subW || y >= _subH) continue;
                    int owner = _subOwner[y * _subW + x];
                    if (owner > 0 && owner != k + 1 && !AtCurHubCore(x, y)) foreign++;
                }
        if (foreign > MaxLineForeign) return false;

        // Пересечения с другими линиями: пиксели, занятые двумя линиями (проверяются у хабов).
        for (int i = 0; i < L; i++)
        {
            int x = s.PX[k][i], y = s.PY[k][i];
            if (!MarkSub(k, x, y) || !MarkSub(k, x + 1, y) || !MarkSub(k, x, y + 1) || !MarkSub(k, x + 1, y + 1)) return false;
        }
        if (h1 >= 0) _hubUse[h1]++;
        if (h2 >= 0) _hubUse[h2]++;
        return true;
    }

    private int NearestIdx(int k, int L, int tx, int ty)
    {
        int best = 0;
        long bd = long.MaxValue;
        for (int i = 0; i < L; i++)
        {
            long dx = s_PX(k, i) - tx, dy = s_PY(k, i) - ty;
            long d = dx * dx + dy * dy;
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    private int s_PX(int k, int i) => _bs.PX[k][i];
    private int s_PY(int k, int i) => _bs.PY[k][i];

    private bool MarkSub(int k, int x, int y)
    {
        if (x < 0 || y < 0 || x >= _subW || y >= _subH) return true;
        int idx = y * _subW + x;
        int owner = _subOwner[idx];
        if (owner == 0) { _subOwner[idx] = k + 1; return true; }
        if (owner == k + 1 || owner == -1) return true;
        if (_confN >= ConfCap) return false;
        _confX[_confN] = x;
        _confY[_confN] = y;
        _confN++;
        _subOwner[idx] = -1;   // пиксель уже учтён как конфликтный (без повторов)
        return true;
    }

    private static int SubX(double px) => (int)Math.Round(px * 2);
    private static int SubY(double py) => (int)Math.Round(py / 0.6);

    private void AddV(double x, double y)
    {
        if (_vn > 0 && Math.Abs(x - _vx[_vn - 1]) < 1e-9 && Math.Abs(y - _vy[_vn - 1]) < 1e-9) return;
        if (_vn >= _vx.Length) return;
        _vx[_vn] = x;
        _vy[_vn] = y;
        _vn++;
    }

    /// <summary>Октилинейный путь A→B в пикселях: одна диагональ 45° и прямая часть, порядок — по выбору.</summary>
    private void Octi(double ax, double ay, double bx, double by, bool diagFirst)
    {
        double dx = bx - ax, dy = by - ay;
        double adx = Math.Abs(dx), ady = Math.Abs(dy);
        if (adx >= ady)
        {
            double sg = dx < 0 ? -1 : 1;
            double mx = diagFirst ? ax + sg * ady : bx - sg * ady;
            double my = diagFirst ? by : ay;
            AddV(mx, my);
        }
        else
        {
            double sg = dy < 0 ? -1 : 1;
            double mx = diagFirst ? bx : ax;
            double my = diagFirst ? ay + sg * adx : by - sg * adx;
            AddV(mx, my);
        }
        AddV(bx, by);
    }

    private bool PushPt(int x, int y)
    {
        if (_pl >= _bs.PX[_bk].Length) return false;
        _bs.PX[_bk][_pl] = x;
        _bs.PY[_bk][_pl] = y;
        _pl++;
        return true;
    }

    /// <summary>Отрезок Брезенхэма от (x0,y0) до (x1,y1): начало не добавляется, конец добавляется.</summary>
    private bool RasterSeg(int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx + dy, x = x0, y = y0;
        while (true)
        {
            if (!(x == x0 && y == y0) && !PushPt(x, y)) return false;
            if (x == x1 && y == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
        return true;
    }

    /// <summary>Станции и подписи: одна станция на ячейку, подписи в свободных ячейках (не на линиях).</summary>
    private void Finish(Scheme s)
    {
        Array.Clear(_taken);
        Array.Clear(_labDone);
        Array.Clear(_labOcc);
        s.WordCount = 0;
        int ec = 0;
        for (int k = 0; k < s.Lines; k++)
        {
            int L = s.Len[k];
            for (int i = 0; i < L; i++)
            {
                int x = s.PX[k][i], y = s.PY[k][i];
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int px = x + dx, py = y + dy;
                        if (px >= 0 && py >= 0 && px < _subW && py < _subH) _taken[(py / 3) * _cols + px / 2] = true;
                    }
                if (s.Station[k][i] && ec < s.StnCell.Length)
                {
                    s.StnCell[ec] = (y / 3) * _cols + x / 2;
                    s.StnIdx[ec] = i;
                    s.StnLine[ec] = k;
                    ec++;
                }
            }
        }
        s.StationCount = ec;
        // Кольца пересадок тоже занимают клетки: подписи туда не ставим.
        for (int h = 0; h < s.HubCount; h++)
            for (int dy = -2; dy <= 1; dy++)
                for (int dx = -2; dx <= 1; dx++)
                {
                    int px = s.HubSx[h] + dx, py = s.HubSy[h] + dy;
                    if (px >= 0 && py >= 0 && px < _subW && py < _subH) _taken[(py / 3) * _cols + px / 2] = true;
                }
        var used = new HashSet<string>();
        for (int e = 0; e < ec; e++)
        {
            int cnt = 0;
            for (int f = 0; f < ec; f++)
                if (s.StnCell[f] == s.StnCell[e]) cnt++;
            s.StnShared[e] = cnt > 1;
            if (s.NameLen[s.StnCell[e]] == 0) GenName(s, s.StnCell[e], used);
        }

        int hcount = 0;
        for (int e = 0; e < ec; e++)
        {
            int cell = s.StnCell[e];
            if (_labDone[cell]) continue;
            _labDone[cell] = true;
            int k = s.StnLine[e], idx = s.StnIdx[e], L = s.Len[k];
            int i0 = Math.Max(0, idx - 2), i1 = Math.Min(L - 1, idx + 2);
            double ddx = (s.PX[k][i1] - s.PX[k][i0]) * 0.5, ddy = (s.PY[k][i1] - s.PY[k][i0]) * 0.6;
            bool horizontal = Math.Abs(ddx) >= Math.Abs(ddy);
            int a = hcount % 2, b = 1 - a;
            hcount++;
            int[] order = horizontal ? new[] { a, b, 2 + a, 2 + b } : new[] { 2 + a, 2 + b, a, b };
            foreach (int v in order)
                if (TryLabel(s, cell, v)) break;
        }
    }

    /// <summary>Имя станции — уникальное в пределах схемы: два слога, при коллизии — третий слог.</summary>
    private void GenName(Scheme s, int cell, HashSet<string> used)
    {
        string name;
        for (int attempt = 0; ; attempt++)
        {
            string a = SylA[_rng.Next(SylA.Length)];
            string b = SylB[_rng.Next(SylB.Length)];
            name = attempt < 200 ? a + b : a + b + SylA[_rng.Next(SylA.Length)];
            if (name.Length <= NameCap && used.Add(name)) break;
            if (attempt >= 2000) break;
        }
        int baseIdx = cell * NameCap;
        int len = Math.Min(name.Length, NameCap);
        for (int q = 0; q < len; q++) s.NameBuf[baseIdx + q] = name[q];
        s.NameLen[cell] = (byte)len;
    }

    /// <summary>Подпись: 0 — над станцией, 1 — под, 2 — справа, 3 — слева. Только в свободных ячейках.</summary>
    private bool TryLabel(Scheme s, int cell, int variant)
    {
        int len = s.NameLen[cell];
        if (len == 0) return false;
        int x = cell % _cols, y = cell / _cols;
        int x0, y0;
        switch (variant)
        {
            case 0: x0 = x - len / 2; y0 = y - 1; break;
            case 1: x0 = x - len / 2; y0 = y + 1; break;
            case 2: x0 = x + 2; y0 = y; break;
            default: x0 = x - len - 1; y0 = y; break;
        }
        if (y0 < _by0 || y0 > _by1) return false;
        for (int q = 0; q < len; q++)
        {
            int px = x0 + q;
            if (px < _bx0 || px > _bx1 || _taken[y0 * _cols + px]) return false;
        }
        // Между соседними подписями на строке — не меньше LabelGap пустых ячеек.
        for (int q = -LabelGap; q < len + LabelGap; q++)
        {
            int px = x0 + q;
            if (px < _bx0 || px > _bx1) continue;
            if (_labOcc[y0 * _cols + px]) return false;
        }
        for (int q = 0; q < len; q++)
        {
            int c = y0 * _cols + x0 + q;
            _taken[c] = true;
            _labOcc[c] = true;
            s.LabelCell[s.LabelCount] = c;
            s.LabelChar[s.LabelCount] = s.NameBuf[cell * NameCap + q];
            s.LabelCount++;
        }
        s.WordX[s.WordCount] = x0;
        s.WordY[s.WordCount] = y0;
        s.WordLen[s.WordCount] = len;
        s.WordCount++;
        return true;
    }

    // ---------- Поезда ----------

    private void InitTrains(Scheme s)
    {
        for (int k = 0; k < s.Lines; k++)
        {
            int L = s.Len[k], M = 2 * (L - 1);
            int tc = Math.Min(_trainsWant, Math.Max(1, M / TrainSpacing));
            s.TrainCount[k] = tc;
            double baseU = _rng.NextSingle() * M;
            for (int ti = 0; ti < tc; ti++)
            {
                int id = k * MaxTrains + ti;
                s.TrU[id] = (baseU + ti * (double)M / tc) % M;
                s.TrV[id] = 0f;
                s.TrWait[id] = 0f;
                s.TrAt[id] = false;
            }
        }
    }

    /// <summary>Поезд едет по петле: туда по линии (колея A), обратно по ней же (колея B). Позиция u на петле длины M.</summary>
    private void UpdateLine(Scheme s, int k, float t)
    {
        int L = s.Len[k];
        if (L < 2) return;
        int M = 2 * (L - 1);
        bool delayed = _delayLeft[k] > 0f;
        int n = s.TrainCount[k];
        for (int ti = 0; ti < n; ti++)
        {
            int id = k * MaxTrains + ti;
            if (s.TrWait[id] > 0f)
            {
                s.TrWait[id] -= t;
                if (s.TrWait[id] > 0f) continue;
                s.TrWait[id] = 0f;
            }

            double u = s.TrU[id];
            if (AtStation(s, k, L, M, u))
            {
                if (!s.TrAt[id])
                {
                    s.TrAt[id] = true;
                    s.TrV[id] = 0f;
                    s.TrWait[id] = 2f + _rng.NextSingle();   // стоянка 2–3 с
                    continue;
                }
            }
            else s.TrAt[id] = false;

            double dStn = DistToNextStation(s, k, u, M);
            double dGap = GapAhead(s, k, ti, u, M, L);
            float target = delayed ? 0f : VMax;
            if (dStn < BrakeRange) target = MathF.Min(target, MathF.Max(1.5f, MathF.Sqrt(2f * Decel * (float)dStn)));
            if (dGap < GapRange) target = MathF.Min(target, MathF.Sqrt(2f * Decel * (float)Math.Max(0.0, dGap - GapSubs)));

            float v = s.TrV[id];
            v = v < target ? MathF.Min(target, v + Accel * t) : MathF.Max(target, v - Decel * t);
            s.TrV[id] = v;

            double step = v * t;
            if (step >= dStn) u = Math.Round(u + dStn);   // остановка точно на станции
            else u += step;
            u %= M;
            if (u < 0) u += M;
            s.TrU[id] = u;
        }
    }

    private static bool AtStation(Scheme s, int k, int L, int M, double u)
    {
        double r = Math.Round(u);
        if (Math.Abs(u - r) > 1e-6) return false;
        int p = (int)r % M;
        return s.Station[k][Idx(L, M, p)];
    }

    private static double Raw(double u, double p, int M)
    {
        double d = (p - u) % M;
        if (d < 0) d += M;
        return d;
    }

    /// <summary>Расстояние вперёд до позиции p; ноль (мы уже здесь) не считается.</summary>
    private static double Ahead(double u, double p, int M)
    {
        double d = Raw(u, p, M);
        return d < 1e-6 || d > M - 1e-6 ? double.MaxValue : d;
    }

    private static double DistToNextStation(Scheme s, int k, double u, int M)
    {
        double best = double.MaxValue;
        for (int e = 0; e < s.StationCount; e++)
        {
            if (s.StnLine[e] != k) continue;
            int p = s.StnIdx[e];
            best = Math.Min(best, Ahead(u, p, M));
            best = Math.Min(best, Ahead(u, (M - p) % M, M));
        }
        return best;
    }

    /// <summary>Расстояние до ближайшего поезда впереди в том же направлении (встречные — другая колея).</summary>
    private static double GapAhead(Scheme s, int k, int ti, double u, int M, int L)
    {
        double best = double.MaxValue;
        bool fwd = u <= L - 1;
        int n = s.TrainCount[k];
        for (int o = 0; o < n; o++)
        {
            if (o == ti) continue;
            double uo = s.TrU[k * MaxTrains + o];
            if ((uo <= L - 1) != fwd) continue;
            best = Math.Min(best, Ahead(u, uo, M));
        }
        return best;
    }

    private static int Idx(int L, int M, int p) => p < L ? p : M - p;

    private static int TrainIdx(double pos, int L, int M)
    {
        int p = (int)Math.Floor(pos) % M;
        if (p < 0) p += M;
        return Idx(L, M, p);
    }

    /// <summary>Табло: ближайшие поезда к выбранной станции по всем линиям, проходящим через неё.</summary>
    private void ComputeDepartures()
    {
        var s = _cur;
        _depN = 0;
        int cell = s.StnCell[_sel];
        for (int e = 0; e < s.StationCount && _depN < 4; e++)
        {
            if (s.StnCell[e] != cell) continue;
            int k = s.StnLine[e], idx = s.StnIdx[e];
            int L = s.Len[k], M = 2 * (L - 1);
            double best = double.MaxValue;
            int dest = -1;
            for (int ti = 0; ti < s.TrainCount[k]; ti++)
            {
                double u = s.TrU[k * MaxTrains + ti];
                double fw = Raw(u, idx, M), bw = Raw(u, (M - idx) % M, M);
                if (fw < best) { best = fw; dest = CellOf(s, k, L - 1); }
                if (bw < best) { best = bw; dest = CellOf(s, k, 0); }
            }
            if (dest < 0 || best >= M) continue;
            _depLine[_depN] = k;
            _depDest[_depN] = dest;
            _depMin[_depN] = Math.Max(1, (int)Math.Ceiling(best / VMax / 60.0));
            _depN++;
        }
    }

    private int CellOf(Scheme s, int k, int idx) => (s.PY[k][idx] / 3) * _cols + s.PX[k][idx] / 2;

    // ---------- Рисование ----------

    private float ShareOf(Scheme s, int k)
    {
        switch (_phase)
        {
            case Phase.Erase: return 1f - Math.Clamp(_phaseT / EraseSec * s.Lines - k, 0f, 1f);
            case Phase.Build: return Math.Clamp(_phaseT / BuildSec * s.Lines - k, 0f, 1f);
            default: return 1f;
        }
    }

    private void MarkSubPx(int x, int y, Rgba col)
    {
        if (x < 0 || y < 0 || x >= _subW || y >= _subH) return;
        int cell = (y / 3) * _cols + x / 2;
        _bits[cell] |= 1 << ((y % 3) * 2 + (x % 2));
        _bcol[cell] = col;
    }

    private void ClearSubPx(int x, int y)
    {
        if (x < 0 || y < 0 || x >= _subW || y >= _subH) return;
        _bits[(y / 3) * _cols + x / 2] &= ~(1 << ((y % 3) * 2 + (x % 2)));
    }

    private bool NearHub(Scheme s, int x, int y)
    {
        for (int h = 0; h < s.HubCount; h++)
            if (Math.Abs(x - s.HubSx[h]) <= 4 && Math.Abs(y - s.HubSy[h]) <= 6) return true;
        return false;
    }

    private void DrawSchematic(CharGrid grid)
    {
        var s = _cur;
        int cells = _cols * _rows;
        Array.Clear(_bits, 0, cells);
        for (int k = 0; k < s.Lines; k++)
        {
            int L = s.Len[k];
            int vis = (int)(L * ShareOf(s, k));
            if (vis <= 0) continue;
            bool delayed = _delayLeft[k] > 0f;
            float f = delayed ? ((((int)(_clock * 2.0)) & 1) == 0 ? 1f : 0.3f) : 0.9f;
            Rgba c = LineColors[k].Scale(f);
            for (int i = 0; i < vis; i++)
            {
                int x = s.PX[k][i], y = s.PY[k][i];
                MarkSubPx(x, y, c);
                MarkSubPx(x + 1, y, c);
                MarkSubPx(x, y + 1, c);
                MarkSubPx(x + 1, y + 1, c);
            }
        }

        if (_phase == Phase.Running && _trainsVisible)
            for (int k = 0; k < s.Lines; k++)
            {
                int L = s.Len[k], M = 2 * (L - 1);
                Rgba tint = Rgba.Lerp(LineColors[k], Rgba.White, 0.55f);
                for (int ti = 0; ti < s.TrainCount[k]; ti++)
                {
                    double u = s.TrU[k * MaxTrains + ti];
                    for (int q = 0; q < BodyLen; q++)
                    {
                        double pos = u - q;
                        int idx = TrainIdx(pos, L, M);
                        int wrapped = (int)Math.Floor(pos) % M;
                        if (wrapped < 0) wrapped += M;
                        int off = wrapped <= L - 1 ? 0 : 1;   // встречная колея смещена на субпиксель
                        MarkSubPx(s.PX[k][idx] + off, s.PY[k][idx] + off, tint);
                    }
                }
            }

        if (_phase == Phase.Running)
            for (int h = 0; h < s.HubCount; h++)
            {
                // Кольцо пересадки 4×4 субпикселя: рамка белая, внутренность пустая.
                int cx = s.HubSx[h], cy = s.HubSy[h];
                for (int dy = -2; dy <= 1; dy++)
                    for (int dx = -2; dx <= 1; dx++)
                    {
                        bool border = dx == -2 || dx == 1 || dy == -2 || dy == 1;
                        if (border) MarkSubPx(cx + dx, cy + dy, Rgba.White);
                        else ClearSubPx(cx + dx, cy + dy);
                    }
            }

        for (int cell = 0; cell < cells; cell++)
            if (_bits[cell] != 0)
                grid.Put(cell % _cols, cell / _cols, GlyphSet.Sextant(_bits[cell]), _bcol[cell]);

        if (_phase == Phase.Running)
        {
            for (int e = 0; e < s.StationCount; e++)
            {
                int k = s.StnLine[e], idx = s.StnIdx[e];
                if (NearHub(s, s.PX[k][idx], s.PY[k][idx])) continue;   // у пересадки рисует кольцо
                int cell = s.StnCell[e];
                grid.Put(cell % _cols, cell / _cols, StationGlyph, LineColors[k]);
            }
            if (_labelsOn)
                for (int e = 0; e < s.LabelCount; e++)
                {
                    int cell = s.LabelCell[e];
                    grid.Put(cell % _cols, cell / _cols, s.LabelChar[e], LabelFg);
                }
        }
    }

    private void DrawHud(CharGrid grid)
    {
        var s = _cur;
        for (int y = _by0; y <= _by1; y++) grid.Put(_hudX - 1, y, '│', Dim);

        int x = _hudX + 1;
        grid.Text(x, 2, "TIME", Dim);
        int secs = (int)_clock % 86400;
        Num(grid, x + 6, 2, secs / 3600, 2, Ink);
        grid.Put(x + 8, 2, ':', Dim);
        Num(grid, x + 9, 2, secs / 60 % 60, 2, Ink);
        grid.Put(x + 11, 2, ':', Dim);
        Num(grid, x + 12, 2, secs % 60, 2, Ink);

        // Легенда линий: цвет, имя по конечной, статус.
        grid.Text(x, 4, "LINES", Dim);
        for (int k = 0; k < s.Lines; k++)
        {
            int row = 5 + k;
            bool delayed = _delayLeft[k] > 0f;
            grid.Text(x, row, "██", LineColors[k]);
            grid.Put(x + 3, row, (char)('1' + k), LineColors[k]);
            grid.Put(x + 2, row, 'L', Ink);
            WriteName(grid, x + 5, row, TermCellOf(s, k), 6, Ink);
            if (delayed) grid.Text(x + 14, row, "DELAY", LineColors[3]);
            else grid.Text(x + 14, row, "OK", Ink);
        }

        // Табло отправлений выбранной станции.
        grid.Text(x, 12, "DEPARTURES", Dim);
        if (s.StationCount > 0)
        {
            grid.Text(x, 13, "AT", Dim);
            WriteName(grid, x + 4, 13, s.StnCell[_sel], NameCap, Ink);
        }
        for (int i = 0; i < 4; i++)
        {
            int row = 14 + i;
            if (i >= _depN) continue;
            Rgba c = LineColors[_depLine[i]];
            grid.Put(x, row, 'L', c);
            grid.Put(x + 1, row, (char)('1' + _depLine[i]), c);
            grid.Put(x + 3, row, '>', c);
            WriteName(grid, x + 5, row, _depDest[i], 6, c);
            Num(grid, x + 13, row, _depMin[i], 2, Ink);
            grid.Text(x + 16, row, "MIN", Dim);
        }

        int trains = 0;
        for (int k = 0; k < s.Lines; k++) trains += s.TrainCount[k];
        grid.Text(x, 19, "STATIONS", Dim);
        Num(grid, x + 10, 19, s.StationCount, 3, Ink);
        grid.Text(x, 20, "TRAINS", Dim);
        Num(grid, x + 10, 20, trains, 3, Ink);
        if (_tickState >= 0)
        {
            grid.Text(x, 22, "LINE", LineColors[3]);
            grid.Put(x + 5, 22, (char)('1' + _tickState), LineColors[3]);
            grid.Text(x + 7, 22, "DELAYED", LineColors[3]);
        }
        else grid.Text(x, 22, "NO DELAYS", Dim);
    }

    private int TermCellOf(Scheme s, int k) => s.Len[k] > 0 ? CellOf(s, k, 0) : -1;

    private void WriteName(CharGrid grid, int x, int y, int cell, int max, Rgba fg)
    {
        if (cell < 0) return;
        int nl = Math.Min(max, _cur.NameLen[cell]);
        for (int q = 0; q < nl; q++) grid.Put(x + q, y, _cur.NameBuf[cell * NameCap + q], fg);
    }

    private void DrawTicker(CharGrid grid)
    {
        if (_tickLen <= 0) return;
        Rgba fg = _tickAlert ? LineColors[3] : Ink;
        int off = (int)_tickOff;
        int y = _rows - 1;
        // Только между колонками 3 и (ширина − 3): скруглённые углы экрана не трогаем.
        for (int x = 3; x <= _cols - 3; x++)
            grid.Put(x, y, _tick[(off + x) % _tickLen], fg);
    }

    /// <summary>Одно бегущее сообщение; между повторами пауза не короче ширины экрана.</summary>
    private void RefreshTicker()
    {
        int p = 0;
        if (_tickState >= 0)
        {
            Copy(ref p, DelayMsg);
            _tick[p++] = (char)('1' + _tickState);
            Copy(ref p, DelayTail);
            _tickAlert = true;
        }
        else
        {
            Copy(ref p, NormalMsg);
            _tickAlert = false;
        }
        int msgLen = p;
        while (p < msgLen + _cols && p < _tick.Length) _tick[p++] = ' ';
        _tickLen = p;
    }

    private void Copy(ref int p, string s)
    {
        s.CopyTo(0, _tick, p, s.Length);
        p += s.Length;
    }

    private static void Num(CharGrid g, int x, int y, int v, int w, Rgba fg)
    {
        if (v < 0) v = 0;
        for (int i = w - 1; i >= 0; i--)
        {
            g.Put(x + i, y, (char)('0' + v % 10), fg);
            v /= 10;
        }
    }
}
