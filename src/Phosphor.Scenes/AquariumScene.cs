using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// Аквариум: тёплая вода с лучами света, водоросли на дне, рыбы, пузыри и редкая акула.
/// Вся случайность — из rng в Init и Update. Draw только читает состояние.
/// </summary>
public sealed class AquariumScene : IScene, IAudioReactive, ISceneParams
{
    // Параметры для окна настроек. Рыб: множитель от автоматического количества (авто = max(3, площадь/900)).
    public static readonly SceneParamSpec FishDensitySpec = new("fishDensity", SceneParamKind.Float, 1f, 0.25f, 4f, "рыбы: × от авто-количества");
    private static readonly SceneParamSpec[] ParamList = { FishDensitySpec };

    /// <summary>Старый абсолютный ключ "fish": если задан в конфиге, имеет приоритет над множителем. В Params не показывается.</summary>
    private const string LegacyFishKey = "fish";

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    private const int MaxBubbles = 128;
    private const int LightLevels = 16;   // уровни подсветки лучей; цвета фона по уровням считаются в Init
    private const float SharkSpeed = 10f;
    private const float DartSeconds = 0.2f;     // рывок рыб после удара
    private const float DartSpeedMul = 3f;
    private const float RushMul = 4f;          // удар: лучи и покачивание ускоряются ×5 и гаснут за RushTau
    private const float RushTau = 0.1f;

    private static readonly Rgba WaterTop = Rgba.Hex(0x0A2A4A);
    private static readonly Rgba WaterBottom = Rgba.Hex(0x02101E);
    private static readonly Rgba SurfaceLo = Rgba.Hex(0x5CC4F0);
    private static readonly Rgba SurfaceHi = Rgba.Hex(0x9FDFFF);
    private static readonly Rgba BubbleColor = Rgba.Hex(0xBFEFFF);
    private static readonly Rgba SharkColor = Rgba.Hex(0x6F8FAF);
    private static readonly Rgba[] SandFg = { Rgba.Hex(0xD8B77A), Rgba.Hex(0xC29B5E), Rgba.Hex(0xE8CF98) };
    private static readonly Rgba[] SandBg = { Rgba.Hex(0x5A4426), Rgba.Hex(0x46341E) };
    private static readonly Rgba[] KelpColors =
    {
        Rgba.Hex(0x2E8B57), Rgba.Hex(0x3CB371), Rgba.Hex(0x1F7A4D), Rgba.Hex(0x58C47A),
    };
    private static readonly Rgba[] FishColors =
    {
        Rgba.Hex(0xFF8C1A), // оранжевый
        Rgba.Hex(0xFFE14D), // жёлтый
        Rgba.Hex(0x2EE6D6), // бирюзовый
        Rgba.Hex(0xFF6FB5), // розовый
        Rgba.Hex(0xA6FF3B), // лаймовый
    };

    /// <summary>Спрайты рыб, смотрящих вправо. Смотрящие влево — Mirror от них.</summary>
    private static readonly string[] FishRight = { "><>", "><(((°>", "><((('>", ">=>", "><_>", "><{{{°>" };
    private static readonly string[] FishLeft = Array.ConvertAll(FishRight, s => Mirror(s));

    private static readonly string[] SharkRight = Pad(new[]
    {
        "         __",
        "        / /",
        "  _____/ /___",
        " /  o        \\==",
        "<  ____      ==>",
        " \\/    \\____/==",
    });
    private static readonly string[] SharkLeft = Array.ConvertAll(SharkRight, s => Mirror(s));
    private static readonly int SharkWidth = SharkRight[0].Length;
    private static readonly int SharkHeight = SharkRight.Length;

    public string Id => "aquarium";

    private Random _rng = new(0);
    private int _cols, _rows;
    private int _sandTop;       // первая строка песка; строки [1, _sandTop) — вода
    private int _waterMax;      // последняя водная строка (может быть 0 на маленьких сетках)
    private float _t;
    private float _rush;        // ускорение времени после удара, 0 — обычный ход
    private float _speed = 1f;
    private float _bass;        // 0 без музыки
    private bool _beatPending;
    private bool _terminal;     // палитра green/amber/mono
    private Rgba _termBase;

