using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// Жизнь Конвея (B3/S23) на торе. Поле в полублоках: W = cols, H = rows × 2, каждая ячейка
/// экрана рисуется символом '▀' (верх — цвет клетки 2·row, низ — 2·row+1).
/// Призраки погибших клеток затухают; при застое поле плавно перезапускается.
/// </summary>
public sealed class LifeScene : IScene, IAudioReactive, IHandoffReceiver
{
    private const int BurstRadiusMin = 4;          // взрыв: кольцо радиусом 4..7
    private const int BurstRadiusRange = 4;
    private const int BurstThicknessMax = 2;       // толщина 1..2
    private const float BurstFill = 0.7f;          // доля клеток кольца, которые оживают (пропуски)
    private const float GhostMax = 0.35f;         // яркость призрака сразу после смерти
    private const float GhostDecayPerSec = 0.7f;   // 0.35 → 0 примерно за 0.5 с
    private const float FadeDuration = 1.0f;       // старое поле гаснет
    private const float RevealDuration = 1.0f;     // новое поле проявляется
    private const float MaxRoundSeconds = 180f;    // раунд не длиннее 3 минут симуляции
    private const int HistoryLen = 64;             // буфер хешей для поиска осцилляторов
    private const int PopWindow = 200;             // окно популяции для проверки застоя
    private const int MaxStepsPerUpdate = 64;      // защита от лавины поколений при больших dt
    private const int AgeCap = 100000;
    private const int OldAge = 30;

    private static readonly (int X, int Y)[] Gosper =
    {
        (24,0),(22,1),(24,1),(12,2),(13,2),(20,2),(21,2),(34,2),(35,2),(11,3),(15,3),(20,3),(21,3),
        (34,3),(35,3),(0,4),(1,4),(10,4),(16,4),(20,4),(21,4),(0,5),(1,5),(10,5),(14,5),(16,5),(17,5),
        (22,5),(24,5),(10,6),(16,6),(24,6),(11,7),(15,7),(12,8),(13,8),
    };
    private static readonly (int X, int Y)[] RPentomino = { (1,0), (2,0), (0,1), (1,1), (1,2) };
    private static readonly (int X, int Y)[] Acorn = { (1,0), (3,1), (0,2), (1,2), (4,2), (5,2), (6,2) };
    private static readonly (int X, int Y)[] Glider = { (1,0), (2,1), (0,2), (1,2), (2,2) };
    private static readonly (int X, int Y)[][] Figures = { Gosper, RPentomino, Acorn, Glider };

    private enum Phase { Running, Fading, Revealing }

    public string Id => "life";

    /// <summary>Сколько раз поле перезапускалось из-за застоя (с последнего Init).</summary>
    public int Restarts { get; private set; }

    private int _cols, _rows, _w, _h;
    private Random _rng = new(0);
    private float _gensPerSec = 12f;
    private float _acc;
    private Phase _phase = Phase.Running;
    private float _phaseLeft;
    private float _roundTime;
    private bool _gliderPending;
    private bool _burstPending;

    /// <summary>Сколько клеток родил взрыв в последний удар (рождения из мёртвых; эволюция сюда не входит).</summary>
    public int LastBurstBirths { get; private set; }

    /// <summary>Число живых клеток (для проверок в тестах).</summary>
    public int LiveCells
    {
        get
        {
            int n = 0;
            foreach (var c in _cur) if (c != 0) n++;
            return n;
        }
    }

    private byte[] _cur = Array.Empty<byte>();
    private byte[] _next = Array.Empty<byte>();
    private byte[] _seed = Array.Empty<byte>();
    private int[] _order = Array.Empty<int>();
    private int[] _age = Array.Empty<int>();
    private float[] _ghost = Array.Empty<float>();
    private int _revealTotal, _revealPos;

    private readonly ulong[] _hist = new ulong[HistoryLen];
    private int _histCount, _histPos;
    private readonly int[] _pop = new int[PopWindow];
    private int _popCount, _popPos;

    private Rgba _base, _newborn, _old;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = cols;
        _rows = rows;
        _w = cols;
        _h = rows * 2;
        _rng = rng;
        _gensPerSec = MathF.Max(0f, options.GetFloat("genPerSec", 12f)) * MathF.Max(0f, options.Speed);
        _base = BaseColor(options.Palette);
        _newborn = Rgba.Lerp(Rgba.White, _base, 0.2f);
        _old = _base.Scale(0.6f);

        int n = _w * _h;
        _cur = new byte[n];
        _next = new byte[n];
        _seed = new byte[n];
        _order = new int[n];
        _age = new int[n];
        _ghost = new float[n];

