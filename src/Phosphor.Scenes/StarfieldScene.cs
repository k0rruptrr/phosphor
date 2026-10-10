using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// Звёздное поле: камера летит вперёд сквозь 3D-звёзды. Варп-прыжки с длинными шлейфами,
/// туманности из value-noise (газ, не диски), реакция на звук (бас — скорость, удар — рывок).
/// </summary>
public sealed class StarfieldScene : IScene, IAudioReactive, ISceneParams
{
    private const float DefaultDensity = 0.04f;
    private const float DefaultSpeed = 0.3f;

    // Параметры для окна настроек.
    public static readonly SceneParamSpec SpeedSpec = new("speed", SceneParamKind.Float, DefaultSpeed, 0f, 3f, "скорость полёта");
    public static readonly SceneParamSpec DensitySpec = new("density", SceneParamKind.Float, DefaultDensity, 0f, 1f, "плотность звёзд");
    public static readonly SceneParamSpec NebulaSpec = new("nebula", SceneParamKind.Bool, 1f, 0f, 1f, "туманности");
    private static readonly SceneParamSpec[] ParamList = { SpeedSpec, DensitySpec, NebulaSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    private const float MinZ = 0.02f;
    private const float BeatDuration = 0.4f;
    private const float BeatKick = 2f;            // при ударе скорость × (1 + 2) = ×3, затухает за BeatDuration
    private const float WarpMaxMul = 6f;
    private const float WarpUpTime = 1f;
    private const float WarpHoldTime = 2f;
    private const float WarpDownTime = 1.5f;
    private const float FirstWarpMin = 8f;       // первый варп через 8..15 с после Init
    private const float FirstWarpRange = 7f;
    private const float NextWarpMin = 20f;       // дальше — раз в 20..40 с
    private const float NextWarpRange = 20f;
    private const int MaxStreakCells = 40;
    private const float NebulaDrift = 0.004f;    // сдвиг туманности от центра, доли полуэкрана в секунду
    private const float NebulaStep = 0.5f;       // карта плотности туманностей пересчитывается раз в 0.5 с
    private const int LatticeSize = 16;          // решётка value-noise 16×16 на туманность
    private const int MaxRespawnTries = 8;

    public string Id => "starfield";

    private int _cols, _rows;
    private float _cx, _cy;          // центр проекции
    private float _projX, _projY;    // cols*0.5 и cols*0.5*0.55 (ячейка высокая и узкая)
    private Random _rng = new(0);
    private float _speedScale = 1f;
    private float _baseSpeed = DefaultSpeed;
    private float _audioMul = 1f;
    private float _beatLeft;         // сколько секунд ещё действует рывок от удара
    private float _warpCountdown;    // до следующего варпа
    private int _warpPhase;          // 0 нет, 1 разгон, 2 держим, 3 спад
    private float _warpT;            // время внутри текущей фазы варпа
    private float _nebT;             // время для дрейфа туманностей
    private int _nebSlot = -1;       // номер полуСекундного слота, для которого посчитана карта плотности

    // Звёзды: 3D-координаты, проекция текущего кадра и предыдущего (для шлейфов).
    private float[] _x = Array.Empty<float>();
    private float[] _y = Array.Empty<float>();
    private float[] _z = Array.Empty<float>();
    private float[] _curX = Array.Empty<float>();
    private float[] _curY = Array.Empty<float>();
    private float[] _prevX = Array.Empty<float>();
    private float[] _prevY = Array.Empty<float>();
    private float[] _streakLen = Array.Empty<float>();   // длина смещения за кадр, в ячейках
    private Rgba[] _tint = Array.Empty<Rgba>();
    private bool[] _alt = Array.Empty<bool>();

    // Туманности: карта по ячейкам — плотность 0..1, номер туманности-владельца, сдвиг оттенка 0..1.
    private Nebula[] _nebulae = Array.Empty<Nebula>();
    private float[] _nebDens = Array.Empty<float>();
    private int[] _nebWho = Array.Empty<int>();
    private float[] _nebMix = Array.Empty<float>();

    /// <summary>Туманность: центр дрейфует, форма задаётся шумом. Создаётся только в Init.</summary>
    private sealed class Nebula
    {
        public float U, V;          // центр в нормированных координатах (−1..1 по экрану)
        public float DirX, DirY;    // направление дрейфа от центра
        public float Radius;        // радиус в долях ширины сетки
        public float OffX, OffY;    // сдвиг по решётке шума, чтобы пятна не были одинаковыми
        public Rgba ColorA, ColorB; // пара оттенков внутри пятна
        public float[] Lattice = Array.Empty<float>(); // LatticeSize² случайных значений
    }

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = cols;
        _rows = rows;
        _rng = rng;
        _cx = cols * 0.5f;
        _cy = rows * 0.5f;
        _projX = cols * 0.5f;
        _projY = cols * 0.5f * 0.55f;
        _speedScale = MathF.Max(0f, options.Speed);
        _baseSpeed = Math.Clamp(options.GetFloat(SpeedSpec.Name, SpeedSpec.Default), SpeedSpec.Min, SpeedSpec.Max);
        float density = Math.Clamp(options.GetFloat(DensitySpec.Name, DensitySpec.Default), DensitySpec.Min, DensitySpec.Max);
        bool nebulaOn = options.GetBool(NebulaSpec.Name, NebulaSpec.Default > 0.5f);
        string palette = options.Palette.ToLowerInvariant();
        bool themed = palette is "green" or "amber" or "mono";
        bool rainbow = palette == "rainbow";
        Rgba themeBase = Palettes.Base(palette);

        _audioMul = 1f;
        _beatLeft = 0f;
        _warpPhase = 0;
        _warpT = 0f;
        _warpCountdown = FirstWarpMin + _rng.NextSingle() * FirstWarpRange;
        _nebT = 0f;
        _nebSlot = -1;

        int count = Math.Max(1, (int)MathF.Round(density * cols * rows));
        _x = new float[count];
        _y = new float[count];
        _z = new float[count];
        _curX = new float[count];
        _curY = new float[count];
        _prevX = new float[count];
        _prevY = new float[count];
        _streakLen = new float[count];
        _alt = new bool[count];
        _tint = new Rgba[count];

        for (int i = 0; i < count; i++)
        {
            _x[i] = _rng.NextSingle() * 2f - 1f;
            _y[i] = _rng.NextSingle() * 2f - 1f;
            _z[i] = 1f - _rng.NextSingle() * 0.98f;     // (0.02, 1]
            _alt[i] = _rng.NextSingle() < 0.5f;
            _tint[i] = rainbow ? Rgba.Hsv(_rng.NextSingle() * 360f, 0.5f, 1f)
                     : themed ? Rgba.Lerp(themeBase, Rgba.White, _rng.NextSingle() * 0.2f)
                     : StarTint(_rng.NextSingle());
            Project(i);
            _prevX[i] = _curX[i];
            _prevY[i] = _curY[i];
        }

        int nebulaCount = nebulaOn ? 2 + _rng.Next(3) : 0;   // 2..4
        _nebulae = new Nebula[nebulaCount];
        for (int n = 0; n < nebulaCount; n++)
        {
            float angle = _rng.NextSingle() * MathF.Tau;
            var lattice = new float[LatticeSize * LatticeSize];
            for (int k = 0; k < lattice.Length; k++) lattice[k] = _rng.NextSingle();

            Rgba a, b;
            if (themed)
            {
                a = themeBase;
                b = Rgba.Lerp(themeBase, Rgba.Black, 0.4f);
            }
            else if (rainbow)
            {
                float hue = _rng.NextSingle() * 360f;
                a = Rgba.Hsv(hue, 0.7f, 1f);
                b = Rgba.Hsv(hue + 40f, 0.7f, 1f);
            }
            else
            {
                float r = _rng.NextSingle();
                if (r < 0.45f) { a = Rgba.Hsv(250f, 0.8f, 1f); b = Rgba.Hsv(285f, 0.7f, 1f); }   // сине-фиолетовый
                else if (r < 0.85f) { a = Rgba.Hsv(175f, 0.8f, 1f); b = Rgba.Hsv(195f, 0.7f, 1f); } // бирюзовый
                else { a = Rgba.Hsv(330f, 0.6f, 1f); b = Rgba.Hsv(300f, 0.6f, 1f); }             // тускло-розовый
            }

            _nebulae[n] = new Nebula
            {
                U = (_rng.NextSingle() * 2f - 1f) * 0.6f,
                V = (_rng.NextSingle() * 2f - 1f) * 0.5f,
                DirX = MathF.Cos(angle),
                DirY = MathF.Sin(angle),
                Radius = 0.09f + 0.07f * _rng.NextSingle(),
                OffX = _rng.NextSingle() * LatticeSize,
                OffY = _rng.NextSingle() * LatticeSize,
                ColorA = a,
                ColorB = b,
                Lattice = lattice,
            };
        }

        _nebDens = new float[cols * rows];
        _nebWho = new int[cols * rows];
        _nebMix = new float[cols * rows];
        if (nebulaCount > 0) RebuildNebulae();
        _nebSlot = 0;
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;

        UpdateWarp(dt);
        if (_beatLeft > 0f) _beatLeft = MathF.Max(0f, _beatLeft - dt);
        _nebT += dt;

        int slot = (int)MathF.Floor(_nebT / NebulaStep);
        if (slot != _nebSlot && _nebulae.Length > 0)
        {
            _nebSlot = slot;
            RebuildNebulae();
        }

        float beatMul = 1f + BeatKick * (_beatLeft / BeatDuration);
        float speed = _baseSpeed * _speedScale * WarpMul() * _audioMul * beatMul;
        float step = speed * dt;

        for (int i = 0; i < _z.Length; i++)
        {
            _prevX[i] = _curX[i];
            _prevY[i] = _curY[i];

            float z = _z[i] - step;
            if (z <= MinZ)
            {
                Respawn(i);
                continue;
            }
            _z[i] = z;
            Project(i);
            if (!OnScreen(i))
            {
                Respawn(i);
                continue;
            }
            float dx = _curX[i] - _prevX[i];
            float dy = _curY[i] - _prevY[i];
            _streakLen[i] = MathF.Max(MathF.Abs(dx), MathF.Abs(dy));
        }
    }

