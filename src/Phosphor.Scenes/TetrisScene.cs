using Phosphor.Core;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Phosphor.Scenes.Tests")]

namespace Phosphor.Scenes;

/// <summary>
/// Тетрис в режиме attract mode: играет сам, как автомат в ожидании монеты. Стакан 10×20 в рамке ║ ╚═╝, клетка —
/// два символа ▀ в ширину (светлая верхняя половина, тёмная нижняя). ИИ перебирает повороты и позиции и выбирает ход
/// по эвристике El-Tetris (высота, дыры, неровность, снятые линии), иногда берёт 2–3-й вариант. Снятые линии мигают
/// белым, четыре линии — «TETRIS!». Игра кончается серым заполнением снизу вверх и GAME OVER.
/// Звук: удар роняет фигуру, если она уже стоит над целью.
/// </summary>
public sealed class TetrisScene : IScene, IAudioReactive, IPreferredRows, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec LevelSpec = new("level", SceneParamKind.Int, 5f, 1f, 15f, "стартовый уровень (скорость)");
    public static readonly SceneParamSpec MistakesSpec = new("mistakes", SceneParamKind.Float, 0.05f, 0f, 0.5f, "шанс ошибки ИИ");
    public static readonly SceneParamSpec GhostSpec = new("ghost", SceneParamKind.Bool, 1f, 0f, 1f, "тень падения");
    private static readonly SceneParamSpec[] ParamList = { LevelSpec, MistakesSpec, GhostSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    public string Id => "tetris";

    // Высота сетки для хоста: стакан 21 строка + заголовок и поля — крупные ячейки на весь экран.
    public int PreferredRows => 26;

    private const int W = 10, H = 20;
    private const int WellCols = 2 * W + 2;          // со стенками, клетка = 2 символа
    private const int SpawnX = 3, SpawnY = -4;       // коробка фигуры появляется над стаканом
    private const float RotStep = 0.12f, MoveStep = 0.07f;
    private const float SpawnDelay = 0.2f, FlashTime = 0.3f, TetrisTime = 1.0f;
    private const float FillStepTime = 0.1f, FillTime = 2.0f, TextTime = 5.0f;
    private const int GrayCell = 8;

    private static readonly int[] LineScore = { 0, 40, 100, 300, 1200 };

    // Базовые клетки фигур (строка, столбец) в коробке. 1 I, 2 O, 3 T, 4 S, 5 Z, 6 J, 7 L.
    private static readonly int[][] BaseCells =
    {
        null!,
        new[] { 1, 0, 1, 1, 1, 2, 1, 3 },
        new[] { 0, 0, 0, 1, 1, 0, 1, 1 },
        new[] { 0, 1, 1, 0, 1, 1, 1, 2 },
        new[] { 0, 1, 0, 2, 1, 0, 1, 1 },
        new[] { 0, 0, 0, 1, 1, 1, 1, 2 },
        new[] { 0, 0, 1, 0, 1, 1, 1, 2 },
        new[] { 0, 2, 1, 0, 1, 1, 1, 2 },
    };
    private static readonly int[] BoxSize = { 0, 4, 2, 3, 3, 3, 3, 3 };

    // Форма с индексом kind*4+rot: 4 пары (строка, столбец) относительно левого верхнего угла коробки.
    private static readonly int[][] Shapes = BuildShapes();
    private static readonly int[] ShapeMinC = ColRange(true);
    private static readonly int[] ShapeMaxC = ColRange(false);

    private static readonly Rgba Amber = Rgba.Hex(0xFFB000);
    private static readonly Rgba Bright = Rgba.Hex(0xE0E0E0);
    private static readonly Rgba Frame = Rgba.Hex(0x6A6A6A);       // тусклая серая рамка
    private static readonly Rgba GridDot = Rgba.Hex(0x2A2A2A);     // сетка точек в клетках стакана

    // Классические цвета фигур, приглушённые до ~75%. Индекс 0 — пусто, 1..7 — фигуры, 8 — серый при GAME OVER.
    // I голубой, O жёлтый, T фиолетовый, S зелёный, Z красный, J синий, L оранжевый.
    private static readonly Rgba[] KindBase =
    {
        Rgba.Black,
        Rgba.Hex(0x00BFBF), Rgba.Hex(0xBFBF00), Rgba.Hex(0x8000BF), Rgba.Hex(0x00BF00),
        Rgba.Hex(0xBF0000), Rgba.Hex(0x0043BF), Rgba.Hex(0xBF6900),
        Rgba.Hex(0x606060),
    };
    // Блок из двух █ одного цвета фигуры: без полос и граней.
    private static readonly Rgba[] KindLeft = Shade(KindBase, 1f);
    private static readonly Rgba[] KindRight = KindLeft;

    private static Rgba[] Shade(Rgba[] src, float k)
    {
        var res = new Rgba[src.Length];
        for (int i = 0; i < src.Length; i++) res[i] = src[i].Scale(k);
        return res;
    }

    private int _wx, _wy;                             // левый верхний угол стакана
    private float _speed = 1f;
    private int _startLevel = 5;
    private float _mistakes = 0.05f;
    private bool _ghost = true;
    private Random _rng = new(1);

    private readonly int[] _board = new int[W * H];
    private readonly int[] _sim = new int[W * H];
    private readonly bool[] _rowFlash = new bool[H];
    private readonly bool[] _rowFull = new bool[H];
    private readonly int[] _colH = new int[W];
    private readonly int[] _stats = new int[8];
    private readonly int[] _bag = new int[7];
    private readonly int[] _candRot = new int[3], _candX = new int[3];
    private readonly float[] _candS = new float[3];
    private int _candCount;

    private int _curKind, _curRot, _curX, _curY;
    private bool _active;
    private int _tgtRot, _tgtX;                       // куда ведёт ИИ: ротация и столбец коробки
    private float _stepT, _fallT, _spawnT;
    private bool _clearing;
    private float _clearT;
    private int _clearCount;
    private float _tetrisT;
    private bool _over;
    private float _overT;
    private bool _beatPending;
    private int _next, _hold;
    private bool _canHold;
    private int _bagPos;
    private int _score, _lines, _level, _locks;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _rng = rng;
        _speed = MathF.Max(0f, options.Speed);
        _startLevel = Math.Clamp(options.GetInt(LevelSpec.Name, (int)LevelSpec.Default), (int)LevelSpec.Min, (int)LevelSpec.Max);
        _mistakes = Math.Clamp(options.GetFloat(MistakesSpec.Name, MistakesSpec.Default), MistakesSpec.Min, MistakesSpec.Max);
        _ghost = options.GetBool(GhostSpec.Name, true);
        _wx = (cols - WellCols) / 2;
        _wy = (rows - H - 1) / 2;
        _locks = 0;
        NewGame();
    }

    private void NewGame()
    {
        Array.Clear(_board, 0, _board.Length);
        Array.Clear(_rowFlash, 0, _rowFlash.Length);
        Array.Clear(_stats, 0, _stats.Length);
        _score = 0;
        _lines = 0;
        _level = _startLevel;
        _hold = 0;
        _canHold = true;
        _bagPos = 7;
        _active = false;
        _clearing = false;
        _over = false;
        _overT = 0f;
        _tetrisT = 0f;
        _beatPending = false;
        _stepT = 0f;
        _fallT = 0f;
        _next = Bag();
        _spawnT = 0f;                                  // первая фигура сразу, без паузы у пустого стакана
    }

    public void OnAudio(in AudioFrame audio)
    {
        if (audio.Active && audio.Beat) _beatPending = true;
    }

    public void Update(float dt)
    {
        dt = Math.Clamp(dt, 0f, 0.1f) * _speed;
        if (dt <= 0f) return;
        if (_tetrisT > 0f) _tetrisT -= dt;

        if (_over)
        {
            UpdateOver(dt);
            return;
        }
        if (_clearing)
        {
            _clearT -= dt;
            if (_clearT <= 0f) FinishClear();
            return;
        }
        if (!_active)
        {
            _spawnT -= dt;
            if (_spawnT <= 0f) SpawnPiece();
            return;
        }

        bool beat = _beatPending;
        _beatPending = false;

        // Сначала поворот и сдвиг к цели по шагам, пока фигура выше стакана.
        if (_curRot != _tgtRot)
        {
            _stepT += dt;
            if (_stepT >= RotStep)
            {
                _stepT -= RotStep;
                _curRot = (_curRot + 1) & 3;
            }
            return;
        }
        if (_curX != _tgtX)
        {
            _stepT += dt;
            if (_stepT >= MoveStep)
            {
                _stepT -= MoveStep;
                _curX += Math.Sign(_tgtX - _curX);
            }
            return;
        }

        // Фигура над целью: удар роняет её сразу.
        int shape = _curKind * 4 + _curRot;
        int landY = DropY(shape, _curX, _curY, _board);
        if (beat)
        {
            _curY = landY;
            Lock();
            return;
        }

        _fallT += dt;
        float g = GravitySeconds();
        while (_fallT >= g && _curY < landY)
        {
            _curY++;
            _fallT -= g;
        }
        if (_curY >= landY)
        {
            _fallT = 0f;
            Lock();
        }
    }

    private float GravitySeconds() => MathF.Max(0.03f, 0.12f * MathF.Pow(0.85f, _level - 1));

    private void SpawnPiece()
    {
        int kind = _next;
        _next = Bag();
        if (_canHold)
        {
            // Держать фигуру, если следующая (или уже отложенная) ставится заметно лучше текущей.
            float cur = BestOf(kind);
            int alt = _hold != 0 ? _hold : _next;
            if (BestOf(alt) > cur)
            {
                _canHold = false;
                if (_hold != 0)
                {
                    int tmp = _hold;
                    _hold = kind;
                    kind = tmp;
                }
                else
                {
                    _hold = kind;
                    kind = _next;
                    _next = Bag();
                }
            }
        }

        _curKind = kind;
        if (!Fits(kind * 4, SpawnX, -2, _board))
        {
            StartGameOver();
            return;
        }
        _stats[kind]++;
        ChooseTarget(kind);
        _curRot = 0;
        _curX = SpawnX;
        _curY = SpawnY;
        _stepT = 0f;
        _fallT = 0f;
        _active = true;
    }

    private void ChooseTarget(int kind)
    {
        Search(kind);
        int k = 0;
        if (_candCount > 1 && _rng.NextDouble() < _mistakes) k = 1 + _rng.Next(Math.Min(2, _candCount - 1));
        _tgtRot = _candRot[k];
        _tgtX = _candX[k];
    }

    private float BestOf(int kind)
    {
        Search(kind);
        return _candCount > 0 ? _candS[0] : float.NegativeInfinity;
    }

    /// <summary>Перебор всех поворотов и столбцов; в _cand* остаются три лучших варианта по убыванию оценки.</summary>
    private void Search(int kind)
    {
        _candCount = 0;
        for (int rot = 0; rot < 4; rot++)
        {
            int shape = kind * 4 + rot;
            for (int x = -ShapeMinC[shape]; x <= W - 1 - ShapeMaxC[shape]; x++)
            {
                int y = DropY(shape, x, SpawnY, _board);
                AddCandidate(rot, x, Evaluate(shape, x, y));
            }
        }
    }

    private void AddCandidate(int rot, int x, float s)
    {
        if (_candCount == 3 && s <= _candS[2]) return;
        int i = _candCount < 3 ? _candCount++ : 2;
        while (i > 0 && _candS[i - 1] < s)
        {
            _candS[i] = _candS[i - 1];
            _candRot[i] = _candRot[i - 1];
            _candX[i] = _candX[i - 1];
            i--;
        }
        _candS[i] = s;
        _candRot[i] = rot;
        _candX[i] = x;
    }

    /// <summary>Оценка El-Tetris после постановки фигуры: высота, снятые линии, дыры, неровность.</summary>
    private float Evaluate(int shape, int x, int y)
    {
        Array.Copy(_board, _sim, _board.Length);
        int kind = shape >> 2;
        var c = Shapes[shape];
        for (int i = 0; i < 8; i += 2)
        {
            int by = y + c[i], bx = x + c[i + 1];
            if (by >= 0 && by < H && bx >= 0 && bx < W) _sim[by * W + bx] = kind;
        }

        int lines = 0;
        for (int r = 0; r < H; r++)
        {
            bool full = true;
            for (int col = 0; col < W && full; col++) full = _sim[r * W + col] != 0;
            _rowFull[r] = full;
            if (full) lines++;
        }

        // Высоты и дыры считаем уже без снятых строк.
        int agg = 0, holes = 0;
        for (int col = 0; col < W; col++)
        {
            int top = -1;
            for (int r = 0; r < H; r++)
            {
                if (!_rowFull[r] && _sim[r * W + col] != 0)
                {
                    top = r;
                    break;
                }
            }
            int h = top < 0 ? 0 : H - top;
            _colH[col] = h;
            agg += h;
            if (top >= 0)
                for (int r = top + 1; r < H; r++)
                    if (!_rowFull[r] && _sim[r * W + col] == 0) holes++;
        }

        int bump = 0;
        for (int col = 0; col < W - 1; col++) bump += Math.Abs(_colH[col] - _colH[col + 1]);

        return -0.510066f * agg + 0.760666f * lines - 0.35663f * holes - 0.184483f * bump;
    }

    /// <summary>Фигура падает от y вниз, пока может. Клетки выше стакана (y&lt;0) не считаются столкновениями.</summary>
    private static int DropY(int shape, int x, int y, int[] board)
    {
        while (Fits(shape, x, y + 1, board)) y++;
        return y;
    }

    private static bool Fits(int shape, int x, int y, int[] board)
    {
        var c = Shapes[shape];
        for (int i = 0; i < 8; i += 2)
        {
            int bx = x + c[i + 1], by = y + c[i];
            if (bx < 0 || bx >= W || by >= H) return false;
            if (by >= 0 && board[by * W + bx] != 0) return false;
        }
        return true;
    }

    private void Lock()
    {
        var c = Shapes[_curKind * 4 + _curRot];
        for (int i = 0; i < 8; i += 2)
        {
            int by = _curY + c[i], bx = _curX + c[i + 1];
            if (by >= 0 && by < H && bx >= 0 && bx < W) _board[by * W + bx] = _curKind;
        }
        _active = false;
        _canHold = true;
        _locks++;

        int n = 0;
        for (int r = 0; r < H; r++)
        {
            bool full = true;
            for (int col = 0; col < W && full; col++) full = _board[r * W + col] != 0;
            _rowFlash[r] = full;
            if (full) n++;
        }
        if (n > 0)
        {
            _clearing = true;
            _clearT = FlashTime;
            _clearCount = n;
            if (n == 4) _tetrisT = TetrisTime;
        }
        else
        {
            _spawnT = SpawnDelay;
        }
    }

    private void FinishClear()
    {
        int write = H - 1;
        for (int r = H - 1; r >= 0; r--)
        {
            if (_rowFlash[r]) continue;
            if (write != r) Array.Copy(_board, r * W, _board, write * W, W);
            write--;
        }
        for (int r = write; r >= 0; r--) Array.Fill(_board, 0, r * W, W);

        _score += LineScore[_clearCount] * (_level + 1);
        _lines += _clearCount;
        _level = _startLevel + _lines / 10;
        Array.Clear(_rowFlash, 0, _rowFlash.Length);
        _clearing = false;
        _spawnT = SpawnDelay;
    }

    private void StartGameOver()
    {
        _over = true;
        _overT = 0f;
        _active = false;
        _beatPending = false;
    }

    /// <summary>Серое заполнение снизу вверх, потом GAME OVER и INSERT COIN, потом новая игра.</summary>
    private void UpdateOver(float dt)
    {
        _overT += dt;
        int filled = Math.Min(H, (int)(_overT / FillStepTime));
        for (int i = 0; i < filled; i++) Array.Fill(_board, GrayCell, (H - 1 - i) * W, W);
        if (_overT >= FillTime + TextTime) NewGame();
    }

    /// <summary>Случайный порядок семи фигур: каждые семь выданных фигур содержат все семь видов.</summary>
    private int Bag()
    {
        if (_bagPos >= 7)
        {
            for (int i = 0; i < 7; i++) _bag[i] = i + 1;
            for (int i = 6; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (_bag[i], _bag[j]) = (_bag[j], _bag[i]);
            }
            _bagPos = 0;
        }
        return _bag[_bagPos++];
    }

    public void Draw(CharGrid grid)
    {
        grid.Clear(Rgba.Black);
        grid.Text(_wx + 3, _wy - 1, "TETRIS  AUTOPLAY", Amber);
        DrawWell(grid);
        DrawPanels(grid);
        DrawOverlays(grid);
    }

    private void DrawWell(CharGrid g)
    {
        for (int r = 0; r < H; r++)
        {
            g.Put(_wx, _wy + r, '║', Frame);
            g.Put(_wx + WellCols - 1, _wy + r, '║', Frame);
        }
        g.Put(_wx, _wy + H, '╚', Frame);
        g.Put(_wx + WellCols - 1, _wy + H, '╝', Frame);
        for (int c = 1; c < WellCols - 1; c++) g.Put(_wx + c, _wy + H, '═', Frame);

        // Почти чёрный стакан с сеткой точек по клеткам.
        for (int r = 0; r < H; r++)
        for (int c = 0; c < W; c++)
            g.Put(_wx + 1 + c * 2, _wy + r, '·', GridDot);

        for (int r = 0; r < H; r++)
        for (int c = 0; c < W; c++)
        {
            int v = _board[r * W + c];
            if (v == 0) continue;
            if (_rowFlash[r]) Paint(g, _wx + 1 + c * 2, _wy + r, Rgba.White, Rgba.White);
            else Block(g, c, r, v);
        }

        if (!_active) return;
        int shape = _curKind * 4 + _curRot;
        var cells = Shapes[shape];
        if (_ghost)
        {
            int gy = DropY(shape, _curX, _curY, _board);
            if (gy != _curY)
            {
                var ghostColor = KindBase[_curKind].Scale(0.45f);
                for (int i = 0; i < 8; i += 2)
                {
                    int x = _wx + 1 + (_curX + cells[i + 1]) * 2, y = _wy + gy + cells[i];
                    g.Put(x, y, '░', ghostColor);
                    g.Put(x + 1, y, '░', ghostColor);
                }
            }
        }
        for (int i = 0; i < 8; i += 2) Block(g, _curX + cells[i + 1], _curY + cells[i], _curKind);
    }

    /// <summary>Клетка стакана (bx, by) — два символа в ширину. Строки выше стакана (by&lt;0) тоже рисуются.</summary>
    private void Block(CharGrid g, int bx, int by, int kind) =>
        Paint(g, _wx + 1 + bx * 2, _wy + by, KindLeft[kind], KindRight[kind]);

    /// <summary>Сплошной блок из двух ячеек █: левая и правая ячейки своим оттенком.</summary>
    private static void Paint(CharGrid g, int x, int y, Rgba left, Rgba right)
    {
        g.Put(x, y, '█', left);
        g.Put(x + 1, y, '█', right);
    }

    private void DrawPanels(CharGrid g)
    {
        int lx = _wx - 17, rx = _wx + WellCols + 3;

        // Слева: статистика, по 3 строки на фигуру (иконка 2 строки и пустая), число на уровне верхней строки иконки.
        g.Text(lx, _wy - 1, "STATS", Amber);
        for (int k = 1; k <= 7; k++)
        {
            int y = _wy + 3 * (k - 1);
            PreviewSmall(g, lx, y, k);
            DrawNumber(g, lx + 10, y, _stats[k], 4, Bright);
        }

        // Справа: HOLD, NEXT, счёт.
        g.Text(rx, _wy, "HOLD", Amber);
        if (_hold != 0) Preview(g, rx, _wy + 1, _hold);
        g.Text(rx, _wy + 4, "NEXT", Amber);
        Preview(g, rx, _wy + 5, _next);
        g.Text(rx, _wy + 8, "SCORE", Amber);
        DrawNumber(g, rx, _wy + 9, _score, 8, Bright);
        g.Text(rx, _wy + 11, "LINES", Amber);
        DrawNumber(g, rx, _wy + 12, _lines, 8, Bright);
        g.Text(rx, _wy + 14, "LEVEL", Amber);
        DrawNumber(g, rx, _wy + 15, _level, 8, Bright);
    }

    /// <summary>Иконка фигуры в её стартовой ориентации: клетка = 2 символа в ширину (ячейка квадратная), цвет фигуры.</summary>
    private void PreviewSmall(CharGrid g, int x, int y, int kind)
    {
        var c = Shapes[kind * 4];
        var color = KindBase[kind];
        for (int i = 0; i < 8; i += 2)
        {
            int px = x + 2 * c[i + 1], py = y + c[i];
            g.Put(px, py, '█', color);
            g.Put(px + 1, py, '█', color);
        }
    }

    /// <summary>Фигура в её стартовой ориентации блоками того же вида, что в стакане.</summary>
    private void Preview(CharGrid g, int x, int y, int kind)
    {
        var c = Shapes[kind * 4];
        for (int i = 0; i < 8; i += 2) Paint(g, x + 2 * c[i + 1], y + c[i], KindLeft[kind], KindRight[kind]);
    }

    private void DrawOverlays(CharGrid g)
    {
        if (_tetrisT > 0f) g.Text(_wx + 7, _wy + 5, "TETRIS!", Rgba.White, Rgba.Black);
        if (_over && _overT >= FillTime)
        {
            g.Text(_wx + 6, _wy + 9, "GAME OVER", Amber, Rgba.Black);
            if (((int)(_overT * 2.5f) & 1) == 0) g.Text(_wx + 5, _wy + 11, "INSERT COIN", Bright, Rgba.Black);
        }
    }

    /// <summary>Число справа налево в width ячеек, ведущие нули не пишем.</summary>
    private static void DrawNumber(CharGrid g, int x, int y, int v, int width, Rgba fg)
    {
        if (v < 0) v = 0;
        for (int i = width - 1; i >= 0; i--)
        {
            char ch = v > 0 || i == width - 1 ? (char)('0' + v % 10) : ' ';
            g.Put(x + i, y, ch, fg);
            v /= 10;
        }
    }

    private static int[][] BuildShapes()
    {
        var res = new int[32][];
        for (int k = 1; k <= 7; k++)
        {
            int n = BoxSize[k];
            var cur = BaseCells[k];
            for (int r = 0; r < 4; r++)
            {
                res[k * 4 + r] = cur;
                var next = new int[8];
                for (int i = 0; i < 8; i += 2)
                {
                    next[i] = cur[i + 1];
                    next[i + 1] = n - 1 - cur[i];
                }
                cur = next;
            }
        }
        return res;
    }

    private static int[] ColRange(bool min)
    {
        var res = new int[32];
        for (int s = 4; s < 32; s++)
        {
            int v = min ? int.MaxValue : int.MinValue;
            for (int i = 1; i < 8; i += 2) v = min ? Math.Min(v, Shapes[s][i]) : Math.Max(v, Shapes[s][i]);
            res[s] = v;
        }
        return res;
    }

    // Хуки для тестов.
    internal int Lines => _lines;
    internal int Locks => _locks;
    internal int Score => _score;
    internal bool GameOver => _over;
    internal int WellX => _wx;
    internal int WellY => _wy;
    internal bool PieceAligned => _active && _curRot == _tgtRot && _curX == _tgtX;
    internal int RowsToLanding => _active ? DropY(_curKind * 4 + _curRot, _curX, _curY, _board) - _curY : -1;
    internal bool Clearing => _clearing;
    internal int FullRowsOnBoard
    {
        get
        {
            int n = 0;
            for (int r = 0; r < H; r++)
            {
                bool full = true;
                for (int col = 0; col < W && full; col++) full = _board[r * W + col] != 0;
                if (full) n++;
            }
            return n;
        }
    }
    internal int NextPieceForTest() => Bag();
    internal void ResetBagForTest() => _bagPos = 7;
}
