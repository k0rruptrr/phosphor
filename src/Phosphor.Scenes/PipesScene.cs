using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>Трубы как в Windows 95, но из двойной псевдографики.</summary>
public sealed class PipesScene : IScene, IAudioReactive, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec PipesSpec = new("pipes", SceneParamKind.Int, 4f, 1f, MaxPipes, "число труб");
    public static readonly SceneParamSpec StepsPerSecSpec = new("stepsPerSec", SceneParamKind.Float, 40f, 0f, 200f, "скорость роста труб, шагов/с");
    public static readonly SceneParamSpec TurnChanceSpec = new("turnChance", SceneParamKind.Float, 0.15f, 0f, 1f, "частота поворотов труб");
    public static readonly SceneParamSpec FillLimitSpec = new("fillLimit", SceneParamKind.Float, 0.55f, 0f, 1f, "заполнение экрана до сброса");
    private static readonly SceneParamSpec[] ParamList = { PipesSpec, StepsPerSecSpec, TurnChanceSpec, FillLimitSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    /// <summary>Сторона ячейки. Порядок по часовой стрелке: N, E, S, W.</summary>
    public enum Side { N, E, S, W }

    private const char Empty = ' ';
    private const char HeadGlyph = '■';
    private const char CrossGlyph = '╬';
    private const int MaxPipes = 64;
    private const int SpawnTries = 64;
    private const float RoundSeconds = 60f;
    private const float ClearSeconds = 1.5f;
    private const float FreshSeconds = 0.5f;
    private const float FlashSeconds = 0.2f;
    private const float MaxRate = 1000f;

    private enum Phase { Running, Clearing }

    private struct Pipe
    {
        public int X, Y;
        public Side Dir, Entry;
        public bool Alive, ForceTurn;
        public Rgba Color, HeadColor;
    }

    public string Id => "pipes";

    private int _cols, _rows;
    private Random _rng = new(0);
    private float _speedScale = 1f;
    private float _stepsPerSec = 40f, _turnChance = 0.15f, _fillLimit = 0.55f;
    private bool _mono;
    private Rgba _monoBase;
    private float _audioMul = 1f;
    private float _flashLeft;
    private float _roundTime, _acc, _clearT;
    private double _clock;
    private int _filled;
    private Phase _phase;
    private Pipe[] _pipes = Array.Empty<Pipe>();
    private char[] _ch = Array.Empty<char>();
    private Rgba[] _col = Array.Empty<Rgba>();
    private double[] _time = Array.Empty<double>();

    /// <summary>Проверка: сторона a и сторона b соединены в одной ячейке — какой символ рисовать.</summary>
    public static char Connect(Side a, Side b)
    {
        int m = (1 << (int)a) | (1 << (int)b);
        return m switch
        {
            0b1010 => '═', // W+E
            0b0101 => '║', // N+S
            0b0110 => '╔', // E+S
            0b1100 => '╗', // W+S
            0b0011 => '╚', // E+N
            0b1001 => '╝', // W+N
            _ => throw new ArgumentException("Стороны должны быть разными"),
        };
    }

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = cols;
        _rows = rows;
        _rng = rng;
        _speedScale = MathF.Max(0f, options.Speed);
        int n = Math.Clamp(options.GetInt(PipesSpec.Name, (int)PipesSpec.Default), (int)PipesSpec.Min, (int)PipesSpec.Max);
        _stepsPerSec = Math.Clamp(options.GetFloat(StepsPerSecSpec.Name, StepsPerSecSpec.Default), StepsPerSecSpec.Min, StepsPerSecSpec.Max);
        _turnChance = Math.Clamp(options.GetFloat(TurnChanceSpec.Name, TurnChanceSpec.Default), TurnChanceSpec.Min, TurnChanceSpec.Max);
        _fillLimit = Math.Clamp(options.GetFloat(FillLimitSpec.Name, FillLimitSpec.Default), FillLimitSpec.Min, FillLimitSpec.Max);
        _mono = IsMono(options.Palette);
        _monoBase = Palettes.Base(options.Palette);

        _ch = new char[cols * rows];
        _col = new Rgba[cols * rows];
        _time = new double[cols * rows];
        _pipes = new Pipe[n];

        _clock = 0;
        _acc = 0;
        _audioMul = 1f;
        _flashLeft = 0f;
        StartRound();
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;

        _clock += dt;
        if (_flashLeft > 0f) _flashLeft = MathF.Max(0f, _flashLeft - dt);

        if (_phase == Phase.Clearing)
        {
            _clearT += dt;
            if (_clearT >= ClearSeconds) StartRound();
            return;
        }

        _roundTime += dt;
        float rate = MathF.Min(_stepsPerSec * _speedScale * _audioMul, MaxRate);
        _acc += rate * dt;
        while (_acc >= 1f)
        {
            _acc -= 1f;
            StepAll();
            if (IsRoundOver())
            {
                StartClearing();
                return;
            }
        }
        if (IsRoundOver()) StartClearing();
    }

    public void Draw(CharGrid grid)
    {
        grid.Clear(Rgba.Black);

        float k = 1f;
        if (_phase == Phase.Clearing) k *= MathF.Max(0f, 1f - _clearT / ClearSeconds);
        if (_flashLeft > 0f) k *= 1f + 0.6f * (_flashLeft / FlashSeconds);
        if (k <= 0f) return;

        for (int y = 0; y < _rows; y++)
        {
            for (int x = 0; x < _cols; x++)
            {
                int i = y * _cols + x;
                char ch = _ch[i];
                if (ch == Empty) continue;
                // Недавно нарисованные участки чуть ярче старых.
                float v = (_clock - _time[i]) < FreshSeconds ? 1f : 0.8f;
                grid.Put(x, y, ch, _col[i].Scale(v * k));
            }
        }
    }

    public void OnAudio(in AudioFrame audio)
    {
        if (!audio.Active)
        {
            _audioMul = 1f;
            return;
        }
        _audioMul = 1f + 1.5f * audio.Bass;
        if (!audio.Beat) return;

        for (int i = 0; i < _pipes.Length; i++)
            if (_pipes[i].Alive) _pipes[i].ForceTurn = true;
        _flashLeft = FlashSeconds;
    }

    private static bool IsMono(string? name) =>
        name?.ToLowerInvariant() is "green" or "amber" or "mono";

    private void StartClearing()
    {
        _phase = Phase.Clearing;
        _clearT = 0f;
        _acc = 0f;
    }

    private bool IsRoundOver() =>
        _filled > _fillLimit * _ch.Length || _roundTime >= RoundSeconds;

    private void StartRound()
    {
        Array.Fill(_ch, Empty);
        Array.Fill(_col, Rgba.Black);
        Array.Fill(_time, 0.0);
        _filled = 0;
        _roundTime = 0f;
        _clearT = 0f;
        _acc = 0f;
        _flashLeft = 0f;
        _phase = Phase.Running;

        float hueOffset = _rng.NextSingle() * 360f;
        int n = _pipes.Length;
        for (int i = 0; i < n; i++)
        {
            Rgba c = _mono
                ? _monoBase.Scale(0.45f + 0.55f * (i + 1f) / n)
                : Rgba.Hsv(hueOffset + i * 360f / n, 0.7f, 1f);
            _pipes[i] = new Pipe
            {
                Color = c,
                HeadColor = Rgba.Lerp(c, Rgba.White, 0.5f),
            };
        }
        for (int i = 0; i < n; i++) TrySpawn(i);
    }

    private void StepAll()
    {
        for (int i = 0; i < _pipes.Length; i++)
            if (_pipes[i].Alive) StepPipe(i);
        for (int i = 0; i < _pipes.Length; i++)
            if (!_pipes[i].Alive) TrySpawn(i);
    }

    private void StepPipe(int i)
    {
        ref Pipe p = ref _pipes[i];
        Side d = p.Dir;

        bool turn = p.ForceTurn || _rng.NextSingle() < _turnChance;
        p.ForceTurn = false;
        if (turn)
        {
            Side left = Left(d), right = Right(d);
            Side s = _rng.Next(2) == 0 ? left : right;
            Side other = s == left ? right : left;
            if (InBounds(p.X + Dx(s), p.Y + Dy(s))) d = s;
            else if (InBounds(p.X + Dx(other), p.Y + Dy(other))) d = other;
        }

        if (TryMove(i, d, allowExit: true)) return;

        // Впереди занято: пробуем свернуть в свободную сторону.
        Side a = Left(d), b = Right(d);
        if (_rng.Next(2) == 0) (a, b) = (b, a);
        if (TryMove(i, a, allowExit: false) || TryMove(i, b, allowExit: false)) return;

        // Тупик: труба умирает, последняя ячейка остаётся стыком к занятой соседней.
        Paint(p.X, p.Y, Connect(p.Entry, d), p.Color);
        p.Alive = false;
    }

    /// <summary>Пытается шагнуть головы в сторону d. Возвращает false, если путь занят.</summary>
    private bool TryMove(int i, Side d, bool allowExit)
    {
        ref Pipe p = ref _pipes[i];
        if (d == p.Entry) return false;

        int nx = p.X + Dx(d), ny = p.Y + Dy(d);
        if (!InBounds(nx, ny))
        {
            if (!allowExit) return false;
            // Вышла за экран: труба умирает.
            Paint(p.X, p.Y, Connect(p.Entry, d), p.Color);
            p.Alive = false;
            return true;
        }

        char nc = _ch[ny * _cols + nx];
        if (nc == Empty)
        {
            Paint(p.X, p.Y, Connect(p.Entry, d), p.Color);
            p.X = nx; p.Y = ny; p.Dir = d; p.Entry = Opp(d);
            Paint(nx, ny, HeadGlyph, p.HeadColor);
            return true;
        }

        // Пересечение: прямой перпендикулярный участок и свободная ячейка за ним.
        bool horizontal = d == Side.E || d == Side.W;
        bool straightCross = horizontal ? nc == '║' : nc == '═';
        if (!straightCross) return false;
        int bx = nx + Dx(d), by = ny + Dy(d);
        if (!InBounds(bx, by) || _ch[by * _cols + bx] != Empty) return false;

        Paint(p.X, p.Y, Connect(p.Entry, d), p.Color);
        Paint(nx, ny, CrossGlyph, p.Color);
        p.X = bx; p.Y = by; p.Dir = d; p.Entry = Opp(d);
        Paint(bx, by, HeadGlyph, p.HeadColor);
        return true;
    }

    private bool TrySpawn(int i)
    {
        for (int t = 0; t < SpawnTries; t++)
        {
            int x, y;
            Side dir;
            switch (_rng.Next(4))
            {
                case 0: x = _rng.Next(_cols); y = 0; dir = Side.S; break;
                case 1: x = _cols - 1; y = _rng.Next(_rows); dir = Side.W; break;
                case 2: x = _rng.Next(_cols); y = _rows - 1; dir = Side.N; break;
                default: x = 0; y = _rng.Next(_rows); dir = Side.E; break;
            }
            if (_ch[y * _cols + x] != Empty) continue;

            ref Pipe p = ref _pipes[i];
            p.X = x; p.Y = y;
            p.Dir = dir; p.Entry = Opp(dir);
            p.Alive = true; p.ForceTurn = false;
            Paint(x, y, HeadGlyph, p.HeadColor);
            return true;
        }
        return false;
    }

    private void Paint(int x, int y, char ch, Rgba color)
    {
        int i = y * _cols + x;
        if (_ch[i] == Empty) _filled++;
        _ch[i] = ch;
        _col[i] = color;
        _time[i] = _clock;
    }

    private bool InBounds(int x, int y) => (uint)x < (uint)_cols && (uint)y < (uint)_rows;

    private static int Dx(Side s) => s switch { Side.E => 1, Side.W => -1, _ => 0 };
    private static int Dy(Side s) => s switch { Side.S => 1, Side.N => -1, _ => 0 };
    private static Side Right(Side s) => (Side)(((int)s + 1) & 3);
    private static Side Left(Side s) => (Side)(((int)s + 3) & 3);
    private static Side Opp(Side s) => (Side)(((int)s + 2) & 3);
}