        Restarts = 0;
        _acc = 0f;
        _roundTime = 0f;
        _gliderPending = false;
        _burstPending = false;
        LastBurstBirths = 0;
        _phase = Phase.Running;
        _phaseLeft = 0f;
        _revealTotal = 0;
        _revealPos = 0;
        ResetDetectors();

        Seed(_cur, rng);
        for (int i = 0; i < n; i++)
            if (_cur[i] != 0) _age[i] = 1;
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;

        for (int i = 0; i < _ghost.Length; i++)
        {
            float g = _ghost[i];
            if (g > 0f)
            {
                g -= dt * GhostDecayPerSec;
                _ghost[i] = g > 0f ? g : 0f;
            }
        }

        switch (_phase)
        {
            case Phase.Fading: FadeStep(dt); break;
            case Phase.Revealing: RevealStep(dt); break;
            default: RunStep(dt); break;
        }
    }

    public void Draw(CharGrid grid)
    {
        var cells = grid.Cells;
        int cols = Math.Min(_cols, grid.Cols), rows = Math.Min(_rows, grid.Rows);
        for (int r = 0; r < rows; r++)
        {
            int top = 2 * r * _w, bottom = top + _w;
            int rowBase = r * grid.Cols;
            for (int x = 0; x < cols; x++)
                cells[rowBase + x] = new Cell('▀', ColorAt(top + x), ColorAt(bottom + x));
        }
    }

    public void OnAudio(in AudioFrame audio)
    {
        if (audio.Active && audio.Beat)
        {
            _gliderPending = true;
            _burstPending = true;
        }
    }

    /// <summary>Один шаг Конвея (B3/S23) на торе. src и dst — поля w×h из 0/1, dst не должен совпадать с src.</summary>
    public static void Step(byte[] src, byte[] dst, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            int yu = (y == 0 ? h - 1 : y - 1) * w;
            int yc = y * w;
            int yd = (y == h - 1 ? 0 : y + 1) * w;
            for (int x = 0; x < w; x++)
            {
                int xl = x == 0 ? w - 1 : x - 1;
                int xr = x == w - 1 ? 0 : x + 1;
                int n = src[yu + xl] + src[yu + x] + src[yu + xr]
                      + src[yc + xl] + src[yc + xr]
                      + src[yd + xl] + src[yd + x] + src[yd + xr];
                int alive = src[yc + x];
                dst[yc + x] = (byte)(n == 3 || (alive != 0 && n == 2) ? 1 : 0);
            }
        }
    }

    private const float SilhouetteLum = 0.35f;   // яркость полупикселя, выше которой он становится живым
    private const float DustRate = 0.04f;        // доля случайной «пыли» поверх силуэта

    /// <summary>
    /// Эстафета: живые клетки — полупиксели поля с яркостью больше 0.35 (силуэт прошлой сцены) плюс 4% пыли.
    /// На силуэте стоят призраки 0.35. Силуэт меньше 1% поля — обычный посев.
    /// Полупиксель: у '▀' верх = Fg, низ = Bg; у остальных символов оба — яркость ячейки.
    /// </summary>
    public void Seed(CharGrid previous)
    {
        int n = _w * _h;
        int pc = previous.Cols, pr = previous.Rows;
        var sil = _seed; // маска силуэта; буфер полностью очищается в BeginReveal
        Array.Clear(sil);
        int count = 0;
        for (int y = 0; y < _h; y++)
        {
            int hy = Math.Min(2 * pr - 1, (int)((y + 0.5f) * 2 * pr / _h)); // полупиксель предыдущей сетки
            int cy = hy >> 1, sub = hy & 1;
            for (int x = 0; x < _w; x++)
            {
                int cx = Math.Min(pc - 1, (int)((x + 0.5f) * pc / _w));
                if (HalfLuma(previous[cx, cy], sub) <= SilhouetteLum) continue;
                sil[y * _w + x] = 1;
                count++;
            }
        }

        if (count < 0.01f * n)
        {
            ResetToInitSeed();
            return;
        }

        for (int i = 0; i < n; i++)
        {
            bool s = sil[i] != 0;
            bool live = s || _rng.NextSingle() < DustRate;
            _cur[i] = live ? (byte)1 : (byte)0;
            _age[i] = live ? 1 : 0;
            _ghost[i] = s ? GhostMax : 0f;
        }
    }

    /// <summary>Обычный посев, как в Init.</summary>
    private void ResetToInitSeed()
    {
        Seed(_cur, _rng);
        for (int i = 0; i < _cur.Length; i++)
        {
            _age[i] = _cur[i] != 0 ? 1 : 0;
            _ghost[i] = 0f;
        }
    }

    /// <summary>Яркость ячейки для эстафеты: max(Fg) у непробельного символа, иначе max(Bg), 0..1.</summary>
    private static float HandoffLuma(Cell c)
    {
        var k = c.Ch != ' ' ? c.Fg : c.Bg;
        return MathF.Max(k.R, MathF.Max(k.G, k.B)) / 255f;
    }

    /// <summary>Яркость полупикселя (sub 0 — верх, 1 — низ) для эстафеты.</summary>
    private static float HalfLuma(Cell c, int sub)
    {
        if (c.Ch != '▀') return HandoffLuma(c);
        var k = sub == 0 ? c.Fg : c.Bg;
        return MathF.Max(k.R, MathF.Max(k.G, k.B)) / 255f;
    }

    private void RunStep(float dt)
    {
        if (_gliderPending)
        {
            _gliderPending = false;
            PlaceGlider();
        }
        if (_burstPending)
        {
            _burstPending = false;
            Burst();
        }

        _roundTime += dt;
        _acc += dt * _gensPerSec;
        int steps = 0;
        while (_acc >= 1f && _phase == Phase.Running && steps < MaxStepsPerUpdate)
        {
            _acc -= 1f;
            steps++;
            Generation();
        }
        if (_acc >= 1f) _acc = 0f;

        if (_phase == Phase.Running && _roundTime > MaxRoundSeconds) BeginRestart();
    }

    private void Generation()
    {
        Step(_cur, _next, _w, _h);

        int n = _cur.Length;
        int pop = 0;
        for (int i = 0; i < n; i++)
        {
            byte c = _cur[i], nx = _next[i];
            if (nx != 0)
            {
                pop++;
                if (c != 0)
                {
                    if (_age[i] < AgeCap) _age[i]++;
                }
                else
                {
                    _age[i] = 1;
                    _ghost[i] = 0f;
                }
            }
            else if (c != 0)
            {
                _age[i] = 0;
                _ghost[i] = GhostMax;
            }
        }
        (_cur, _next) = (_next, _cur);

        // FNV-1a по байтам поля.
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < n; i++)
        {
            h ^= (ulong)_cur[i];
            h *= 1099511628211UL;
        }

        // Застой 1: совпал с одним из последних 64 поколений (натюрморт или осциллятор периода <= 64).
        bool stagnant = pop == 0;
        for (int k = 0; k < _histCount && !stagnant; k++)
            if (_hist[k] == h) stagnant = true;
        _hist[_histPos] = h;
        _histPos = (_histPos + 1) % HistoryLen;
        if (_histCount < HistoryLen) _histCount++;

        // Застой 2: популяция изменилась меньше чем на 1% за последние 200 поколений.
        _pop[_popPos] = pop;
        _popPos = (_popPos + 1) % PopWindow;
        if (_popCount < PopWindow) _popCount++;
        if (!stagnant && _popCount == PopWindow)
        {
            int mn = int.MaxValue, mx = 0;
            for (int k = 0; k < PopWindow; k++)
            {
                int v = _pop[k];
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            if (mx - mn < 0.01f * mx) stagnant = true;
        }

        if (stagnant) BeginRestart();
    }

    private void BeginRestart()
    {
        Restarts++;
        _phase = Phase.Fading;
        _phaseLeft = FadeDuration;
        _acc = 0f;
    }

    /// <summary>Старое поле гаснет: каждая живая клетка умирает со своим шансом, так что к концу все мертвы.</summary>
    private void FadeStep(float dt)
    {
        float rem = _phaseLeft;
        _phaseLeft -= dt;
        float p = rem <= dt ? 1f : dt / rem;
        int n = _cur.Length;
        for (int i = 0; i < n; i++)
        {
            if (_cur[i] != 0 && _rng.NextSingle() < p) Kill(i);
        }

        if (_phaseLeft <= 0f) BeginReveal();
    }

    private void BeginReveal()
    {
        for (int i = 0; i < _cur.Length; i++)
            if (_cur[i] != 0) Kill(i);

        Seed(_seed, _rng);
        int n = 0;
        for (int i = 0; i < _seed.Length; i++)
            if (_seed[i] != 0) _order[n++] = i;
        for (int i = n - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (_order[i], _order[j]) = (_order[j], _order[i]);
        }

        _revealTotal = n;
        _revealPos = 0;
        _phase = Phase.Revealing;
        _phaseLeft = RevealDuration;
    }

    /// <summary>Новая расстановка проявляется в случайном порядке за RevealDuration.</summary>
    private void RevealStep(float dt)
    {
        _phaseLeft -= dt;
        float progress = _phaseLeft <= 0f ? 1f : 1f - _phaseLeft / RevealDuration;
        int target = (int)(progress * _revealTotal);
        while (_revealPos < target) Born(_order[_revealPos++]);

        if (_phaseLeft <= 0f)
        {
            _phase = Phase.Running;
            _acc = 0f;
            _roundTime = 0f;
            ResetDetectors();
        }
    }

    private void PlaceGlider()
    {
        int px = _rng.Next(_w), py = _rng.Next(_h);
        bool flip = _rng.Next(2) == 1;
        int rot = _rng.Next(4);
        foreach (var (x0, y0) in Glider)
        {
            int x = flip ? 2 - x0 : x0, y = y0;
            for (int r = 0; r < rot; r++)
            {
                int nx = y, ny = 2 - x;
                x = nx;
                y = ny;
            }
            int i = ((py + y) % _h) * _w + (px + x) % _w;
            if (_cur[i] == 0) Born(i);
        }
    }

    /// <summary>
    /// Взрыв на ударе: кольцо живых клеток радиусом 4–7 (толщина 1–2) в случайной точке, часть клеток пропущена (rng).
    /// Дальше кольцо разлетается обычной эволюцией.
    /// </summary>
    private void Burst()
    {
        int cx = _rng.Next(_w), cy = _rng.Next(_h);
        int r = BurstRadiusMin + _rng.Next(BurstRadiusRange);
        int t = 1 + _rng.Next(BurstThicknessMax);
        int reach = r + t;
        int births = 0;
        for (int dy = -reach; dy <= reach; dy++)
            for (int dx = -reach; dx <= reach; dx++)
            {
                float d = MathF.Sqrt(dx * dx + dy * dy);
                if (d < r || d >= r + t) continue;
                if (_rng.NextSingle() >= BurstFill) continue;
                int x = ((cx + dx) % _w + _w) % _w, y = ((cy + dy) % _h + _h) % _h;
                int i = y * _w + x;
                if (_cur[i] == 0)
                {
                    Born(i);
                    births++;
                }
            }
        LastBurstBirths = births;
    }

    private void ResetDetectors()
    {
        _histCount = 0;
        _histPos = 0;
        _popCount = 0;
        _popPos = 0;
    }

    private void Born(int i)
    {
        _cur[i] = 1;
        _age[i] = 1;
        _ghost[i] = 0f;
    }

    private void Kill(int i)
    {
        _cur[i] = 0;
        _age[i] = 0;
        _ghost[i] = GhostMax;
    }

    /// <summary>Заполняет буфер стартовой расстановкой: композиция фигур (30%) или random soup в центре (70%).</summary>
    private void Seed(byte[] buf, Random rng)
    {
        Array.Clear(buf);
        if (rng.NextSingle() < 0.3f)
        {
            int count = 2 + rng.Next(4);
            for (int k = 0; k < count; k++)
            {
                var fig = Figures[rng.Next(Figures.Length)];
                Bounds(fig, out int fw, out int fh);
                if (fw > _w || fh > _h) continue;
                int x0 = rng.Next(_w - fw + 1), y0 = rng.Next(_h - fh + 1);
                foreach (var (x, y) in fig) buf[(y0 + y) * _w + x0 + x] = 1;
            }
        }
        else
        {
            int cw = Math.Max(1, (int)(_w * 0.6f)), ch = Math.Max(1, (int)(_h * 0.6f));
            int x0 = (_w - cw) / 2, y0 = (_h - ch) / 2;
            for (int y = y0; y < y0 + ch; y++)
                for (int x = x0; x < x0 + cw; x++)
                    buf[y * _w + x] = rng.NextSingle() < 0.3f ? (byte)1 : (byte)0;
        }
    }

    private static void Bounds((int X, int Y)[] pts, out int w, out int h)
    {
        w = 0;
        h = 0;
        foreach (var (x, y) in pts)
        {
            if (x + 1 > w) w = x + 1;
            if (y + 1 > h) h = y + 1;
        }
    }

    private Rgba ColorAt(int i)
    {
        if (_cur[i] != 0)
        {
            int a = _age[i];
            if (a <= 1) return _newborn;
            if (a > OldAge) return _old;
            return _base;
        }
        float g = _ghost[i];
        return g > 0f ? _base.Scale(g) : Rgba.Black;
    }

    private static Rgba BaseColor(string? palette) =>
        (palette ?? string.Empty).ToLowerInvariant() switch
        {
            "green" or "amber" or "mono" or "rainbow" => Palettes.Base(palette),
            _ => Rgba.Hex(0x3FA7FF), // "default" и неизвестные имена
        };
}