    private Rgba[] _rowBg = Array.Empty<Rgba>();
    private Rgba[] _bgLevels = Array.Empty<Rgba>();   // [строка * (LightLevels+1) + уровень], уже с палитрой
    private Ray[] _rays = Array.Empty<Ray>();
    private Stalk[] _stalks = Array.Empty<Stalk>();
    private Fish[] _fish = Array.Empty<Fish>();
    private Bubble[] _bubbles = Array.Empty<Bubble>();
    private int _bubbleCount;

    private bool _sharkOn;
    private float _sharkTimer;
    private float _sharkX;
    private int _sharkY, _sharkDir = 1;

    /// <summary>Сколько пузырей сейчас живёт (для тестов и отладки).</summary>
    public int BubbleCount => _bubbleCount;

    /// <summary>Средняя строка живых пузырей; NaN, если пузырей нет (для тестов).</summary>
    public float AverageBubbleY()
    {
        if (_bubbleCount == 0) return float.NaN;
        float sum = 0f;
        int n = 0;
        foreach (var b in _bubbles)
        {
            if (!b.Alive) continue;
            sum += b.Y;
            n++;
        }
        return sum / n;
    }

    /// <summary>Позиции рыб (левый край спрайта и базовая строка) — для тестов.</summary>
    public IEnumerable<(float x, float y)> FishPositions()
    {
        foreach (var f in _fish) yield return (f.X, f.Y);
    }

    /// <summary>Зеркалит спрайт: переворот строки и замена парных символов.</summary>
    public static string Mirror(string s)
    {
        var chars = new char[s.Length];
        for (int i = 0; i < s.Length; i++) chars[s.Length - 1 - i] = SwapChar(s[i]);
        return new string(chars);
    }

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = cols;
        _rows = rows;
        _rng = rng;
        _speed = MathF.Max(0f, options.Speed);
        string pal = (options.Palette ?? "default").ToLowerInvariant();
        _terminal = pal is "green" or "amber" or "mono";
        _termBase = Palettes.Base(pal);

        _t = 0f;
        _rush = 0f;
        _bass = 0f;
        _beatPending = false;

        _sandTop = Math.Max(1, rows - 2);
        _waterMax = _sandTop - 1;

        _rowBg = new Rgba[rows];
        for (int y = 0; y < rows; y++)
            _rowBg[y] = Rgba.Lerp(WaterTop, WaterBottom, rows > 1 ? y / (float)(rows - 1) : 0f);

        // Яркость в центре луча: +40–60% к фону. Уровни фона с подсветкой предвычисляются.
        float boost = 0.4f + 0.2f * rng.NextSingle();
        _bgLevels = new Rgba[rows * (LightLevels + 1)];
        for (int y = 0; y < rows; y++)
            for (int level = 0; level <= LightLevels; level++)
            {
                float k = 1f + boost * level / LightLevels;
                _bgLevels[y * (LightLevels + 1) + level] = Tint(_rowBg[y].Scale(k));
            }

        int rayCount = 2 + rng.Next(2);
        _rays = new Ray[rayCount];
        for (int i = 0; i < rayCount; i++)
        {
            float slope = 0.25f + rng.NextSingle() * 0.25f;
            float width = 8f + 12f * rng.NextSingle();   // 8–20 колонок
            _rays[i] = new Ray
            {
                Slope = slope,
                Width = width,
                Half = width * 0.5f,
                InvHalf = 1f / (width * 0.5f),
                Period = cols + rows * slope + width + 1f,
                Speed = (0.6f + rng.NextSingle() * 0.6f) * _speed,
                Phase = rng.NextSingle() * 100f,
            };
        }

        int kelpBase = _sandTop - 1;
        int stalkCount = kelpBase >= 1 ? Math.Clamp(cols / 12, 5, 10) : 0;
        _stalks = new Stalk[stalkCount];
        for (int i = 0; i < stalkCount; i++)
        {
            int x = (int)((i + 0.5f) * cols / stalkCount) + rng.Next(-1, 2);
            _stalks[i] = new Stalk
            {
                X = Math.Clamp(x, 0, cols - 1),
                Base = kelpBase,
                Height = Math.Min(3 + rng.Next(8), kelpBase),
                Phase = rng.NextSingle() * MathF.Tau,
                Color = KelpColors[rng.Next(KelpColors.Length)],
            };
        }