    public void Draw(CharGrid grid)
    {
        grid.Clear(Rgba.Black);
        DrawNebulae(grid);
        DrawStreaks(grid);
        DrawStars(grid);
    }

    public void OnAudio(in AudioFrame audio)
    {
        if (!audio.Active)
        {
            _audioMul = 1f;
            _beatLeft = 0f;
            return;
        }
        _audioMul = 1f + audio.Bass;
        if (audio.Beat) _beatLeft = BeatDuration;
    }

    // ---- варп ----

    private void UpdateWarp(float dt)
    {
        switch (_warpPhase)
        {
            case 0:
                _warpCountdown -= dt;
                if (_warpCountdown <= 0f) { _warpPhase = 1; _warpT = 0f; }
                break;
            case 1:
                _warpT += dt;
                if (_warpT >= WarpUpTime) { _warpPhase = 2; _warpT = 0f; }
                break;
            case 2:
                _warpT += dt;
                if (_warpT >= WarpHoldTime) { _warpPhase = 3; _warpT = 0f; }
                break;
            default:
                _warpT += dt;
                if (_warpT >= WarpDownTime)
                {
                    _warpPhase = 0;
                    _warpT = 0f;
                    _warpCountdown = NextWarpDelay();
                }
                break;
        }
    }

    private float WarpMul() => _warpPhase switch
    {
        1 => 1f + (WarpMaxMul - 1f) * Smooth(_warpT / WarpUpTime),
        2 => WarpMaxMul,
        3 => WarpMaxMul - (WarpMaxMul - 1f) * Smooth(_warpT / WarpDownTime),
        _ => 1f,
    };

