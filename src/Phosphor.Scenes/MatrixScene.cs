using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>Классический цифровой дождь «Матрицы».</summary>
public sealed class MatrixScene : IScene, IAudioReactive, IHandoffReceiver, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec MinSpeedSpec = new("minSpeed", SceneParamKind.Float, 6f, 0f, 60f, "скорость падения: минимум");
    public static readonly SceneParamSpec MaxSpeedSpec = new("maxSpeed", SceneParamKind.Float, 22f, 0f, 60f, "скорость падения: максимум");
    public static readonly SceneParamSpec DensitySpec = new("density", SceneParamKind.Float, 0.6f, 0f, 1f, "плотность дождя");
    public static readonly SceneParamSpec MutateRateSpec = new("mutateRate", SceneParamKind.Float, 1.5f, 0f, 10f, "частота смены символов");
    private static readonly SceneParamSpec[] ParamList = { MinSpeedSpec, MaxSpeedSpec, DensitySpec, MutateRateSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    private const int StreamsPerColumn = 2;
    private const int MinLen = 6;
    private const float GlitchDuration = 0.2f;
    private const float KickSeconds = 0.15f; // удар: скорость падения ×2 на 150 мс

    /// <summary>Пул символов. Все символы входят в GlyphSet.All.</summary>
    private static readonly string Pool = GlyphSet.Katakana + "0123456789" + "Z:.\"=*+-<>|";

    public string Id => "matrix";

    private int _cols, _rows;
    private Random _rng = new(0);
    private float _speedScale = 1f;
    private float _minSpeed = 6f, _maxSpeed = 22f;
    private float _density = 0.6f;
    private float _mutateRate = 1.5f;
    private float _audioMul = 1f;
    private float _kickLeft;
    private float _glitchTimer = 5f, _glitchLeft;
    private int _glitchRow, _glitchHeight = 1;

    // Струи: слот = col * StreamsPerColumn + k. Len == 0 — слот свободен.
    private float[] _head = Array.Empty<float>();
    private float[] _speed = Array.Empty<float>();
    private int[] _len = Array.Empty<int>();
    private float[] _pause = Array.Empty<float>();   // по колонкам: сколько ждать до попытки родить струю
    private char[] _chars = Array.Empty<char>();     // по ячейкам: символ каждой ячейки хвоста
    private Rgba[] _colBase = Array.Empty<Rgba>();
    private Rgba[] _colHead = Array.Empty<Rgba>();

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = cols;
        _rows = rows;
        _rng = rng;
        _speedScale = MathF.Max(0f, options.Speed);
        _minSpeed = Math.Clamp(options.GetFloat(MinSpeedSpec.Name, MinSpeedSpec.Default), MinSpeedSpec.Min, MinSpeedSpec.Max);
        _maxSpeed = MathF.Max(_minSpeed, Math.Clamp(options.GetFloat(MaxSpeedSpec.Name, MaxSpeedSpec.Default), MaxSpeedSpec.Min, MaxSpeedSpec.Max));
        _density = Math.Clamp(options.GetFloat(DensitySpec.Name, DensitySpec.Default), DensitySpec.Min, DensitySpec.Max);
        _mutateRate = Math.Clamp(options.GetFloat(MutateRateSpec.Name, MutateRateSpec.Default), MutateRateSpec.Min, MutateRateSpec.Max);
        _audioMul = 1f;
        _kickLeft = 0f;
        _glitchTimer = 5f + rng.NextSingle() * 10f;
        _glitchLeft = 0f;
        _glitchRow = 0;
        _glitchHeight = 1;

        int slots = cols * StreamsPerColumn;
        _head = new float[slots];
        _speed = new float[slots];
        _len = new int[slots];
        _pause = new float[cols];
        _chars = new char[cols * rows];
        _colBase = new Rgba[cols];
        _colHead = new Rgba[cols];
        for (int c = 0; c < cols; c++)
        {
            var b = Palettes.ColumnBase(options.Palette, c, cols);
            _colBase[c] = b;
            _colHead[c] = Palettes.Head(options.Palette, b);
        }

        // Стартовое состояние: часть колонок уже с струёй на разной высоте, экран не пустой.
        for (int c = 0; c < cols; c++)
        {
            if (_rng.NextSingle() < _density) Spawn(c, c * StreamsPerColumn, _rng.NextSingle() * rows);
            else _pause[c] = _rng.NextSingle() * 2f;
        }
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;

        if (_kickLeft > 0f) _kickLeft -= dt;

        _glitchTimer -= dt;
        if (_glitchTimer <= 0f)
        {
            _glitchLeft = GlitchDuration;
            _glitchRow = _rng.Next(_rows);
            _glitchHeight = 1 + _rng.Next(2);
            _glitchTimer = 5f + _rng.NextSingle() * 10f;
        }
        else if (_glitchLeft > 0f)
        {
            _glitchLeft -= dt;
        }

        for (int col = 0; col < _cols; col++)
        {
            int s0 = col * StreamsPerColumn, s1 = s0 + 1;
            if (_len[s0] != 0) StepStream(col, s0, dt);
            if (_len[s1] != 0) StepStream(col, s1, dt);

            bool a0 = _len[s0] != 0, a1 = _len[s1] != 0;
            if (!a0 && !a1)
            {
                _pause[col] -= dt;
                if (_pause[col] <= 0f)
                {
                    if (_rng.NextSingle() < _density) Spawn(col, s0, -1f);
                    else _pause[col] = _rng.NextSingle() * 2f;
                }
            }
            else if (a0 != a1)
            {
                // Вторая струя — когда первая ушла достаточно далеко вниз.
                int first = a0 ? s0 : s1, free = a0 ? s1 : s0;
                if (_head[first] >= _rows * 0.5f && _rng.NextSingle() < _density * dt * 2f)
                    Spawn(col, free, -1f);
            }
        }
    }

    public void Draw(CharGrid grid)
    {
        grid.Clear(Rgba.Black);
        for (int col = 0; col < _cols; col++)
        {
            for (int k = 0; k < StreamsPerColumn; k++)
            {
                int s = col * StreamsPerColumn + k;
                int len = _len[s];
                if (len == 0) continue;

                int hr = (int)MathF.Floor(_head[s]);
                int top = Math.Max(0, hr - len + 1), bottom = Math.Min(_rows - 1, hr);
                for (int r = top; r <= bottom; r++)
                {
                    int d = hr - r;
                    Rgba fg;
                    if (d == 0)
                    {
                        fg = _colHead[col];
                    }
                    else
                    {
                        float t = 1f - d / (float)len;
                        fg = _colBase[col].Scale(0.08f + 0.92f * t * t);
                    }
                    if (_glitchLeft > 0f && r >= _glitchRow && r < _glitchRow + _glitchHeight)
                        fg = Rgba.Lerp(fg, Rgba.White, 0.5f);
                    grid.Put(col, r, _chars[r * _cols + col], fg);
                }
            }
        }
    }

    public void OnAudio(in AudioFrame audio)
    {
        if (!audio.Active)
        {
            _audioMul = 1f;
            _kickLeft = 0f;
            return;
        }
        _audioMul = 1f + 1.5f * audio.Bass;
        if (!audio.Beat || _cols == 0) return;

        _kickLeft = KickSeconds;

        // В 3-6 случайных пустых колонках сразу рождаются струи.
        int want = 3 + _rng.Next(4), done = 0;
        for (int t = 0; t < _cols * 4 && done < want; t++)
        {
            int col = _rng.Next(_cols);
            int s0 = col * StreamsPerColumn;
            if (_len[s0] == 0 && _len[s0 + 1] == 0)
            {
                Spawn(col, s0, -1f);
                done++;
            }
        }
    }

    /// <summary>
    /// Эстафета: в колонках, где в previous есть ячейка ярче 0.5, сразу рождается струя с головой на строке
    /// самой яркой ячейки (масштабированной). Остальные колонки — как после Init.
    /// </summary>
    public void Seed(CharGrid previous)
    {
        for (int col = 0; col < _cols; col++)
        {
            int pc = Math.Min(previous.Cols - 1, (int)((col + 0.5f) * previous.Cols / _cols));
            float best = 0.5f;
            int bestRow = -1;
            for (int pr = 0; pr < previous.Rows; pr++)
            {
                float l = HandoffLuma(previous[pc, pr]);
                if (l > best) { best = l; bestRow = pr; }
            }
            if (bestRow < 0) continue;

            int row = Math.Min(_rows - 1, (int)((bestRow + 0.5f) * _rows / previous.Rows));
            Spawn(col, col * StreamsPerColumn, row);
        }
    }

    /// <summary>Яркость ячейки для эстафеты: max(Fg) у непробельного символа, иначе max(Bg), 0..1.</summary>
    private static float HandoffLuma(Cell c)
    {
        var k = c.Ch != ' ' ? c.Fg : c.Bg;
        return MathF.Max(k.R, MathF.Max(k.G, k.B)) / 255f;
    }

    private void Spawn(int col, int slot, float head)
    {
        int maxLen = Math.Max(MinLen, (int)(_rows * 0.8f));
        int len = MinLen + _rng.Next(maxLen - MinLen + 1);
        _len[slot] = len;
        _head[slot] = head;
        _speed[slot] = (_minSpeed + _rng.NextSingle() * (_maxSpeed - _minSpeed)) * _speedScale;

        // Уже видимая часть струи получает свои символы.
        int hr = (int)MathF.Floor(head);
        int top = Math.Max(0, hr - len + 1), bottom = Math.Min(_rows - 1, hr);
        for (int r = top; r <= bottom; r++) _chars[r * _cols + col] = RandomGlyph();
    }

    private void StepStream(int col, int slot, float dt)
    {
        float oldH = _head[slot];
        float kick = _kickLeft > 0f ? 2f : 1f;
        float newH = oldH + _speed[slot] * dt * _audioMul * kick;
        int len = _len[slot];

        if (newH - len + 1 >= _rows)
        {
            // Хвост целиком ушёл за низ: струя умирает.
            _len[slot] = 0;
            _pause[col] = _rng.NextSingle() * 2f;
            return;
        }

        // Голова вошла в новые строки: каждая новая строка получает свежий символ.
        int oldRow = (int)MathF.Floor(oldH), newRow = (int)MathF.Floor(newH);
        int from = Math.Max(oldRow + 1, 0), to = Math.Min(newRow, _rows - 1);
        for (int r = from; r <= to; r++) _chars[r * _cols + col] = RandomGlyph();
        _head[slot] = newH;

        // Случайные смены символов в хвосте: mutateRate раз в секунду на ячейку.
        float p = _mutateRate * dt;
        int top = Math.Max(0, newRow - len + 1), bottom = Math.Min(_rows - 1, newRow);
        for (int r = top; r <= bottom; r++)
            if (_rng.NextSingle() < p) _chars[r * _cols + col] = RandomGlyph();
    }

    private char RandomGlyph() => Pool[_rng.Next(Pool.Length)];
}