        int fishCount = 0;
        if (_waterMax >= 1)
        {
            int legacy = options.GetInt(LegacyFishKey, -1);
            fishCount = legacy >= 0
                ? Math.Clamp(legacy, 0, 500)
                : Math.Max(1, (int)MathF.Round(Math.Max(3, cols * rows / 900) * Math.Clamp(options.GetFloat(FishDensitySpec.Name, FishDensitySpec.Default), FishDensitySpec.Min, FishDensitySpec.Max)));
        }
        _fish = new Fish[fishCount];
        for (int i = 0; i < fishCount; i++)
        {
            int sprite = rng.Next(FishRight.Length);
            int w = FishRight[sprite].Length;
            _fish[i] = new Fish
            {
                Sprite = sprite,
                Color = FishColors[rng.Next(FishColors.Length)],
                Speed = 2f + rng.NextSingle() * 6f,
                Dir = rng.Next(2) == 0 ? 1 : -1,
                X = rng.NextSingle() * (cols + w) - w,
                Y = 1 + rng.Next(_waterMax),
                Phase = rng.NextSingle() * MathF.Tau,
                TurnTimer = 5f + rng.NextSingle() * 15f,
            };
        }

        _bubbles = new Bubble[MaxBubbles];
        for (int i = 0; i < MaxBubbles; i++) _bubbles[i] = new Bubble();
        _bubbleCount = 0;

        _sharkOn = false;
        _sharkTimer = 40f + rng.NextSingle() * 50f;
        _sharkX = 0f;
        _sharkY = 1;
        _sharkDir = 1;
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;
        _rush *= MathF.Exp(-dt / RushTau);
        _t += dt * (1f + _rush);
        float k = _speed;
        int bottomRow = _sandTop - 1;

        // Редкие пузыри со дна у водорослей.
        if (_stalks.Length > 0 && _rng.NextSingle() < 1.2f * dt)
        {
            var s = _stalks[_rng.Next(_stalks.Length)];
            Spawn(s.X, s.Base);
        }

        // Удар музыки: бурст пузырей со дна и рывок всех рыб (dart).
        if (_beatPending)
        {
            _beatPending = false;
            int n = 16 + _rng.Next(9);
            for (int i = 0; i < n; i++) Spawn(_rng.Next(_cols), bottomRow);
            foreach (var f in _fish) f.Dart = DartSeconds;
        }

        // Рыбы.
        bool shark = _sharkOn;
        float sharkCenter = _sharkX + SharkWidth * 0.5f;
        foreach (var f in _fish)
        {
            int w = FishRight[f.Sprite].Length;
            float cx = f.X + w * 0.5f;
            float sp = f.Speed * k;

            if (!shark)
            {
                f.Panic = 0f;
            }
            else if (f.Panic <= 0f && MathF.Abs(cx - sharkCenter) < 20f)
            {
                // Рыба рядом с акулой начинает бегство на 2–5 с. Бегство не прерывается,
                // когда рыба выходит из 20 колонок: иначе рыбы впереди акулы возвращаются
                // в зону и копятся на её границе, двигаясь со скоростью акулы (колонка).
                f.Panic = 2f + _rng.NextSingle() * 3f;
            }

            if (f.Panic > 0f)
            {
                // Уплывает от акулы вдвое быстрее; направление — от её центра.
                f.Panic -= dt;
                f.Dir = cx >= sharkCenter ? 1 : -1;
                sp *= 2f;
            }
            else
            {
                f.TurnTimer -= dt;
                if (f.TurnTimer <= 0f)
                {
                    f.Dir = -f.Dir;
                    f.TurnTimer = 5f + _rng.NextSingle() * 15f;
                }
            }

            // Рывок после удара: рыба на короткое время плывёт в 3 раза быстрее.
            if (f.Dart > 0f)
            {
                f.Dart -= dt;
                sp *= DartSpeedMul;
            }

            f.X += f.Dir * sp * dt;

            if (_rng.NextSingle() < 0.15f * dt)
                Spawn(f.Dir > 0 ? f.X + w - 1 : f.X, f.Y);

            if (f.Dir > 0 && f.X > _cols) Reenter(f, exitedRight: true);
            else if (f.Dir < 0 && f.X + w < 0) Reenter(f, exitedRight: false);
        }

