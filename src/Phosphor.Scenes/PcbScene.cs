using System.Runtime.CompilerServices;
using Phosphor.Core;

[assembly: InternalsVisibleTo("Phosphor.Scenes.Tests")]

namespace Phosphor.Scenes;

/// <summary>
/// Плата в CAD-программе: тёмно-зелёная маска, микросхемы, резисторы, конденсаторы, разъём и крепёжные отверстия.
/// Автотрассировщик по волне Дейкстры (0°, 90°, 45°) прокладывает дорожки между выводами по списку цепей: медная
/// дорожка появляется от вывода к выводу, переходы — через «○». Потом заливка земли штриховкой и новая плата.
/// Звук: удар ускоряет трассировку, бас подсвечивает кончик текущей дорожки.
/// </summary>
public sealed class PcbScene : IScene, IAudioReactive, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec RouteSpeedSpec = new("routeSpeed", SceneParamKind.Float, 1f, 0.2f, 5f, "скорость трассировки");
    public static readonly SceneParamSpec DensitySpec = new("density", SceneParamKind.Float, 0.6f, 0.3f, 1f, "сколько компонентов на плате");
    public static readonly SceneParamSpec LayersSpec = new("layers", SceneParamKind.Choice, 1f, 0f, 1f, "слои платы: один или два",
        new[] { "one", "two" });
    private static readonly SceneParamSpec[] ParamList = { RouteSpeedSpec, DensitySpec, LayersSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    public string Id => "pcb";

    // Палитра: маска, медь, синий нижний слой, шелкография.
    private static readonly Rgba MaskBg = Rgba.Hex(0x0B2A1B);
    private static readonly Rgba StatusBg = Rgba.Hex(0x06140C);
    private static readonly Rgba BodyBg = Rgba.Hex(0x061A11);      // корпус темнее маски
    private static readonly Rgba BodyFg = Rgba.Hex(0x93AE9C);      // серо-зелёная шелкография корпуса
    private static readonly Rgba KeyFg = Rgba.Hex(0xD8F2E1);
    private static readonly Rgba LabelFg = Rgba.Hex(0xD8F2E1);
    private static readonly Rgba PadFg = Rgba.Hex(0x9A6230);
    private static readonly Rgba Copper = Rgba.Hex(0xFF9A3C);
    private static readonly Rgba CopperTip = Rgba.Hex(0xFFF4E0);
    private static readonly Rgba BlueTrack = Rgba.Hex(0x6FB8E8);
    private static readonly Rgba ViaFg = Rgba.Hex(0xFFB060);
    private static readonly Rgba HoleFg = Rgba.Hex(0x4A7F5E);
    private static readonly Rgba FrameFg = Rgba.Hex(0x2E6B47);
    private static readonly Rgba PourFg = Rgba.Hex(0x1F5C38);
    private static readonly Rgba HeaderFg = Rgba.Hex(0x7FC79A);
    private static readonly Rgba StatusFg = Rgba.Hex(0x9ED9B3);
    private static readonly Rgba DrcFg = Rgba.Hex(0xFF4040);

    private static readonly string[] IcNames = { "ATMEGA", "74HC595", "NE555", "LM358", "ESP32", "MAX232", "CH340", "STM32" };

    // Направления: 0 N, 1 NE, 2 E, 3 SE, 4 S, 5 SW, 6 W, 7 NW. Бит в маске = 1 << индекс.
    private static readonly int[] DirDx = { 0, 1, 1, 1, 0, -1, -1, -1 };
    private static readonly int[] DirDy = { -1, -1, 0, 1, 1, 1, 0, -1 };
    private static readonly int[] DirBit = { 1, 2, 4, 8, 16, 32, 64, 128 };
    private const int OrthCost = 2, DiagCost = 3, ViaCost = 6;
    private const float RouteRate = 18f;          // ячеек/с при routeSpeed = 1 (плата разводится за ~60–90 с)
    private const float DrcChance = 0.12f;        // шанс «ошибки зазора» на цепь (один раз)
    private const float FadeTime = 1f;            // заливка проявляется за 1 с
    private const float PourTotal = 6f;          // заливка + 5 с паузы до новой платы

    private const int MaxComps = 64, MaxPads = 1024, MaxNets = 128;
    private const int NetTagUnrouted = 1000000;   // метка выводов без цепи (блокируют трассу)

    /// <summary>Маска → символ. Таблица строится один раз.</summary>
    private static readonly char[] MaskGlyph = BuildMaskGlyphs();

    private enum Phase { Route, Pour }

    private Random _rng = new(0);
    private float _speed = 1f, _routeSpeed = 1f, _density = 0.6f;
    private bool _two = true;
    private int _cols, _rows, _top, _bottom, _statusRow, _oy, _W, _H;
    private bool _header, _status;

    // Состояние поля (индексы — внутренние координаты платы, bx + by * W).
    private int[] _occTop = Array.Empty<int>(), _occBot = Array.Empty<int>();   // 0 свободно, >0 цепь+1, <0 вывод
    private byte[] _mTop = Array.Empty<byte>(), _mBot = Array.Empty<byte>();   // связи дорожек по направлениям
    private bool[] _via = Array.Empty<bool>(), _body = Array.Empty<bool>(), _pad = Array.Empty<bool>();
    private bool[] _pour = Array.Empty<bool>(), _pourTmp = Array.Empty<bool>(), _fp = Array.Empty<bool>();
    private byte[] _subTop = Array.Empty<byte>(), _subBot = Array.Empty<byte>();   // субпиксели секстелей: 2×3 на ячейку, биты как в GlyphSet
    private readonly bool[] _routedFlag = new bool[MaxNets];
    private char[] _sCh = Array.Empty<char>();
    private Rgba[] _sFg = Array.Empty<Rgba>(), _sBg = Array.Empty<Rgba>();

    // Компоненты и цепи (выделяются в Init).
    private int _compCount, _padCount, _netCount, _refU, _refR, _refC, _refJ;
    private readonly int[] _fbX0 = new int[MaxComps], _fbY0 = new int[MaxComps], _fbX1 = new int[MaxComps], _fbY1 = new int[MaxComps];
    private readonly int[] _padCell = new int[MaxPads], _padNet = new int[MaxPads], _padComp = new int[MaxPads];
    private readonly int[] _netA = new int[MaxNets], _netB = new int[MaxNets];
    private readonly bool[] _retried = new bool[MaxNets];

    // Поиск пути: состояние = cell * 2 + слой.
    private int[] _dist = Array.Empty<int>(), _stamp = Array.Empty<int>(), _prev = Array.Empty<int>();
    private int[] _hKey = Array.Empty<int>(), _hVal = Array.Empty<int>();
    private int _hCount, _gen;
    private int[] _pathCell = Array.Empty<int>(), _pathLayer = Array.Empty<int>();
    private int _pathLen, _blocked = -1;

    // Ход трассировки.
    private Phase _phase;
    private int _netIdx, _routedOk, _failed, _vias, _boardNo;
    private bool _active;
    private int _revealPos;
    private float _revealAcc, _pauseT, _pourT, _pourFrac, _drcT;

    // Время и звук.
    private float _t, _boost, _bass, _tBass;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _rng = rng;
        _speed = MathF.Max(0.05f, options.Speed);
        _routeSpeed = Math.Clamp(options.GetFloat(RouteSpeedSpec.Name, RouteSpeedSpec.Default), RouteSpeedSpec.Min, RouteSpeedSpec.Max);
        _density = Math.Clamp(options.GetFloat(DensitySpec.Name, DensitySpec.Default), DensitySpec.Min, DensitySpec.Max);
        _two = options.GetString(LayersSpec.Name, LayersSpec.Options![(int)LayersSpec.Default]) != "one";

        // Разметка: служебная строка сверху и снизу, рамка платы, внутри — поле трассировки.
        _cols = Math.Max(1, cols);
        _rows = Math.Max(1, rows);
        _header = _rows >= 10;
        _status = _rows >= 5;
        _top = _header ? 1 : 0;
        _bottom = _status ? _rows - 2 : _rows - 1;
        _statusRow = _rows - 1;
        _oy = _top + 1;
        _W = _cols - 2;
        _H = _bottom - _top - 1;

        int n = Math.Max(0, _W) * Math.Max(0, _H);
        _occTop = new int[n];
        _occBot = new int[n];
        _mTop = new byte[n];
        _mBot = new byte[n];
        _via = new bool[n];
        _body = new bool[n];
        _pad = new bool[n];
        _pour = new bool[n];
        _pourTmp = new bool[n];
        _fp = new bool[n];
        _subTop = new byte[n];
        _subBot = new byte[n];
        _sCh = new char[n];
        _sFg = new Rgba[n];
        _sBg = new Rgba[n];

        int states = 2 * n;
        _dist = new int[states];
        _stamp = new int[states];
        _prev = new int[states];
        _pathCell = new int[states];
        _pathLayer = new int[states];
        _hKey = new int[states * 9 + 2];
        _hVal = new int[states * 9 + 2];
        _gen = 0;

        _boardNo = 0;
        _t = _boost = _bass = _tBass = 0f;
        _drcT = 0f;
        Generate();
    }

    public void OnAudio(in AudioFrame audio)
    {
        _tBass = audio.Active ? audio.Bass : 0f;
        if (audio.Active && audio.Beat) _boost = 1f;
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;
        dt = MathF.Min(dt, 0.1f);
        float s = dt * _speed;                        // весь темп сцены — от суммы dt
        if (_W < 1 || _H < 1) return;

        float k = 1f - MathF.Exp(-dt * 12f);
        _bass += (_tBass - _bass) * k;
        _boost *= MathF.Exp(-s / 0.12f);
        _t += s;
        _drcT = MathF.Max(0f, _drcT - s);

        if (_phase == Phase.Pour)
        {
            _pourT += s;
            _pourFrac = Math.Min(1f, _pourT / FadeTime);
            if (_pourT >= PourTotal) Generate();
            return;
        }

        if (_pauseT > 0f) { _pauseT -= s; return; }
        if (_netIdx >= _netCount) { BeginPour(); return; }

        if (!_active)
        {
            if (!RouteNet(_netIdx))
            {
                _failed++;
                NextNet(0.05f);
                return;
            }
            _active = true;
            _revealPos = 0;
            _revealAcc = 0f;
            return;
        }

        _revealAcc += RouteRate * _routeSpeed * (1f + 3f * _boost) * s;
        while (_revealAcc >= 1f && _revealPos < _pathLen)
        {
            Reveal(_revealPos);
            _revealPos++;
            _revealAcc -= 1f;
        }
        if (_revealPos < _pathLen) return;

        // Дорожка проложена. Иногда трассировщик находит «ошибку зазора»: цепь рвётся и кладётся заново в обход.
        _active = false;
        if (!_retried[_netIdx] && _rng.NextSingle() < DrcChance)
        {
            _blocked = _pathCell[_pathLen / 2];
            RipUp();
            _retried[_netIdx] = true;
            _drcT = 1.6f;
            _pauseT = 0.4f;
            return;
        }
        _routedOk++;
        _routedFlag[_netIdx] = true;
        NextNet(0.05f);
    }

    public void Draw(CharGrid grid)
    {
        int cols = grid.Cols, rows = grid.Rows;
        bool board = _W >= 1 && _H >= 1;
        int tip = _phase == Phase.Route && _active && _revealPos > 0 ? _pathCell[_revealPos - 1] : -1;
        float tipK = 0.25f + 0.75f * _bass;
        Rgba tipFg = Rgba.Lerp(Copper, CopperTip, tipK);

        for (int y = 0; y < rows; y++)
        for (int x = 0; x < cols; x++)
        {
            char ch = ' ';
            Rgba fg = MaskBg, bg = MaskBg;
            if ((_header && y == 0) || (_status && y == _statusRow))
            {
                bg = StatusBg;
            }
            else if (board && (x == 0 || x == cols - 1) && y >= _top && y <= _bottom)
            {
                fg = FrameFg;
                ch = x == 0 ? (y == _top ? '┌' : y == _bottom ? '└' : '│') : (y == _top ? '┐' : y == _bottom ? '┘' : '│');
            }
            else if (board && y >= _top && y <= _bottom && (y == _top || y == _bottom))
            {
                fg = FrameFg;
                ch = '─';
            }
            else if (board && x >= 1 && x <= _W && y > _top && y < _bottom)
            {
                int bi = x - 1, bj = y - _oy, idx = bj * _W + bi;
                ch = _sCh[idx];
                fg = _sFg[idx];
                bg = _sBg[idx];
                if (_pad[idx]) { /* выводы поверх дорожек */ }
                else if (_via[idx]) { ch = '○'; fg = ViaFg; }
                else if (_subTop[idx] != 0 || (_two && _subBot[idx] != 0))
                {
                    // Дорожки — секстели: объединение слоёв, цвет верхнего меди приоритетнее.
                    byte bits = (byte)(_subTop[idx] | (_two ? _subBot[idx] : 0));
                    ch = GlyphSet.Sextant(bits);
                    fg = _subTop[idx] != 0 ? Copper : BlueTrack;
                }
                else if (_pour[idx] && _pourFrac > 0f && Hash(idx) < _pourFrac) { ch = '░'; fg = PourFg; }
                if (idx == tip) { ch = '●'; fg = tipFg; }
            }
            grid.Put(x, y, ch, fg, bg);
        }

        // Служебные строки: текст пишется поверх, без строк.
        if (_header)
        {
            int hx = 3;
            WriteStr(grid, ref hx, 0, "PCB REV C  AUTOROUTE  ", HeaderFg, StatusBg);
            WriteStr(grid, ref hx, 0, _two ? "2 LAYER" : "1 LAYER", HeaderFg, StatusBg);
            WriteStr(grid, ref hx, 0, "  BOARD ", HeaderFg, StatusBg);
            WriteNum(grid, ref hx, 0, _boardNo % 100, 2, HeaderFg, StatusBg);
        }
        if (_status)
        {
            int sx = 3;
            WriteStr(grid, ref sx, _statusRow, "ROUTED ", StatusFg, StatusBg);
            WriteNum(grid, ref sx, _statusRow, _routedOk, 1, StatusFg, StatusBg);
            WriteStr(grid, ref sx, _statusRow, "/", StatusFg, StatusBg);
            WriteNum(grid, ref sx, _statusRow, _netCount, 1, StatusFg, StatusBg);
            WriteStr(grid, ref sx, _statusRow, "  VIAS ", StatusFg, StatusBg);
            WriteNum(grid, ref sx, _statusRow, _vias, 1, StatusFg, StatusBg);
            WriteStr(grid, ref sx, _statusRow, "  ", StatusFg, StatusBg);
            if (_drcT > 0f)
                WriteStr(grid, ref sx, _statusRow, "DRC: CLEARANCE", ((int)(_t * 6f) & 1) == 0 ? DrcFg : StatusFg, StatusBg);
            else
                WriteStr(grid, ref sx, _statusRow, "DRC OK", StatusFg, StatusBg);
        }
    }

    // ───────────────────────── Генерация платы ─────────────────────────

    /// <summary>Новая плата: компоненты, выводы, список цепей. Аллокаций нет — массивы выделены в Init.</summary>
    private void Generate()
    {
        Array.Fill(_occTop, 0);
        Array.Fill(_occBot, 0);
        Array.Fill(_mTop, (byte)0);
        Array.Fill(_mBot, (byte)0);
        Array.Fill(_subTop, (byte)0);
        Array.Fill(_subBot, (byte)0);
        Array.Fill(_routedFlag, false);
        Array.Fill(_via, false);
        Array.Fill(_body, false);
        Array.Fill(_pad, false);
        Array.Fill(_pour, false);
        Array.Fill(_fp, false);
        Array.Fill(_sCh, ' ');
        Array.Fill(_sFg, MaskBg);
        Array.Fill(_sBg, MaskBg);
        Array.Fill(_retried, false);

        _compCount = 0;
        _padCount = 0;
        _netCount = 0;
        _refU = 0; _refR = 0; _refC = 0; _refJ = 0;
        _netIdx = 0; _routedOk = 0; _failed = 0; _vias = 0;
        _active = false; _blocked = -1; _revealPos = 0; _pathLen = 0;
        _revealAcc = 0f; _drcT = 0f;
        _phase = Phase.Route;
        _pauseT = 1f;
        _pourT = 0f;
        _pourFrac = 0f;
        _boardNo++;

        if (_W < 8 || _H < 6) return;

        // Крепёжные отверстия по углам поля.
        if (_W >= 6 && _H >= 5)
        {
            PlaceHole(1, 1);
            PlaceHole(_W - 2, 1);
            PlaceHole(1, _H - 2);
            PlaceHole(_W - 2, _H - 2);
        }

        int target = (int)(_density * 50f + 0.5f);     // 0.6 → 30 компонентов
        for (int attempt = 0; attempt < 600 && _compCount < target && _compCount < MaxComps; attempt++)
        {
            int r = _rng.Next(10);
            if (r < 3) TryIc();
            else if (r < 8) TryPassive();
            else TryConnector();
        }

        MakeNets();

        // Выводы без цепи тоже блокируют трассы (все выводы — сквозные, на обоих слоях).
        for (int i = 0; i < _padCount; i++)
        {
            int c = _padCell[i];
            int tag = _padNet[i] >= 0 ? -(_padNet[i] + 1) : -NetTagUnrouted;
            _occTop[c] = tag;
            _occBot[c] = tag;
        }
    }

    private void PlaceHole(int bx, int by)
    {
        int c = by * _W + bx;
        _body[c] = true;
        _fp[c] = true;
        _sCh[c] = '○';
        _sFg[c] = HoleFg;
    }

    private void TryIc()
    {
        int k = 2 + _rng.Next(5);                       // выводов с каждой стороны: 2..6, шаг 1 строка (DIP/SOIC)
        int name = _rng.Next(IcNames.Length);
        int refNo = ++_refU;
        int labelLen = 1 + DigitCount(refNo) + 1 + IcNames[name].Length;
        int w = Math.Max(8, labelLen + 1);
        int h = k + 2;                                  // корпус: строка над выводами, k строк выводов, строка под ними
        int x = 1 + _rng.Next(Math.Max(1, _W - w - 3));
        int y = 1 + _rng.Next(Math.Max(1, _H - h - 2));
        // Площадь: выводы слева и справа (x-1 и x+w), подпись сверху (y-1).
        if (!FootprintFree(x - 1, y - 1, x + w, y + h - 1)) return;

        for (int j = 0; j < h; j++)
        for (int i = 0; i < w; i++)
        {
            int px = x + i, py = y + j;
            bool edge = i == 0 || j == 0 || i == w - 1 || j == h - 1;
            int c = py * _W + px;
            _body[c] = true;
            _sCh[c] = !edge ? ' ' : (i == 0 && j == 0) ? '┌' : (i == w - 1 && j == 0) ? '┐'
                : (i == 0 && j == h - 1) ? '└' : (i == w - 1 && j == h - 1) ? '┘'
                : (j == 0 || j == h - 1) ? '─' : '│';
            _sFg[c] = BodyFg;
            _sBg[c] = BodyBg;
        }
        // Выемка сверху по центру и точка ключа у первого вывода.
        _sCh[y * _W + x + w / 2] = '◦';
        _sFg[y * _W + x + w / 2] = BodyFg;
        _sCh[(y + 1) * _W + x + 1] = '•';
        _sFg[(y + 1) * _W + x + 1] = KeyFg;

        // Подпись «U3 ATMEGA» над корпусом, блокирует трассы (это тоже занятые клетки).
        int cx = x;
        StaticText(ref cx, y - 1, "U", LabelFg);
        StaticNum(ref cx, y - 1, refNo, LabelFg);
        StaticText(ref cx, y - 1, " ", LabelFg);
        StaticText(ref cx, y - 1, IcNames[name], LabelFg);

        for (int i = 0; i < k; i++)
        {
            int py = y + 1 + i;
            AddPad(x - 1, py, _compCount);
            AddPad(x + w, py, _compCount);
        }
        AddComp(x - 1, y - 1, x + w, y + h - 1);
    }

    private void TryPassive()
    {
        bool cap = _rng.Next(2) == 0;
        int refNo = cap ? ++_refC : ++_refR;
        int x = 1 + _rng.Next(Math.Max(1, _W - 6));
        int y = 1 + _rng.Next(Math.Max(1, _H - 3));
        if (!FootprintFree(x - 1, y - 1, x + 3, y)) return;

        // Маленький корпус на 3 ячейки с двумя площадками: резистор «[▒]», конденсатор «|▒|».
        for (int i = 0; i < 3; i++)
        {
            int c = y * _W + x + i;
            _body[c] = true;
            _sCh[c] = i == 1 ? '▒' : (cap ? '|' : i == 0 ? '[' : ']');
            _sFg[c] = BodyFg;
            _sBg[c] = BodyBg;
        }
        int cx = x;
        StaticText(ref cx, y - 1, cap ? "C" : "R", LabelFg);
        StaticNum(ref cx, y - 1, refNo, LabelFg);
        AddPad(x - 1, y, _compCount);
        AddPad(x + 3, y, _compCount);
        AddComp(x - 1, y - 1, x + 3, y);
    }

    private void TryConnector()
    {
        int k = 4 + _rng.Next(5);                       // контактов: 4..8
        int x = 1 + _rng.Next(Math.Max(1, _W - 6));
        int y = 1 + _rng.Next(Math.Max(1, _H - k - 2));
        if (!FootprintFree(x - 1, y - 1, x + 3, y + k - 1)) return;

        int refNo = ++_refJ;
        for (int j = 0; j < k; j++)
        for (int i = 0; i < 3; i++)
        {
            int c = (y + j) * _W + x + i;
            _body[c] = true;
            _sCh[c] = '▓';
            _sFg[c] = BodyFg;
            _sBg[c] = BodyBg;
        }
        int cx = x;
        StaticText(ref cx, y - 1, "J", LabelFg);
        StaticNum(ref cx, y - 1, refNo, LabelFg);
        for (int j = 0; j < k; j++) AddPad(x + 3, y + j, _compCount);
        AddComp(x - 1, y - 1, x + 3, y + k - 1);
    }

    /// <summary>Область свободна (ни корпуса, ни подписи, ни соседнего компонента) и целиком внутри платы.</summary>
    private bool FootprintFree(int x0, int y0, int x1, int y1)
    {
        if (x0 < 0 || y0 < 0 || x1 >= _W || y1 >= _H) return false;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
            if (_fp[y * _W + x]) return false;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
            _fp[y * _W + x] = true;
        return true;
    }

    private void AddComp(int x0, int y0, int x1, int y1)
    {
        // Площадь уже помечена в FootprintFree; здесь только запоминаем её для проверок.
        if (_compCount >= MaxComps) return;
        _fbX0[_compCount] = x0; _fbY0[_compCount] = y0;
        _fbX1[_compCount] = x1; _fbY1[_compCount] = y1;
        _compCount++;
    }

    /// <summary>Вывод: клетка на краю корпуса. Блокируется не как корпус, а своей цепью (см. Generate).</summary>
    private void AddPad(int bx, int by, int comp)
    {
        if (_padCount >= MaxPads || (uint)bx >= (uint)_W || (uint)by >= (uint)_H) return;
        int c = by * _W + bx;
        _pad[c] = true;
        _sCh[c] = '▪';
        _sFg[c] = PadFg;
        _padCell[_padCount] = c;
        _padNet[_padCount] = -1;
        _padComp[_padCount] = comp;
        _padCount++;
    }

    private void StaticText(ref int x, int y, string s, Rgba fg)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if ((uint)x < (uint)_W && (uint)y < (uint)_H)
            {
                int c = y * _W + x;
                _sCh[c] = s[i];
                _sFg[c] = fg;
                _body[c] = true;
            }
            x++;
        }
    }

    private void StaticNum(ref int x, int y, int n, Rgba fg)
    {
        int digits = DigitCount(n);
        int div = 1;
        for (int i = 1; i < digits; i++) div *= 10;
        for (int i = 0; i < digits; i++)
        {
            StaticText(ref x, y, DigitStrings[(Math.Abs(n) / div) % 10], fg);
            div /= 10;
        }
    }

    private static readonly string[] DigitStrings = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };

    private static int DigitCount(int n)
    {
        int d = 1;
        for (int v = Math.Abs(n); v >= 10; v /= 10) d++;
        return d;
    }

    /// <summary>Цепи: пары выводов разных компонентов, ближайший из четырёх кандидатов. Порядок — случайный.</summary>
    private void MakeNets()
    {
        if (_padCount < 2) return;
        int maxNets = Math.Min(Math.Min(MaxNets, 100), _padCount / 2);
        Span<int> cand = stackalloc int[4];
        Span<int> candDist = stackalloc int[4];
        for (int n = 0; n < maxNets; n++)
        {
            int a = -1;
            int start = _rng.Next(_padCount);
            for (int i = 0; i < _padCount && a < 0; i++)
            {
                int p = (start + i) % _padCount;
                if (_padNet[p] < 0) a = p;
            }
            if (a < 0) break;

            // Четыре ближайших свободных вывода другого компонента (по Манхэттену), по возрастанию расстояния.
            int found = 0;
            int ax = _padCell[a] % _W, ay = _padCell[a] / _W;
            for (int b = 0; b < _padCount; b++)
            {
                if (_padNet[b] >= 0 || b == a || _padComp[b] == _padComp[a]) continue;
                int bx = _padCell[b] % _W, by = _padCell[b] / _W;
                int d = Math.Abs(bx - ax) + Math.Abs(by - ay);
                int ins;
                if (found < 4) { ins = found; found++; }
                else if (d < candDist[3]) ins = 3;
                else continue;
                while (ins > 0 && candDist[ins - 1] > d)
                {
                    cand[ins] = cand[ins - 1];
                    candDist[ins] = candDist[ins - 1];
                    ins--;
                }
                cand[ins] = b;
                candDist[ins] = d;
            }
            if (found == 0) break;

            int pick = cand[_rng.Next(found)];
            _padNet[a] = _netCount;
            _padNet[pick] = _netCount;
            _netA[_netCount] = a;
            _netB[_netCount] = pick;
            _retried[_netCount] = false;
            _netCount++;
        }
    }

    // ───────────────────────── Трассировка ─────────────────────────

    /// <summary>Выводы одной цепи: ищем путь Дейкстрой по (клетка, слой) и сразу резервируем клетки.</summary>
    private bool RouteNet(int net)
    {
        int tag = net + 1;
        int start = _padCell[_netA[net]], goal = _padCell[_netB[net]];
        _gen++;
        _hCount = 0;
        int s0 = start * 2;
        _stamp[s0] = _gen; _dist[s0] = 0; _prev[s0] = -1;
        HeapPush(0, s0);
        int found = -1;
        while (_hCount > 0)
        {
            HeapPop(out int d, out int s);
            if (d != _dist[s]) continue;
            int cell = s >> 1, layer = s & 1;
            if (cell == goal) { found = s; break; }
            int x = cell % _W, y = cell / _W;

            if (_two && !_pad[cell] && Open(cell, 1 - layer, tag, goal))
                Relax(cell * 2 + (1 - layer), d + ViaCost, s);

            for (int i = 0; i < 8; i++)
            {
                int nx = x + DirDx[i], ny = y + DirDy[i];
                if ((uint)nx >= (uint)_W || (uint)ny >= (uint)_H) continue;
                int ncell = ny * _W + nx;
                if (!Open(ncell, layer, tag, goal)) continue;
                bool diag = (i & 1) == 1;
                if (diag)
                {
                    // Диагональ не проходит между двумя чужими дорожками: обе соседние клетки должны быть свободны.
                    int ax = x + DirDx[i], ay = y, bx = x, by = y + DirDy[i];
                    if (!OrthFree(ax, ay, layer, tag) || !OrthFree(bx, by, layer, tag)) continue;
                }
                Relax(ncell * 2 + layer, d + (diag ? DiagCost : OrthCost), s);
            }
        }
        if (found < 0) return false;

        int len = 0;
        for (int s = found; s >= 0; s = _prev[s]) len++;
        int idx = len - 1;
        for (int s = found; s >= 0; s = _prev[s])
        {
            _pathCell[idx] = s >> 1;
            _pathLayer[idx] = s & 1;
            idx--;
        }
        _pathLen = len;

        for (int k = 0; k < len; k++)
        {
            int c = _pathCell[k];
            if (_pad[c]) continue;
            if (_pathLayer[k] == 0) _occTop[c] = tag; else _occBot[c] = tag;
        }
        // Угол диагонали резервируется цепью: чужая дорожка не войдёт в клетку, которую задевает наклонный сегмент.
        for (int k = 1; k < len; k++)
        {
            if (!DiagCorners(k, out int ca, out int cb)) continue;
            int[] occ = _pathLayer[k] == 0 ? _occTop : _occBot;
            if (!_pad[ca] && !_body[ca] && occ[ca] == 0) occ[ca] = tag;
            if (!_pad[cb] && !_body[cb] && occ[cb] == 0) occ[cb] = tag;
        }
        return true;
    }

    private void Relax(int ns, int nd, int from)
    {
        if (_stamp[ns] == _gen && nd >= _dist[ns]) return;
        _stamp[ns] = _gen;
        _dist[ns] = nd;
        _prev[ns] = from;
        HeapPush(nd, ns);
    }

    /// <summary>Клетка доступна цепи на слое: не корпус, не чужая дорожка и не чужой вывод. Цель — всегда доступна.</summary>
    private bool Open(int cell, int layer, int tag, int goal)
    {
        if (cell == _blocked || _body[cell]) return false;
        if (cell == goal) return true;
        int o = layer == 0 ? _occTop[cell] : _occBot[cell];
        return o == 0 || o == tag;
    }

    private bool OrthFree(int x, int y, int layer, int tag)
    {
        if ((uint)x >= (uint)_W || (uint)y >= (uint)_H) return false;
        int c = y * _W + x;
        if (_body[c]) return false;
        int o = layer == 0 ? _occTop[c] : _occBot[c];
        return o == 0 || o == tag;
    }

    /// <summary>Проявить клетку k пути: дорожка тянется от предыдущей клетки, переход — «○».</summary>
    private void Reveal(int k)
    {
        if (k == 0) return;
        int c = _pathCell[k], layer = _pathLayer[k];
        int pc = _pathCell[k - 1];
        if (pc == c)
        {
            _via[c] = true;
            _vias++;
            return;
        }
        DrawSubLine(layer == 0 ? _subTop : _subBot, pc % _W, pc / _W, c % _W, c / _W);
        int dx = c % _W - pc % _W, dy = c / _W - pc / _W;
        int i = 0;
        while (i < 8 && (DirDx[i] != dx || DirDy[i] != dy)) i++;
        if (i == 8) return;
        int j = (i + 4) & 7;
        if (layer == 0)
        {
            _mTop[pc] = (byte)(_mTop[pc] | DirBit[i]);
            _mTop[c] = (byte)(_mTop[c] | DirBit[j]);
        }
        else
        {
            _mBot[pc] = (byte)(_mBot[pc] | DirBit[i]);
            _mBot[c] = (byte)(_mBot[c] | DirBit[j]);
        }
    }

    /// <summary>
    /// Сегмент дорожки между центрами клеток в субпикселях (2×3 на ячейку). Большая ось — по шагам,
    /// поэтому линия без разрывов; диагональный шаг даёт лесенку. Биты пишутся только в ячейки самой цепи.
    /// </summary>
    private void DrawSubLine(byte[] sub, int ax, int ay, int bx, int by)
    {
        int x0 = 2 * ax + 1, y0 = 3 * ay + 1;
        int dx = 2 * (bx - ax), dy = 3 * (by - ay);
        int steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
        for (int i = 0; i <= steps; i++)
        {
            int px = x0 + (int)MathF.Round(dx * i / (float)steps);
            int py = y0 + (int)MathF.Round(dy * i / (float)steps);
            int cx = px >> 1, cy = py / 3;
            if ((uint)cx >= (uint)_W || (uint)cy >= (uint)_H) continue;
            int bit = (py - 3 * cy) * 2 + (px - 2 * cx);
            sub[cy * _W + cx] |= (byte)(1 << bit);
        }
    }

    /// <summary>Диагональный шаг пути k: две клетки-угла (x клетки k, y предыдущей) и (x предыдущей, y клетки k).</summary>
    private bool DiagCorners(int k, out int ca, out int cb)
    {
        ca = cb = -1;
        if (k == 0) return false;
        int c = _pathCell[k], pc = _pathCell[k - 1];
        if (c == pc || _pathLayer[k] != _pathLayer[k - 1]) return false;
        int dx = c % _W - pc % _W, dy = c / _W - pc / _W;
        if (Math.Abs(dx) != 1 || Math.Abs(dy) != 1) return false;
        ca = (pc / _W) * _W + c % _W;
        cb = (c / _W) * _W + pc % _W;
        return true;
    }

    /// <summary>Снять проложенную цепь (после «ошибки зазора»): клетки, субпиксели, маски и переходы освобождаются.</summary>
    private void RipUp()
    {
        int tag = _netIdx + 1;
        for (int k = 0; k < _pathLen; k++)
        {
            int c = _pathCell[k];
            if (k > 0 && _pathCell[k - 1] == c) _vias--;
            int[] occ = _pathLayer[k] == 0 ? _occTop : _occBot;
            byte[] sub = _pathLayer[k] == 0 ? _subTop : _subBot;
            if (!_pad[c]) occ[c] = 0;
            sub[c] = 0;
            _mTop[c] = 0;
            _mBot[c] = 0;
            _via[c] = false;
            if (DiagCorners(k, out int ca, out int cb))
            {
                if (occ[ca] == tag) occ[ca] = 0;
                if (occ[cb] == tag) occ[cb] = 0;
                sub[ca] = 0;
                sub[cb] = 0;
            }
        }
        _pathLen = 0;
        _revealPos = 0;
    }

    private void NextNet(float pause)
    {
        _netIdx++;
        _blocked = -1;
        _pauseT = pause;
    }

    /// <summary>Заливка земли: пустые клетки рядом с дорожками (радиус 2) штриховкой ░.</summary>
    private void BeginPour()
    {
        _phase = Phase.Pour;
        _pourT = 0f;
        _pourFrac = 0f;
        Array.Fill(_pourTmp, false);
        for (int c = 0; c < _pour.Length; c++)
        {
            if (!IsTrack(c)) continue;
            int x = c % _W, y = c / _W;
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((uint)nx < (uint)_W && (uint)ny < (uint)_H) _pourTmp[ny * _W + nx] = true;
            }
        }
        for (int c = 0; c < _pour.Length; c++)
        {
            _pour[c] = _pourTmp[c] && !IsTrack(c) && !_body[c] && _occTop[c] == 0 && _occBot[c] == 0 && _sCh[c] == ' ';
        }
    }

    private bool IsTrack(int c) => _via[c] || _mTop[c] != 0 || _mBot[c] != 0;

    // ───────────────────────── Очередь с приоритетом (без аллокаций) ─────────────────────────

    private void HeapPush(int key, int val)
    {
        int i = _hCount++;
        while (i > 0)
        {
            int p = (i - 1) >> 1;
            if (_hKey[p] <= key) break;
            _hKey[i] = _hKey[p];
            _hVal[i] = _hVal[p];
            i = p;
        }
        _hKey[i] = key;
        _hVal[i] = val;
    }

    private void HeapPop(out int key, out int val)
    {
        key = _hKey[0];
        val = _hVal[0];
        int n = --_hCount;
        if (n == 0) return;
        int k = _hKey[n], v = _hVal[n];
        int i = 0;
        while (true)
        {
            int c = 2 * i + 1;
            if (c >= n) break;
            if (c + 1 < n && _hKey[c + 1] < _hKey[c]) c++;
            if (_hKey[c] >= k) break;
            _hKey[i] = _hKey[c];
            _hVal[i] = _hVal[c];
            i = c;
        }
        _hKey[i] = k;
        _hVal[i] = v;
    }

    // ───────────────────────── Вспомогательное ─────────────────────────

    /// <summary>Детерминированная «случайность» на клетку, 0..1: заливка проявляется пятнами.</summary>
    private static float Hash(int i) => (float)((((uint)i * 2654435761u) >> 8) & 0xFFFF) / 65536f;

    private static char[] BuildMaskGlyphs()
    {
        var g = new char[256];
        for (int m = 0; m < 256; m++) g[m] = MaskGlyphOf(m);
        return g;
    }

    private static char MaskGlyphOf(int m)
    {
        bool n = (m & 1) != 0, ne = (m & 2) != 0, e = (m & 4) != 0, se = (m & 8) != 0;
        bool s = (m & 16) != 0, sw = (m & 32) != 0, w = (m & 64) != 0, nw = (m & 128) != 0;
        int count = 0;
        for (int i = 0; i < 8; i++) if ((m & (1 << i)) != 0) count++;
        if (count == 0) return ' ';
        if (count == 1) return n || s ? '│' : e || w ? '─' : ne || sw ? '/' : '\\';
        if (count == 2)
        {
            if (e && w) return '─';
            if (n && s) return '│';
            if (ne && sw) return '/';
            if (nw && se) return '\\';
            if (n && e) return '└';
            if (n && w) return '┘';
            if (s && e) return '┌';
            if (s && w) return '┐';
            return n || s ? '│' : '─';
        }
        return '┼';
    }

    private static void WriteStr(CharGrid g, ref int x, int y, string s, Rgba fg, Rgba bg)
    {
        for (int i = 0; i < s.Length; i++) g.Put(x++, y, s[i], fg, bg);
    }

    private static void WriteNum(CharGrid g, ref int x, int y, int n, int minDigits, Rgba fg, Rgba bg)
    {
        Span<char> buf = stackalloc char[10];
        int len = 0, v = Math.Abs(n);
        do { buf[len++] = (char)('0' + v % 10); v /= 10; } while (v > 0 && len < buf.Length);
        while (len < minDigits && len < buf.Length) buf[len++] = '0';
        for (int i = len - 1; i >= 0; i--) g.Put(x++, y, buf[i], fg, bg);
    }

    // Для тестов: состояние поля без публичного API.
    internal int BoardWidth => _W;
    internal int BoardHeight => _H;
    internal int Generation => _boardNo;
    internal int RoutedNets => _routedOk;
    internal int NetCount => _netCount;
    internal int ViaCount => _vias;
    internal bool Pouring => _phase == Phase.Pour;
    internal byte[] TopMask => _mTop;
    internal byte[] BottomMask => _mBot;
    internal int[] TopOwner => _occTop;
    internal int[] BottomOwner => _occBot;
    internal bool[] BodyCells => _body;
    internal int NetIndex => _netIdx;
    internal int ScreenOffsetY => _oy;
    internal bool IsRouted(int net) => _routedFlag[net];
    internal bool IsVia(int idx) => _via[idx];
    /// <summary>Выводы цепи в экранных координатах сетки (A и B).</summary>
    internal ((int X, int Y) A, (int X, int Y) B) NetPads(int net)
    {
        int a = _padCell[_netA[net]], b = _padCell[_netB[net]];
        return ((a % _W + 1, a / _W + _oy), (b % _W + 1, b / _W + _oy));
    }
    internal (int X0, int Y0, int X1, int Y1)[] ComponentBoxes()
    {
        var r = new (int, int, int, int)[_compCount];
        for (int i = 0; i < _compCount; i++) r[i] = (_fbX0[i], _fbY0[i], _fbX1[i], _fbY1[i]);
        return r;
    }
}