    private float NextWarpDelay() => NextWarpMin + _rng.NextSingle() * NextWarpRange;

    private static float Smooth(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // ---- звёзды ----

    private void Project(int i)
    {
        float inv = 1f / _z[i];
        _curX[i] = _cx + _x[i] * inv * _projX;
        _curY[i] = _cy + _y[i] * inv * _projY;
    }

    private bool OnScreen(int i) =>
        _curX[i] >= 0f && _curX[i] < _cols && _curY[i] >= 0f && _curY[i] < _rows;

    /// <summary>Звезда возрождается на z = 1. Несколько попыток, чтобы сразу попасть в кадр.</summary>
    private void Respawn(int i)
    {
        for (int t = 0; t < MaxRespawnTries; t++)
        {
            _x[i] = _rng.NextSingle() * 2f - 1f;
            _y[i] = _rng.NextSingle() * 2f - 1f;
            _z[i] = 1f;
            Project(i);
            if (OnScreen(i)) break;
        }
        // Без шлейфа от прошлого места.
        _prevX[i] = _curX[i];
        _prevY[i] = _curY[i];
        _streakLen[i] = 0f;
    }

    private void DrawStars(CharGrid grid)
    {
        for (int i = 0; i < _z.Length; i++)
        {
            int x = (int)MathF.Floor(_curX[i]);
            int y = (int)MathF.Floor(_curY[i]);
            if (!grid.InBounds(x, y)) continue;
            float z = _z[i];
            // Голова быстрого шлейфа — яркая звезда.
            float k = MathF.Max(Brightness(z), MathF.Min(1f, _streakLen[i] / 3f));
            grid.Put(x, y, StarGlyph(z, _alt[i]), _tint[i].Scale(k));
        }
    }

    /// <summary>
    /// Шлейф: непрерывная линия по Брезенхэму от головы (текущая клетка звезды) к прошлой клетке.
    /// Голова яркая (Scale 1.0), хвост гаснет до 0.35 и слегка голубеет.
    /// </summary>
    private void DrawStreaks(CharGrid grid)
    {
        var tailBlue = Rgba.Hex(0x7FB8FF);
        for (int i = 0; i < _z.Length; i++)
        {
            if (_streakLen[i] < 1f) continue;

            float dx = _curX[i] - _prevX[i];
            float dy = _curY[i] - _prevY[i];
            int hx = (int)MathF.Floor(_curX[i]), hy = (int)MathF.Floor(_curY[i]);
            int tx = (int)MathF.Floor(_prevX[i]), ty = (int)MathF.Floor(_prevY[i]);
            int steps = Math.Min(MaxStreakCells, Math.Max(Math.Abs(tx - hx), Math.Abs(ty - hy)));
            if (steps == 0) continue;

            char ch = StreakChar(dx, dy);
            float bright = Brightness(_z[i]);
            var tint = _tint[i];

            // Брезенхэм от головы к хвосту.
            int x = hx, y = hy;
            int ddx = Math.Abs(tx - hx), sx = hx < tx ? 1 : -1;
            int ddy = -Math.Abs(ty - hy), sy = hy < ty ? 1 : -1;
            int err = ddx + ddy;
            for (int s = 1; s <= steps; s++)
            {
                int e2 = 2 * err;
                if (e2 >= ddy) { err += ddy; x += sx; }
                if (e2 <= ddx) { err += ddx; y += sy; }

                float t = s / (float)steps;
                float fade = 1f - 0.65f * t;   // хвост гаснет до 0.35
                var fg = Rgba.Lerp(tint.Scale(bright * fade), tailBlue.Scale(fade), 0.35f * t);
                grid.Put(x, y, ch, fg);
            }
        }
    }

    // ---- туманности ----

    /// <summary>Пересчитывает карту плотности туманностей по всем ячейкам. Без аллокаций, без rng.</summary>
    private void RebuildNebulae()
    {
        Array.Clear(_nebDens);
        float drift = _nebT * NebulaDrift;
        for (int n = 0; n < _nebulae.Length; n++)
        {
            var nb = _nebulae[n];
            float cx = _cx + (nb.U + nb.DirX * drift) * _cols * 0.5f;
            float cy = _cy + (nb.V + nb.DirY * drift) * _rows * 0.5f;
            float rx = MathF.Max(1f, nb.Radius * _cols);
            float ry = rx * 0.55f;                       // круг в ячейках 10×18

            int x0 = Math.Max(0, (int)MathF.Floor(cx - rx));
            int x1 = Math.Min(_cols - 1, (int)MathF.Ceiling(cx + rx));
            int y0 = Math.Max(0, (int)MathF.Floor(cy - ry));
            int y1 = Math.Min(_rows - 1, (int)MathF.Ceiling(cy + ry));

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    float dx = (x + 0.5f - cx) / rx;
                    float dy = (y + 0.5f - cy) / ry;
                    float d2 = dx * dx + dy * dy;
                    if (d2 >= 1f) continue;

                    // Мягкая радиальная маска (1 − d²)² и шум в 3 октавы: рваные клочья без границы.
                    float mask = 1f - d2;
                    mask *= mask;
                    float lx = dx * 2f + nb.OffX, ly = dy * 2f + nb.OffY;
                    float f = 0.5f * Noise(nb.Lattice, lx, ly)
                            + 0.3f * Noise(nb.Lattice, lx * 2f, ly * 2f)
                            + 0.2f * Noise(nb.Lattice, lx * 4f, ly * 4f);
                    float d = Math.Clamp((mask * f - 0.06f) * 2.2f, 0f, 1f);
                    if (d <= 0f) continue;

                    int idx = y * _cols + x;
                    if (d > _nebDens[idx])
                    {
                        _nebDens[idx] = d;
                        _nebWho[idx] = n;
                        _nebMix[idx] = Noise(nb.Lattice, lx * 1.3f + 7.3f, ly * 1.3f + 3.1f);
                    }
                }
            }
        }
    }

    private void DrawNebulae(CharGrid grid)
    {
        if (_nebulae.Length == 0) return;
        for (int y = 0; y < _rows; y++)
        {
            for (int x = 0; x < _cols; x++)
            {
                int idx = y * _cols + x;
                float d = _nebDens[idx];
                if (d <= 0f) continue;

                var nb = _nebulae[_nebWho[idx]];
                var col = Rgba.Lerp(nb.ColorA, nb.ColorB, _nebMix[idx]);
                var bg = col.Scale(d * 0.12f);
                // '░' только в плотных местах и только в части ячеек; иначе пробел с цветным фоном.
                if (d > 0.6f && (Hash(x, y, 0) & 3) == 0)
                    grid.Put(x, y, '░', col.Scale(0.15f + 0.15f * d), bg);
                else
                    grid.Put(x, y, ' ', col, bg);
            }
        }
    }

    /// <summary>Value noise: билинейная интерполяция решётки со smoothstep. Результат в 0..1.</summary>
    private static float Noise(float[] lat, float x, float y)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float fx = x - xi, fy = y - yi;
        float sx = fx * fx * (3f - 2f * fx), sy = fy * fy * (3f - 2f * fy);
        int x0 = xi & (LatticeSize - 1), x1 = (xi + 1) & (LatticeSize - 1);
        int y0 = yi & (LatticeSize - 1), y1 = (yi + 1) & (LatticeSize - 1);
        float a = lat[y0 * LatticeSize + x0], b = lat[y0 * LatticeSize + x1];
        float c = lat[y1 * LatticeSize + x0], d = lat[y1 * LatticeSize + x1];
        float top = a + (b - a) * sx;
        float bottom = c + (d - c) * sx;
        return top + (bottom - top) * sy;
    }

    // ---- помощники (чистые, без состояния) ----

    private static float Brightness(float z) => 0.25f + 0.75f * (1f - z);

    private static char StarGlyph(float z, bool alt)
    {
        if (z > 0.75f) return '.';
        if (z > 0.5f) return alt ? '∙' : '·';
        if (z > 0.3f) return '*';
        if (z > 0.15f) return '•';
        return '●';
    }

    private static char StreakChar(float dx, float dy)
    {
        float ax = MathF.Abs(dx), ay = MathF.Abs(dy);
        if (ay * 2f < ax) return '─';
        if (ax * 2f < ay) return '│';
        return dx * dy >= 0f ? '\\' : '/';   // экран: y вниз
    }

    private static Rgba StarTint(float r)
    {
        if (r < 0.6f) return Rgba.White;
        if (r < 0.8f) return Rgba.Hex(0xC8DCFF);   // голубоватые
        return Rgba.Hex(0xFFF0C8);                 // желтоватые
    }

    private static uint Hash(int x, int y, int n)
    {
        uint h = (uint)x * 374761393u + (uint)y * 668265263u + (uint)n * 2246822519u;
        h ^= h >> 13;
        h *= 1274126177u;
        h ^= h >> 16;
        return h;
    }
}