        // Акула.
        if (_sharkOn)
        {
            _sharkX += _sharkDir * SharkSpeed * k * dt;
            if ((_sharkDir > 0 && _sharkX > _cols) || (_sharkDir < 0 && _sharkX + SharkWidth < 0))
            {
                _sharkOn = false;
                _sharkTimer = 40f + _rng.NextSingle() * 50f;
            }
        }
        else
        {
            _sharkTimer -= dt;
            if (_sharkTimer <= 0f)
            {
                _sharkTimer = 40f + _rng.NextSingle() * 50f;
                int room = _sandTop - SharkHeight; // верхняя строка акулы в 1..room
                if (room >= 1)
                {
                    _sharkOn = true;
                    _sharkY = 1 + _rng.Next(room);
                    _sharkDir = _rng.Next(2) == 0 ? 1 : -1;
                    _sharkX = _sharkDir > 0 ? -SharkWidth : _cols;
                }
            }
        }

        // Пузыри поднимаются и лопаются у поверхности.
        foreach (var b in _bubbles)
        {
            if (!b.Alive) continue;
            b.Y -= b.Speed * k * dt;
            if (b.Y < 1f)
            {
                b.Alive = false;
                _bubbleCount--;
            }
        }
    }

    public void Draw(CharGrid grid)
    {
        int cols = _cols;

        // Вода: вертикальный градиент и лучи света (уровни фона предвычислены в Init).
        Cell[] cells = grid.Cells;
        int rayCount = _rays.Length;
        Span<float> m0 = stackalloc float[rayCount];
        for (int y = 0; y < _sandTop; y++)
        {
            // Положение каждого луча в начале строки; дальше — инкрементально по x.
            for (int r = 0; r < rayCount; r++)
            {
                var ry = _rays[r];
                float u = -ry.Slope * y + ry.Speed * _t + ry.Phase;
                m0[r] = u - ry.Period * MathF.Floor(u / ry.Period);
            }
            int rowIdx = y * cols;
            int levelBase = y * (LightLevels + 1);
            for (int x = 0; x < cols; x++)
            {
                float lift = 0f;
                for (int r = 0; r < rayCount; r++)
                {
                    var ry = _rays[r];
                    float m = m0[r] + x;
                    if (m >= ry.Period) m -= ry.Period;
                    if (m <= ry.Width)
                    {
                        // Мягкий спад к краю луча (smoothstep от центра).
                        float t = 1f - MathF.Abs(m - ry.Half) * ry.InvHalf;
                        lift += t * t * (3f - 2f * t);
                    }
                }
                int level = (int)(lift * LightLevels);
                if (level > LightLevels) level = LightLevels;
                cells[rowIdx + x] = new Cell(' ', Rgba.Black, _bgLevels[levelBase + level]);
            }
        }

        // Песок: нижние строки.
        for (int y = _sandTop; y < _rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                uint h = Hash(x, y);
                uint r = h % 100;
                char ch = r < 50 ? '░' : r < 75 ? '▒' : r < 88 ? '.' : ',';
                Rgba fg = SandFg[(int)((h >> 8) % 3)];
                Rgba bg = SandBg[(int)((h >> 20) % 2)];
                grid.Put(x, y, ch, Tint(fg), Tint(bg));
            }
        }

        // Поверхность: анимированные волны.
        for (int x = 0; x < cols; x++)
        {
            float w = MathF.Sin(x * 0.6f + _t * 2.2f) + 0.6f * MathF.Sin(x * 0.21f - _t * 1.4f);
            char ch = w > 0.4f ? '≈' : '~';
            Rgba fg = Rgba.Lerp(SurfaceLo, SurfaceHi, (w + 1.6f) / 3.2f);
            grid.Put(x, 0, ch, Tint(fg));
        }

        // Водоросли: покачиваются, амплитуда растёт к верхушке.
        float bassMul = 1f + _bass;
        foreach (var s in _stalks)
        {
            Rgba color = Tint(s.Color);
            for (int k = 0; k < s.Height; k++)
            {
                int y = s.Base - k;
                float amp = s.Height > 1 ? 1.5f * k / (s.Height - 1) : 0f;
                amp *= bassMul;
                int dx = (int)MathF.Round(MathF.Sin(_t * 1.2f + y * 0.6f + s.Phase) * amp);
                grid.Put(s.X + dx, y, k % 2 == 0 ? '(' : ')', color);
            }
        }

        // Рыбы: лёгкое покачивание по вертикали.
        foreach (var f in _fish)
        {
            string sprite = f.Dir > 0 ? FishRight[f.Sprite] : FishLeft[f.Sprite];
            int row = (int)MathF.Round(f.Y + MathF.Sin(_t * 2f + f.Phase) * 0.5f);
            row = Math.Clamp(row, 1, _waterMax);
            int x0 = (int)MathF.Floor(f.X);
            Rgba color = Tint(f.Color);
            for (int i = 0; i < sprite.Length; i++)
                if (sprite[i] != ' ') grid.Put(x0 + i, row, sprite[i], color);
        }

        // Акула поверх рыб.
        if (_sharkOn)
        {
            int x0 = (int)MathF.Floor(_sharkX);
            string[] lines = _sharkDir > 0 ? SharkRight : SharkLeft;
            Rgba color = Tint(SharkColor);
            for (int r = 0; r < SharkHeight; r++)
            {
                string line = lines[r];
                for (int i = 0; i < line.Length; i++)
                    if (line[i] != ' ') grid.Put(x0 + i, _sharkY + r, line[i], color);
            }
        }

        // Пузыри: растут по мере подъёма.
        Rgba bubble = Tint(BubbleColor);
        foreach (var b in _bubbles)
        {
            if (!b.Alive) continue;
            int x = (int)MathF.Round(b.X + MathF.Sin(_t * 2.5f + b.Phase) * 0.6f);
            int y = (int)MathF.Floor(b.Y);
            float p = 1f - b.Y / _sandTop;
            char ch = p < 0.25f ? '.' : p < 0.5f ? 'o' : p < 0.8f ? 'O' : '°';
            grid.Put(x, y, ch, bubble);
        }
    }

    public void OnAudio(in AudioFrame audio)
    {
        if (!audio.Active)
        {
            _bass = 0f;
            _rush = 0f;
            return;
        }
        _bass = Math.Clamp(audio.Bass, 0f, 1f);
        if (!audio.Beat) return;
        _beatPending = true;
        _rush = RushMul;
    }

    private void Spawn(float x, float y)
    {
        if (_bubbleCount >= _bubbles.Length) return;
        foreach (var b in _bubbles)
        {
            if (b.Alive) continue;
            b.Alive = true;
            b.X = x;
            b.Y = y;
            b.Speed = 3f + _rng.NextSingle() * 3f;
            b.Phase = _rng.NextSingle() * MathF.Tau;
            _bubbleCount++;
            return;
        }
    }

    /// <summary>Рыба уплыла за край: появляется с той же стороны или с другой (rng), на новой глубине.</summary>
    private void Reenter(Fish f, bool exitedRight)
    {
        int w = FishRight[f.Sprite].Length;
        bool sameSide = _rng.Next(2) == 0;
        bool appearRight = exitedRight == sameSide;
        if (appearRight)
        {
            f.X = _cols;
            f.Dir = -1;
        }
        else
        {
            f.X = -w;
            f.Dir = 1;
        }
        f.Y = 1 + _rng.Next(_waterMax);
        f.Panic = 0f;
    }

    private Rgba Tint(Rgba c)
    {
        if (!_terminal) return c;
        float lum = (0.3f * c.R + 0.59f * c.G + 0.11f * c.B) / 255f;
        return _termBase.Scale(lum);
    }

    private static string[] Pad(string[] rows)
    {
        int w = 0;
        foreach (var r in rows) w = Math.Max(w, r.Length);
        var result = new string[rows.Length];
        for (int i = 0; i < rows.Length; i++) result[i] = rows[i].PadRight(w);
        return result;
    }

    private static char SwapChar(char c) => c switch
    {
        '<' => '>',
        '>' => '<',
        '(' => ')',
        ')' => '(',
        '{' => '}',
        '}' => '{',
        '[' => ']',
        ']' => '[',
        '/' => '\\',
        '\\' => '/',
        _ => c,
    };

    private static uint Hash(int x, int y)
    {
        uint h = (uint)x * 0x9E3779B1u ^ (uint)y * 0x85EBCA77u;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        return h;
    }

    private sealed class Ray
    {
        public float Slope, Width, Half, InvHalf, Period, Speed, Phase;
    }

    private sealed class Stalk
    {
        public int X, Base, Height;
        public float Phase;
        public Rgba Color;
    }

    private sealed class Fish
    {
        public float X, Y, Speed, Phase, TurnTimer, Panic, Dart;
        public int Dir, Sprite;
        public Rgba Color;
    }

    private sealed class Bubble
    {
        public bool Alive;
        public float X, Y, Speed, Phase;
    }
}
