using Phosphor.Core;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Phosphor.Scenes.Tests")]

namespace Phosphor.Scenes;

/// <summary>
/// Лабиринт из псевдографики: генерация по кругу (recursive backtracker, Prim, Kruskal), затем решение от входа
/// (верх-лево) к выходу (низ-право): A*, волна BFS или правило правой руки. Волна разливается тепловой картой,
/// найденный путь яркой линией проступает от выхода к входу. Пауза 3 с, лабиринт растворяется, начинается новый.
/// Звука нет: сцена живёт на dt.
/// </summary>
public sealed class MazeScene : IScene, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec CellSpec = new("cellSize", SceneParamKind.Int, 2f, 1f, 4f, "размер клетки лабиринта");
    public static readonly SceneParamSpec SpeedSpec = new("speed", SceneParamKind.Float, 1f, 0.2f, 5f, "скорость генерации и решения");
    public static readonly SceneParamSpec SolverSpec = new("solver", SceneParamKind.Choice, 0f, 0f, 2f, "алгоритм решения",
        new[] { "astar", "bfs", "wall" });
    private static readonly SceneParamSpec[] ParamList = { CellSpec, SpeedSpec, SolverSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    public string Id => "maze";

    private static readonly string[] GenNames = { "BACKTRACKER", "PRIM", "KRUSKAL" };
    private static readonly string[] SolverNames = { "A*", "BFS", "WALL" };

    // Маска стыка: 1 — влево, 2 — вправо, 4 — вверх, 8 — вниз. Индекс = маска.
    private const string JunctionGlyphs = " ───│┘└┴│┐┌┬│┤├┼";

    private enum Phase { Gen, Solve, Path, Hold, Dissolve }

    private const float HoldSeconds = 3f;
    private const float DissolveSeconds = 1.5f;
    private const float PathSeconds = 1f;
    private const byte MarkNone = 0, MarkSeen = 1, MarkFront = 2, MarkPath = 3;
    private const int MarginX = 3;                 // отступ поля от левого и правого краёв, колонок
    private const float DissolveBand = 0.15f;      // ширина полосы, где стены гаснут

    private int _cs, _mw, _mh, _n;
    private int _pcw, _pch, _px, _py, _uw, _uh, _ox, _oy;
    private int _ixc, _iyc;                         // центр прохода: линия пути идёт через него
    private float _speedParam, _speed;
    private int _solver;
    private Random _rng = new(0);
    private Rgba _base;

    private bool[] _hW = Array.Empty<bool>();   // горизонтальные линии: (mw) x (mh+1)
    private bool[] _vW = Array.Empty<bool>();   // вертикальные линии: (mw+1) x (mh)
    private byte[] _mark = Array.Empty<byte>();
    private int[] _stack = Array.Empty<int>();
    private int[] _front = Array.Empty<int>();  // Prim: (клетка << 2 | направление)
    private int[] _uf = Array.Empty<int>();     // Kruskal: система непересекающихся множеств
    private int[] _edges = Array.Empty<int>();  // Kruskal: (клетка << 1 | 0 — восток, 1 — юг), перемешаны
    private int[] _queue = Array.Empty<int>();
    private int[] _dist = Array.Empty<int>();
    private int[] _parent = Array.Empty<int>();
    private int[] _heapK = Array.Empty<int>();
    private int[] _heapV = Array.Empty<int>();
    private int[] _trail = Array.Empty<int>();
    private int[] _path = Array.Empty<int>();
    private byte[] _arms = Array.Empty<byte>(); // стыки линии пути в клетке: 1 — влево, 2 — вправо, 4 — вверх, 8 — вниз
    private Rgba[] _cellBg = Array.Empty<Rgba>();

    private int _frontCount, _edgeCount, _edgePos, _stackTop, _carved, _head;
    private int _heapCount, _qHead, _qTail;
    private int _gen, _mazeNo, _explored, _maxDist, _pathLen, _pathReveal;
    private int _wallDir, _trailLen;
    private Phase _phase;
    private float _acc, _phaseT, _genRate, _solveRate, _pathRate, _fade;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _rng = rng;
        _cs = Math.Clamp(options.GetInt(CellSpec.Name, (int)CellSpec.Default), (int)CellSpec.Min, (int)CellSpec.Max);
        _speedParam = Math.Clamp(options.GetFloat(SpeedSpec.Name, SpeedSpec.Default), SpeedSpec.Min, SpeedSpec.Max);
        _speed = _speedParam * MathF.Max(0f, options.Speed);
        string solverName = options.GetString(SolverSpec.Name, SolverSpec.Options![(int)SolverSpec.Default]);
        _solver = Math.Max(0, Array.IndexOf(SolverSpec.Options!, solverName));
        _base = Palettes.Base(options.Palette);

        // Клетка лабиринта: проход pcw×pch символов, стенка — одна строка/колонка. Ширина прохода — по CellAspect,
        // чтобы проход выглядел квадратным.
        _pch = _cs;
        _pcw = Math.Max(1, (int)MathF.Round(_cs * options.CellAspect));
        _px = _pcw + 1;
        _py = _pch + 1;
        _ixc = (_pcw + 1) / 2;
        _iyc = (_pch + 1) / 2;
        _mw = Math.Max(0, (cols - 2 * MarginX - 1) / _px);   // по краям поля — отступ от экрана
        _mh = Math.Max(0, (rows - 2) / _py);            // последняя строка — статус
        _n = _mw * _mh;
        _uw = _mw > 0 ? _mw * _px + 1 : 0;
        _uh = _mh > 0 ? _mh * _py + 1 : 0;
        _ox = Math.Max(0, (cols - _uw) / 2);
        _oy = Math.Max(0, (rows - 1 - _uh) / 2);

        int nn = Math.Max(1, _n);
        _hW = new bool[Math.Max(1, _mw * (_mh + 1))];
        _vW = new bool[Math.Max(1, (_mw + 1) * _mh)];
        _mark = new byte[nn];
        _stack = new int[nn];
        _front = new int[4 * nn];
        _uf = new int[nn];
        _edges = new int[2 * nn];
        _queue = new int[nn];
        _dist = new int[nn];
        _parent = new int[nn];
        _heapK = new int[4 * nn + 4];
        _heapV = new int[4 * nn + 4];
        _trail = new int[2 * nn + 4];
        _path = new int[2 * nn + 4];
        _arms = new byte[nn];
        _cellBg = new Rgba[nn];

        _mazeNo = 0;
        _fade = 1f;
        _acc = _phaseT = 0f;
        if (_n > 0) StartMaze(0);
        else _phase = Phase.Hold;
        UpdateCellColors();
    }

    public void Update(float dt)
    {
        dt = Math.Clamp(dt, 0f, 0.1f);
        if (!(dt > 0f) || _n == 0) return;

        switch (_phase)
        {
            case Phase.Gen:
                _acc += _genRate * _speed * dt;
                while (_acc >= 1f)
                {
                    _acc -= 1f;
                    if (GenStep()) { StartSolve(); break; }
                }
                break;

            case Phase.Solve:
                _acc += _solveRate * _speed * dt;
                while (_acc >= 1f)
                {
                    _acc -= 1f;
                    if (SolveStep()) { BeginPath(); break; }
                }
                break;

            case Phase.Path:
                _acc += _pathRate * _speed * dt;
                while (_acc >= 1f && _pathReveal < _pathLen)
                {
                    _acc -= 1f;
                    RevealPath(_pathReveal);
                    _pathReveal++;
                }
                if (_pathReveal >= _pathLen)
                {
                    _phase = Phase.Hold;
                    _phaseT = 0f;
                }
                break;

            case Phase.Hold:
                _phaseT += dt;
                if (_phaseT >= HoldSeconds)
                {
                    _phase = Phase.Dissolve;
                    _phaseT = 0f;
                }
                break;

            case Phase.Dissolve:
                // Волна от краёв к центру: k уходит с 1.15 до −0.15, полоса DissolveBand плавно гасит стены.
                _phaseT += dt;
                _fade = 1.15f - 1.3f * (_phaseT / DissolveSeconds);
                if (_fade <= -DissolveBand)
                {
                    _mazeNo++;
                    StartMaze(_mazeNo % 3);
                }
                break;
        }

        if (_phase != Phase.Dissolve) _fade = 1f;
        UpdateCellColors();
    }

    public void Draw(CharGrid grid)
    {
        grid.Clear(Rgba.Black);
        if (_n == 0) return;

        bool solving = _phase != Phase.Gen;
        bool dissolving = _phase == Phase.Dissolve;
        Rgba wallFg = _base.Scale(solving ? 0.5f : 0.6f);
        Rgba markFg = Rgba.Lerp(_base, Rgba.White, 0.8f);
        Rgba pathFg = Rgba.Lerp(_base, Rgba.White, 0.9f);
        float halfW = _uw * 0.5f, halfH = _uh * 0.5f;

        for (int uy = 0; uy < _uh; uy++)
        for (int ux = 0; ux < _uw; ux++)
        {
            int ix = ux % _px, iy = uy % _py;
            int ci = ux / _px, cj = uy / _py;
            char ch = ' ';
            Rgba fg = wallFg, bg = Rgba.Black;

            if (ix == 0 && iy == 0)
            {
                ch = JunctionGlyph(ci, cj);
            }
            else if (ix == 0)
            {
                // Вертикальная линия: стена, либо проём (пролом на входе и выходе, линия пути через проём).
                if (VWall(ci, cj)) ch = (ci == 0 || ci == _mw) ? '║' : '│';
                else
                {
                    bg = Brighter(Owner(ci - 1, cj), Owner(ci, cj));
                    if (iy == _iyc && LinkedAcross(ci, cj)) { ch = '─'; fg = pathFg; }
                    if (iy == _iyc && ci == 0 && cj == 0) { ch = '►'; fg = markFg; }
                    if (iy == _iyc && ci == _mw && cj == _mh - 1) { ch = '◄'; fg = markFg; }
                }
            }
            else if (iy == 0)
            {
                if (HWall(ci, cj)) ch = (cj == 0 || cj == _mh) ? '═' : '─';
                else
                {
                    bg = Brighter(Owner(ci, cj - 1), Owner(ci, cj));
                    if (ix == _ixc && LinkedDown(ci, cj)) { ch = '│'; fg = pathFg; }
                }
            }
            else
            {
                // Проход клетки: линия пути — тонкая, по центрам проходов, от центра к стыкам с соседями.
                int c = Owner(ci, cj);
                bg = _cellBg[c];
                byte a = _arms[c];
                if (a != 0)
                {
                    if (iy == _iyc && ix == _ixc) { ch = JunctionGlyphs[a]; fg = pathFg; }
                    else if (iy == _iyc)
                    {
                        if ((ix < _ixc && (a & 1) != 0) || (ix > _ixc && (a & 2) != 0)) { ch = '─'; fg = pathFg; }
                    }
                    else if (ix == _ixc)
                    {
                        if ((iy < _iyc && (a & 4) != 0) || (iy > _iyc && (a & 8) != 0)) { ch = '│'; fg = pathFg; }
                    }
                }
            }

            float v = 1f;
            if (dissolving)
            {
                // Растворение: край поля гаснет первым, центр — последним.
                float r = MathF.Max(MathF.Abs(ux + 0.5f - halfW) / halfW, MathF.Abs(uy + 0.5f - halfH) / halfH);
                v = Math.Clamp((_fade + DissolveBand - r) / DissolveBand, 0f, 1f);
                if (v <= 0f) continue;
            }
            grid.Put(_ox + ux, _oy + uy, ch, fg.Scale(v), bg.Scale(v));
        }

        // Статусная строка: GEN: BACKTRACKER  CELLS 1840  SOLVE: A*  VISITED 912  PATH 233
        int y = grid.Rows - 1;
        Rgba sfg = _base.Scale(0.8f * (dissolving ? Math.Clamp(_fade, 0f, 1f) : 1f));
        int x = 1;
        grid.Text(x, y, "GEN: ", sfg); x += 5;
        grid.Text(x, y, GenNames[_gen], sfg); x += GenNames[_gen].Length;
        grid.Text(x, y, "  CELLS ", sfg); x += 8;
        x = PutNum(grid, x, y, _n, sfg);
        grid.Text(x, y, "  SOLVE: ", sfg); x += 9;
        grid.Text(x, y, SolverNames[_solver], sfg); x += SolverNames[_solver].Length;
        grid.Text(x, y, "  VISITED ", sfg); x += 10;
        x = PutNum(grid, x, y, _explored, sfg);
        grid.Text(x, y, "  PATH ", sfg); x += 7;
        PutNum(grid, x, y, _pathLen, sfg);
    }

    /// <summary>Число по одной цифре в ячейку, без строк и аллокаций. Возвращает x после последней цифры.</summary>
    private static int PutNum(CharGrid g, int x, int y, int v, Rgba fg)
    {
        int digits = 1;
        for (int t = v; t >= 10; t /= 10) digits++;
        for (int i = digits - 1; i >= 0; i--)
        {
            g.Put(x + i, y, (char)('0' + v % 10), fg);
            v /= 10;
        }
        return x + digits;
    }

    // ---- Хуки для тестов ----

    internal int CellCount => _n;
    internal (int W, int H) MazeSize => (_mw, _mh);
    internal bool GenerationDone => _phase != Phase.Gen;
    internal bool PathReady => _phase is Phase.Hold or Phase.Dissolve;
    internal int PathLength => _pathLen;
    internal int PathAt(int i) => _path[i];
    internal void RestartWith(int gen) => StartMaze(gen);

    // ---- Геометрия ----

    private int HIdx(int ci, int cj) => cj * _mw + ci;
    private int VIdx(int ci, int cj) => cj * (_mw + 1) + ci;
    private bool HWall(int ci, int cj) => _hW[HIdx(ci, cj)];
    private bool VWall(int ci, int cj) => _vW[VIdx(ci, cj)];

    /// <summary>Направления: 0 — север, 1 — восток, 2 — юг, 3 — запад.</summary>
    private int Delta(int d) => d == 0 ? -_mw : d == 1 ? 1 : d == 2 ? _mw : -1;

    private bool HasNeighbour(int c, int d)
    {
        int ci = c % _mw, cj = c / _mw;
        return d switch { 0 => cj > 0, 1 => ci < _mw - 1, 2 => cj < _mh - 1, _ => ci > 0 };
    }

    /// <summary>Есть ли проход из клетки c в направлении d (стенки нет).</summary>
    internal bool IsOpen(int c, int d)
    {
        if (!HasNeighbour(c, d)) return false;   // проломы ведут наружу, а не к соседней клетке
        int ci = c % _mw, cj = c / _mw;
        return d switch
        {
            0 => !_hW[HIdx(ci, cj)],
            1 => !_vW[VIdx(ci + 1, cj)],
            2 => !_hW[HIdx(ci, cj + 1)],
            _ => !_vW[VIdx(ci, cj)],
        };
    }

    private void Carve(int c, int d)
    {
        int ci = c % _mw, cj = c / _mw;
        switch (d)
        {
            case 0: _hW[HIdx(ci, cj)] = false; break;
            case 1: _vW[VIdx(ci + 1, cj)] = false; break;
            case 2: _hW[HIdx(ci, cj + 1)] = false; break;
            default: _vW[VIdx(ci, cj)] = false; break;
        }
    }

    /// <summary>Клетка, которая отвечает за символ (клампится к сетке клеток — для границ).</summary>
    private int Owner(int ci, int cj)
    {
        ci = Math.Clamp(ci, 0, _mw - 1);
        cj = Math.Clamp(cj, 0, _mh - 1);
        return cj * _mw + ci;
    }

    private static int Sum(Rgba c) => c.R + c.G + c.B;

    private Rgba Brighter(int a, int b)
    {
        Rgba x = _cellBg[a], y = _cellBg[b];
        return Sum(x) >= Sum(y) ? x : y;
    }

    /// <summary>
    /// Узел (ci, cj). Внешняя рамка двойная, внутренние стены одинарные. Пролом убирает кусок рамки,
    /// поэтому стык на рамке смотрит только в те стороны, где стена есть.
    /// </summary>
    private char JunctionGlyph(int ci, int cj)
    {
        bool top = cj == 0, bottom = cj == _mh, left = ci == 0, right = ci == _mw;
        bool l = ci > 0 && HWall(ci - 1, cj);
        bool r = ci < _mw && HWall(ci, cj);
        bool u = cj > 0 && VWall(ci, cj - 1);
        bool d = cj < _mh && VWall(ci, cj);

        if (top && left) return r && d ? '╔' : r ? '═' : d ? '║' : ' ';
        if (top && right) return l && d ? '╗' : l ? '═' : d ? '║' : ' ';
        if (bottom && left) return r && u ? '╚' : r ? '═' : u ? '║' : ' ';
        if (bottom && right) return l && u ? '╝' : l ? '═' : u ? '║' : ' ';
        if (top) return d ? '╤' : '═';
        if (bottom) return u ? '╧' : '═';
        if (left)
        {
            if (u && d) return r ? '╟' : '║';
            if (d && r) return '╓';
            if (u && r) return '╙';
            if (u || d) return '║';
            return r ? '─' : ' ';
        }
        if (right)
        {
            if (u && d) return l ? '╢' : '║';
            if (d && l) return '╖';
            if (u && l) return '╜';
            if (u || d) return '║';
            return l ? '─' : ' ';
        }
        int mask = (l ? 1 : 0) | (r ? 2 : 0) | (u ? 4 : 0) | (d ? 8 : 0);
        return JunctionGlyphs[mask];
    }

    /// <summary>Линия пути пересекает вертикальный проём между клетками (ci-1, cj) и (ci, cj).</summary>
    private bool LinkedAcross(int ci, int cj) =>
        (ci >= 1 && (_arms[Owner(ci - 1, cj)] & 2) != 0) || (ci < _mw && (_arms[Owner(ci, cj)] & 1) != 0);

    /// <summary>Линия пути пересекает горизонтальный проём между клетками (ci, cj-1) и (ci, cj).</summary>
    private bool LinkedDown(int ci, int cj) =>
        (cj >= 1 && (_arms[Owner(ci, cj - 1)] & 8) != 0) || (cj < _mh && (_arms[Owner(ci, cj)] & 4) != 0);

    /// <summary>Направление стыка от клетки from к соседней to: 1 — влево, 2 — вправо, 4 — вверх, 8 — вниз.</summary>
    private int ArmTo(int from, int to)
    {
        int fi = from % _mw, ti = to % _mw;
        if (ti == fi + 1) return 2;
        if (ti == fi - 1) return 1;
        return to < from ? 4 : 8;
    }

    /// <summary>Линия пути проступает от выхода: клетка r соединяется с уже проявленной клеткой r-1.</summary>
    private void RevealPath(int r)
    {
        int c = _path[r];
        _mark[c] = MarkPath;
        if (r > 0)
        {
            int p = _path[r - 1];
            _arms[c] |= (byte)ArmTo(c, p);
            _arms[p] |= (byte)ArmTo(p, c);
        }
        if (r == 0) _arms[c] |= 2;                  // выход: линия уходит в пролом справа
        if (r == _pathLen - 1) _arms[c] |= 1;       // вход: линия уходит в пролом слева
    }

    // ---- Генерация ----

    private void StartMaze(int gen)
    {
        _gen = gen;
        Array.Fill(_hW, true);
        Array.Fill(_vW, true);
        Array.Fill(_mark, MarkNone);
        Array.Fill(_arms, (byte)0);
        for (int c = 0; c < _n; c++) _uf[c] = c;
        // Проломы во внешней рамке: вход — левая стена верхней клетки, выход — правая стена нижней.
        _vW[VIdx(0, 0)] = false;
        _vW[VIdx(_mw, _mh - 1)] = false;
        _carved = 0;
        _stackTop = 0;
        _frontCount = 0;
        _edgeCount = 0;
        _edgePos = 0;
        _head = -1;
        _explored = 0;
        _maxDist = 0;
        _pathLen = 0;
        _pathReveal = 0;
        _phase = Phase.Gen;
        _acc = 0f;
        _phaseT = 0f;
        _fade = 1f;

        // Генерация занимает ~4 с на типичном экране (1920×1080), на маленьком — короче, но не мгновенно.
        float genSeconds = Math.Clamp(_n / 180f, 1.5f, 6f);
        _genRate = 3f * _n / genSeconds;   // шаги включают откаты и пустые выборы, их ~2–3N

        if (gen == 0)
        {
            _stack[_stackTop++] = 0;
            Head(0);
        }
        else if (gen == 1)
        {
            Head(0);
            for (int d = 0; d < 4; d++)
                if (HasNeighbour(0, d)) _front[_frontCount++] = d;
        }
        else
        {
            int e = 0;
            for (int c = 0; c < _n; c++)
            {
                if (c % _mw < _mw - 1) _edges[e++] = c << 1;
                if (c / _mw < _mh - 1) _edges[e++] = (c << 1) | 1;
            }
            _edgeCount = e;
            for (int i = _edgeCount - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (_edges[i], _edges[j]) = (_edges[j], _edges[i]);
            }
        }
    }

    /// <summary>Текущая голова генерации: яркая. Прежнюю голову — в тусклый «пройдено».</summary>
    private void Head(int c)
    {
        if (_head >= 0 && _mark[_head] == MarkFront) _mark[_head] = MarkSeen;
        _mark[c] = MarkFront;
        _head = c;
    }

    /// <summary>Один шаг генерации. True — лабиринт совершенный и готов.</summary>
    private bool GenStep()
    {
        switch (_gen)
        {
            case 0: BacktrackStep(); break;
            case 1: PrimStep(); break;
            default: KruskalStep(); break;
        }
        return _carved >= _n - 1;
    }

    private void BacktrackStep()
    {
        if (_stackTop == 0) return;
        int c = _stack[_stackTop - 1];
        int pick = -1, cnt = 0;
        for (int d = 0; d < 4; d++)
        {
            if (!HasNeighbour(c, d) || _mark[c + Delta(d)] != MarkNone) continue;
            cnt++;
            if (_rng.Next(cnt) == 0) pick = d;   // равномерный выбор среди свободных соседей
        }
        if (pick < 0)
        {
            _stackTop--;
            if (_stackTop > 0) Head(_stack[_stackTop - 1]);
            return;
        }
        int nb = c + Delta(pick);
        Carve(c, pick);
        _carved++;
        _stack[_stackTop++] = nb;
        Head(nb);
    }

    private void PrimStep()
    {
        if (_frontCount == 0) return;
        int k = _rng.Next(_frontCount);
        int e = _front[k];
        _front[k] = _front[--_frontCount];
        int c = e >> 2, d = e & 3;
        int nb = c + Delta(d);
        if (_mark[nb] != MarkNone) return;
        Carve(c, d);
        _carved++;
        Head(nb);
        for (int dd = 0; dd < 4; dd++)
            if (HasNeighbour(nb, dd) && _mark[nb + Delta(dd)] == MarkNone)
                _front[_frontCount++] = (nb << 2) | dd;
    }

    private void KruskalStep()
    {
        if (_edgePos >= _edgeCount) return;
        int e = _edges[_edgePos++];
        int c = e >> 1;
        int d = (e & 1) == 0 ? 1 : 2;
        int nb = c + Delta(d);
        int rc = Find(c), rn = Find(nb);
        if (rc == rn) return;
        _uf[rc] = rn;
        Carve(c, d);
        _carved++;
        if (_mark[c] == MarkNone) _mark[c] = MarkSeen;
        Head(nb);
    }

    private int Find(int x)
    {
        while (_uf[x] != x)
        {
            _uf[x] = _uf[_uf[x]];
            x = _uf[x];
        }
        return x;
    }

    // ---- Решение ----

    private int Heur(int c) => Math.Abs(c % _mw - (_mw - 1)) + Math.Abs(c / _mw - (_mh - 1));

    private void StartSolve()
    {
        _phase = Phase.Solve;
        _acc = 0f;
        _phaseT = 0f;
        // Решение занимает 1–4 с.
        float solveSeconds = Math.Clamp(_n / 250f, 1f, 4f);
        _solveRate = _n / solveSeconds;
        Array.Fill(_mark, MarkNone);
        Array.Fill(_dist, -1);
        _explored = 0;
        _maxDist = 0;
        _pathLen = 0;
        _pathReveal = 0;
        _heapCount = 0;
        _qHead = 0;
        _qTail = 0;
        _dist[0] = 0;
        _parent[0] = -1;
        _explored = 1;

        if (_solver == 0)
        {
            _mark[0] = MarkFront;
            HeapPush(Heur(0), 0);
        }
        else if (_solver == 1)
        {
            _mark[0] = MarkFront;
            _queue[_qTail++] = 0;
        }
        else
        {
            _mark[0] = MarkFront;
            _wallDir = 0;
            _trailLen = 0;
            _trail[_trailLen++] = 0;
        }
    }

    /// <summary>Один шаг решения. True — выход достигнут.</summary>
    private bool SolveStep()
    {
        int exit = _n - 1;
        if (_solver == 0) return AStarStep(exit);
        if (_solver == 1) return BfsStep(exit);
        return WallStep(exit);
    }

    private bool AStarStep(int exit)
    {
        if (_heapCount == 0) return true;
        int c = HeapPop();
        if (_mark[c] == MarkSeen) return false;   // устаревшая запись
        _mark[c] = MarkSeen;
        _explored++;
        if (c == exit) return true;
        for (int d = 0; d < 4; d++)
        {
            if (!IsOpen(c, d)) continue;
            int nb = c + Delta(d);
            if (_dist[nb] >= 0) continue;          // в дереве это предок
            _dist[nb] = _dist[c] + 1;
            if (_dist[nb] > _maxDist) _maxDist = _dist[nb];
            _parent[nb] = c;
            if (_mark[nb] == MarkNone) _mark[nb] = MarkFront;
            HeapPush(_dist[nb] + Heur(nb), nb);
        }
        return false;
    }

    private bool BfsStep(int exit)
    {
        if (_qHead >= _qTail) return true;
        int c = _queue[_qHead++];
        _mark[c] = MarkSeen;
        _explored++;
        if (c == exit) return true;
        for (int d = 0; d < 4; d++)
        {
            if (!IsOpen(c, d)) continue;
            int nb = c + Delta(d);
            if (_dist[nb] >= 0) continue;
            _dist[nb] = _dist[c] + 1;
            if (_dist[nb] > _maxDist) _maxDist = _dist[nb];
            _parent[nb] = c;
            _queue[_qTail++] = nb;
            _mark[nb] = MarkFront;
        }
        return false;
    }

    /// <summary>Правило правой руки: мышь смотрит на север у входа, на каждом шаге пробует поворот вправо первым.</summary>
    private bool WallStep(int exit)
    {
        int c = _trail[_trailLen - 1];
        if (c == exit) return true;
        for (int k = 0; k < 4; k++)
        {
            int off = k == 0 ? 1 : k == 1 ? 0 : k == 2 ? 3 : 2;   // право, прямо, лево, назад
            int d = (_wallDir + off) & 3;
            if (!IsOpen(c, d)) continue;
            int nb = c + Delta(d);
            _wallDir = d;
            if (_dist[nb] < 0)
            {
                _dist[nb] = _trailLen;
                _explored++;
            }
            if (_mark[c] == MarkFront) _mark[c] = MarkSeen;
            _mark[nb] = MarkFront;
            _trail[_trailLen++] = nb;
            break;
        }
        return false;
    }

    private void BeginPath()
    {
        int exit = _n - 1;
        if (_solver == 2)
        {
            // Путь мыши — это её след; проступает от выхода к входу.
            _pathLen = _trailLen;
            for (int i = 0; i < _trailLen; i++) _path[i] = _trail[_trailLen - 1 - i];
        }
        else
        {
            _pathLen = 0;
            for (int p = exit; p != -1; p = _parent[p]) _path[_pathLen++] = p;
        }
        _phase = Phase.Path;
        _pathReveal = 0;
        _acc = 0f;
        // Путь проступает примерно за секунду.
        _pathRate = Math.Max(10f, _pathLen / PathSeconds);
    }

    // ---- Кучa (A*) ----

    private void HeapPush(int key, int val)
    {
        int i = _heapCount++;
        while (i > 0)
        {
            int p = (i - 1) >> 1;
            if (_heapK[p] <= key) break;
            _heapK[i] = _heapK[p];
            _heapV[i] = _heapV[p];
            i = p;
        }
        _heapK[i] = key;
        _heapV[i] = val;
    }

    private int HeapPop()
    {
        int top = _heapV[0];
        int last = --_heapCount;
        if (last > 0)
        {
            int key = _heapK[last], val = _heapV[last];
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1;
                if (l >= last) break;
                int r = l + 1;
                int m = (r < last && _heapK[r] < _heapK[l]) ? r : l;
                if (_heapK[m] >= key) break;
                _heapK[i] = _heapK[m];
                _heapV[i] = _heapV[m];
                i = m;
            }
            _heapK[i] = key;
            _heapV[i] = val;
        }
        return top;
    }

    // ---- Цвета клеток (считаются в Update, Draw только читает) ----

    private void UpdateCellColors()
    {
        bool solving = _phase != Phase.Gen;
        Rgba head = Rgba.Lerp(_base, Rgba.White, 0.5f);
        for (int c = 0; c < _n; c++)
        {
            byte m = _mark[c];
            Rgba bg = Rgba.Black;
            if (!solving)
            {
                if (m == MarkSeen) bg = _base.Scale(0.10f);
                else if (m == MarkFront) bg = head;
            }
            else
            {
                // Тепловая карта тусклая, градиент по расстоянию от входа (15–30%); фронт волны — около 60%.
                // Путь рисуется линией поверх (см. Draw), здесь фон клетки остаётся тусклым.
                float t = _maxDist > 0 ? (float)Math.Max(0, _dist[c]) / _maxDist : 0f;
                if (m == MarkSeen || m == MarkPath) bg = _base.Scale(0.15f + 0.15f * t);
                else if (m == MarkFront) bg = _base.Scale(0.6f);
            }
            _cellBg[c] = bg;
        }
    }
}
