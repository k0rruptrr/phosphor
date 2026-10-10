using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// Воксельный пролёт над горами (Voxel Space, по мотивам Comanche 1992; алгоритм s-macke/VoxelSpace).
/// Карта высот и цветов 512×512 на торе, генерируется в Init. Кадр рендерится в Update в буфер пикселей
/// W×H (W = cols, H = rows × 2); Draw переводит пары пикселей в полублоки '▀' (верх — Fg, низ — Bg) и рисует HUD.
/// Draw не меняет состояние и не использует rng.
/// </summary>
public sealed class VoxelScene : IScene, IAudioReactive, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec FlySpec = new("flySpeed", SceneParamKind.Float, 40f, 0f, 200f, "скорость полёта, ед./с");
    private static readonly SceneParamSpec[] ParamList = { FlySpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    private float _flySpeed;

    private const int MapSize = 512;
    private const int MapMask = MapSize - 1;
    private const int MapShift = 9;
    private const int Octaves = 6;
    private const float WaterFrac = 0.30f;       // доля карты под водой (по рангу высоты)
    private const float SnowFrac = 0.94f;        // выше этого ранга — снег (верхние ~6%)
    private const float HeightExp = 1.8f;        // степень ранга: расширяет низины
    private const float ClearanceTarget = 60f;   // высота над максимумом впереди
    private const float ClearanceMin = 12f;      // жёсткий минимум над землёй под камерой
    private const float LookAhead = 120f;        // дистанция, по которой ищем максимум высоты впереди
    private const float MaxDist = 340f;
    private const float FogStart = 220f;
    private const float DzStart = 0.6f;          // начальный шаг по дистанции (мелкий вблизи)
    private const float DzFactor = 1.015f;       // шаг растёт мультипликативно
    private const float HorizonBase = 0.38f;     // горизонт в долях высоты кадра (сверху)
    private const float ScaleHeightK = 0.4f;     // масштаб вертикали: k = H × K
    private const float OffsetCoord = 1024f;     // сдвиг координат лучей, чтобы они были положительными
    private const float KickDuration = 0.15f;
    private const float KickPixels = 8f;        // толчок горизонта на ударе (тангаж), пикс., затухает за KickDuration
    private const float YawKick = 0.15f;        // рад (≈9°): рывок курса камеры на ударе…
    private const float YawWhipSeconds = 0.15f; // …полуволна синуса за 0.15 с (плавный «взмах», без мигания)
    private const float FlyKick = 3f;           // рывок полёта на ударе: скорость ×4 в момент удара…
    private const float FlyKickTau = 0.06f;     // …затухает к обычной за ~0.25 с
    private const float BassPixels = 6f;
    private const float SwayPeriod = 40f;
    private const float SwayAmp = 0.35f;
    private const float TurnRate = 0.12f;       // рад/с
    private const float ShadeStrength = 0.1f;

    private static readonly Rgba SkyTop = Rgba.Hex(0x0B1430);
    private static readonly Rgba SkyHorizon = Rgba.Hex(0xFF9A5C);
    private static readonly Rgba WaterDeep = Rgba.Hex(0x0A2A5A);
    private static readonly Rgba WaterShallow = Rgba.Hex(0x1F6AA8);
    private static readonly Rgba Glint = Rgba.Hex(0xB8E4FF);
    private static readonly Rgba Sand = Rgba.Hex(0xC9B27C);
    private static readonly Rgba GrassA = Rgba.Hex(0x3D7A2B);
    private static readonly Rgba GrassB = Rgba.Hex(0x5E9A3A);
    private static readonly Rgba Earth = Rgba.Hex(0x6E5A3A);
    private static readonly Rgba RockA = Rgba.Hex(0x776A5E);
    private static readonly Rgba RockB = Rgba.Hex(0x8E8577);
    private static readonly Rgba Snow = Rgba.Hex(0xF0F4FF);
    private static readonly Rgba HudColor = Rgba.Hex(0x8A9BB0);
    private static readonly Rgba CrossColor = Rgba.Hex(0x50565E);

    // Свет с северо-запада (x — восток, y — юг, z — вверх).
    private const float LightX = -0.5f, LightY = -0.5f, LightZ = 0.7071f;

    private const string HudAlt = "ALT ";
    private const string HudHdg = " · HDG ";
    private const string HudDeg = "°";
    private const string HudSpd = " · SPD ";
    private const string HudTitle = "VOXEL SPACE · MAP 512×512";

    public string Id => "voxel";

    /// <summary>Высота камеры в единицах карты высот (байт 0..255 и выше).</summary>
    public float CameraHeight => _camH;

    /// <summary>Смещение горизонта от базового положения, пикс.: баса и толчка на ударе (для проверок в тестах).</summary>
    public float HorizonShiftPx => _horizonPx - _h * HorizonBase;

    /// <summary>Высота рельефа прямо под камерой в тех же единицах.</summary>
    public float GroundUnderCamera => HeightAt(_camX, _camY);

    private int _cols, _rows, _w, _h;
    private Random _rng = new(0);
    private string _palette = "default";
    private bool _thermal;
    private float _speed = 1f;
    private int _spdKmh;

    private byte[] _height = Array.Empty<byte>();
    private Rgba[] _color = Array.Empty<Rgba>();
    private Rgba[] _frame = Array.Empty<Rgba>();   // W×H псевдопикселей
    private Rgba[] _sky = Array.Empty<Rgba>();     // по строкам
    private int[] _ybuf = Array.Empty<int>();      // по колонкам: верх уже нарисованного
    private Rgba _fog, _hud, _cross;

    private float _camX, _camY, _camH;
    private float _yawBase, _yawTarget, _turnLeft, _time;
    private float _sin, _cos;
    private int _hdg;
    private float _bass, _bassPx, _kickLeft, _horizonPx;
    private float _yawOff;                      // рывок курса после удара, рад
    private float _yawAmp, _yawT = YawWhipSeconds; // амплитуда взмаха и время с удара (≥ WhipSeconds — взмаха нет)
    private float _flyKick;                     // добавка к скорости полёта после удара (×(1+_flyKick))
    private int _yawSign = 1;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _flySpeed = Math.Clamp(options.GetFloat(FlySpec.Name, FlySpec.Default), FlySpec.Min, FlySpec.Max);
        _cols = cols;
        _rows = rows;
        _w = cols;
        _h = rows * 2;
        _rng = rng;
        _speed = MathF.Max(0f, options.Speed);
        _spdKmh = Math.Clamp((int)MathF.Round(_flySpeed * _speed), 0, 999);
        _palette = (options.Palette ?? "default").ToLowerInvariant();
        _thermal = _palette is "green" or "amber" or "mono";

        _fog = Tint(SkyHorizon);
        _hud = Tint(HudColor);
        _cross = Tint(CrossColor);

        BuildTerrain(rng);

        _sky = new Rgba[_h];
        float horizonRows = _h * HorizonBase;
        for (int y = 0; y < _h; y++)
        {
            float t = MathF.Min(1f, y / horizonRows);
            _sky[y] = Tint(Rgba.Lerp(SkyTop, SkyHorizon, MathF.Pow(t, 1.5f)));
        }
        _frame = new Rgba[_w * _h];
        _ybuf = new int[_w];

        _camX = rng.NextSingle() * MapSize;
        _camY = rng.NextSingle() * MapSize;
        float yaw = rng.NextSingle() * MathF.Tau;
        _yawBase = yaw;
        _yawTarget = yaw;
        _turnLeft = 15f + rng.NextSingle() * 15f;
        _time = 0f;
        _bass = 0f;
        _bassPx = 0f;
        _kickLeft = 0f;
        _yawOff = 0f;
        _yawAmp = 0f;
        _yawT = YawWhipSeconds;
        _yawSign = 1;
        _flyKick = 0f;
        _horizonPx = _h * HorizonBase;

        UpdateHeading();
        _camH = AheadMax() + ClearanceTarget;
        EnsureClearance();
        Render();
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;
        if (dt > 0.1f) dt = 0.1f;
        _time += dt;

        // Поворот: медленная случайная цель раз в 15–30 с, плюс синусоидальное покачивание курса.
        _turnLeft -= dt;
        if (_turnLeft <= 0f)
        {
            _yawTarget = _yawBase + (_rng.NextSingle() * 2f - 1f) * 0.8f;
            _turnLeft = 15f + _rng.NextSingle() * 15f;
        }
        float maxStep = TurnRate * dt;
        _yawBase += Math.Clamp(_yawTarget - _yawBase, -maxStep, maxStep);
        _yawT += dt;
        _yawOff = _yawT < YawWhipSeconds ? _yawAmp * MathF.Sin(MathF.PI * _yawT / YawWhipSeconds) : 0f;
        UpdateHeading();

        // Полёт вперёд: направление взгляда = (-sin, -cos).
        _flyKick *= MathF.Exp(-dt / FlyKickTau);
        float v = _flySpeed * _speed * dt * (1f + _flyKick);
        _camX = Wrap(_camX - _sin * v);
        _camY = Wrap(_camY - _cos * v);

        // Высота: максимум впереди + клиренс; вверх быстрее, вниз медленнее.
        float target = AheadMax() + ClearanceTarget;
        float rate = target > _camH ? 3f : 0.8f;
        _camH += (target - _camH) * (1f - MathF.Exp(-rate * dt));
        EnsureClearance();

        // Звук: покачивание горизонта от баса и короткий толчок на ударе.
        float bassTarget = BassPixels * _bass;
        _bassPx += (bassTarget - _bassPx) * (1f - MathF.Exp(-12f * dt));
        _kickLeft = MathF.Max(0f, _kickLeft - dt);
        float kick = KickPixels * (_kickLeft / KickDuration);
        _horizonPx = _h * HorizonBase + _bassPx + kick;

        Render();
    }

    public void Draw(CharGrid grid)
    {
        if (grid.Cols != _cols || grid.Rows != _rows) grid.Clear(Rgba.Black);
        int cols = Math.Min(grid.Cols, _cols), rows = Math.Min(grid.Rows, _rows);
        var cells = grid.Cells;
        int w = _w;
        for (int r = 0; r < rows; r++)
        {
            int top = 2 * r * w, bot = top + w, rowBase = r * grid.Cols;
            for (int x = 0; x < cols; x++)
                cells[rowBase + x] = new Cell('▀', _frame[top + x], _frame[bot + x]);
        }

        // HUD по краям, отступ 2. Числа пишутся без аллокаций.
        int x0 = 2;
        x0 = Text(grid, x0, 2, HudAlt, _hud);
        x0 = Num(grid, x0, 2, (int)_camH, 4, _hud);
        x0 = Text(grid, x0, 2, HudHdg, _hud);
        x0 = Num(grid, x0, 2, _hdg, 3, _hud);
        x0 = Text(grid, x0, 2, HudDeg, _hud);
        x0 = Text(grid, x0, 2, HudSpd, _hud);
        Num(grid, x0, 2, _spdKmh, 3, _hud);

        Text(grid, grid.Cols - 2 - HudTitle.Length, 2, HudTitle, _hud);
        grid.Put(grid.Cols / 2, grid.Rows / 2, '┼', _cross);
    }

    public void OnAudio(in AudioFrame audio)
    {
        _bass = audio.Active ? audio.Bass : 0f;
        if (!audio.Active)
        {
            _yawOff = 0f;
            _yawT = YawWhipSeconds;
            _flyKick = 0f;
            return;
        }
        if (!audio.Beat) return;
        _kickLeft = KickDuration;
        _yawSign = -_yawSign;
        _yawAmp = YawKick * _yawSign;
        _yawT = 0f;
        _flyKick = FlyKick;
    }

    // ---------- Карта ----------

    private void BuildTerrain(Random rng)
    {
        const int N = MapSize;
        int total = N * N;

        // Фрактальный value noise на торе: 6 октав, клетки 4..128 на карту (период = карта, шва нет).
        var field = new float[total];
        for (int oct = 0; oct < Octaves; oct++)
            AddValueNoise(field, 4 << oct, 1f / (1 << oct), rng);

        // Равномерное распределение через гистограмму, затем степень HeightExp: вода ≈ 30%, снег ≈ 6%.
        float waterH = MathF.Pow(WaterFrac, HeightExp);
        float snowH = MathF.Pow(SnowFrac, HeightExp);
        float midLo = MathF.Pow(0.55f, HeightExp), midHi = MathF.Pow(0.8f, HeightExp);
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < total; i++)
        {
            float v = field[i];
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        const int Bins = 4096;
        float binScale = (Bins - 1) / MathF.Max(1e-6f, hi - lo);
        var cum = new float[Bins];
        for (int i = 0; i < total; i++) cum[BinOf(field[i], lo, binScale)] += 1f;
        float acc = 0f;
        for (int b = 0; b < Bins; b++)
        {
            acc += cum[b];
            cum[b] = acc / total;
        }

        var hf = new float[total];
        for (int i = 0; i < total; i++)
            hf[i] = MathF.Pow(cum[BinOf(field[i], lo, binScale)], HeightExp);

        // Вода плоская: ниже уровня высота = уровень воды.
        _height = new byte[total];
        for (int i = 0; i < total; i++)
        {
            float hv = MathF.Max(hf[i], waterH) * 255f + 0.5f;
            _height[i] = (byte)Math.Clamp(hv, 0f, 255f);
        }

        // Низкочастотный оттенок травы/скал 0..1.
        var tint = new float[total];
        AddValueNoise(tint, 16, 1f, rng);
        float tlo = float.MaxValue, thi = float.MinValue;
        for (int i = 0; i < total; i++)
        {
            if (tint[i] < tlo) tlo = tint[i];
            if (tint[i] > thi) thi = tint[i];
        }
        float tscale = 1f / MathF.Max(1e-6f, thi - tlo);

        _color = new Rgba[total];
        for (int y = 0; y < N; y++)
        {
            for (int x = 0; x < N; x++)
            {
                int i = y * N + x;
                float hfi = hf[i];
                float t = (tint[i] - tlo) * tscale;

                float hE = _height[y * N + ((x + 1) & MapMask)];
                float hW = _height[y * N + ((x - 1) & MapMask)];
                float hS = _height[((y + 1) & MapMask) * N + x];
                float hN = _height[((y - 1) & MapMask) * N + x];
                float dzdx = (hE - hW) * 0.5f;
                float dzdy = (hS - hN) * 0.5f;
                float slope = MathF.Sqrt(dzdx * dzdx + dzdy * dzdy);

                // Затенение: нормаль (-dzdx, -dzdy, 1) относительно света с NW.
                float nx = -dzdx * ShadeStrength, ny = -dzdy * ShadeStrength;
                float len = MathF.Sqrt(nx * nx + ny * ny + 1f);
                float ndl = (nx * LightX + ny * LightY + LightZ) / len;
                float shade = Math.Clamp(ndl / LightZ, 0.4f, 1.05f);

                Rgba c;
                if (hfi < waterH)
                {
                    float depth = hfi / waterH;
                    c = Rgba.Lerp(WaterDeep, WaterShallow, depth);
                    if (rng.NextSingle() < 0.03f) c = Glint;
                    shade = 1f;
                }
                else
                {
                    Rgba grass = Rgba.Lerp(GrassA, GrassB, t);
                    c = Rgba.Lerp(Sand, grass, SmoothStep(waterH, waterH + 0.03f, hfi));
                    Rgba mid = Rgba.Lerp(grass, Earth, SmoothStep(midLo, midHi, hfi));
                    Rgba rock = Rgba.Lerp(RockA, RockB, t);
                    c = Rgba.Lerp(mid, rock, SmoothStep(3f, 6f, slope));
                    c = Rgba.Lerp(c, Snow, SmoothStep(snowH, snowH + 0.03f, hfi));
                }
                _color[i] = Tint(c.Scale(shade));
            }
        }
    }

    private static int BinOf(float v, float lo, float binScale) =>
        Math.Clamp((int)((v - lo) * binScale), 0, 4095);

    /// <summary>Добавляет к dst значения value noise с периодом карты: lattice cells×cells, сглаживание smoothstep.</summary>
    private static void AddValueNoise(float[] dst, int cells, float amp, Random rng)
    {
        const int N = MapSize;
        var lat = new float[cells * cells];
        for (int i = 0; i < lat.Length; i++) lat[i] = rng.NextSingle() * 2f - 1f;
        float s = (float)cells / N;
        for (int y = 0; y < N; y++)
        {
            float fy = y * s;
            int y0 = (int)fy;
            float ty = fy - y0;
            ty = ty * ty * (3f - 2f * ty);
            int y1 = (y0 + 1) % cells;
            for (int x = 0; x < N; x++)
            {
                float fx = x * s;
                int x0 = (int)fx;
                float tx = fx - x0;
                tx = tx * tx * (3f - 2f * tx);
                int x1 = (x0 + 1) % cells;
                float a = lat[y0 * cells + x0], b = lat[y0 * cells + x1];
                float c = lat[y1 * cells + x0], d = lat[y1 * cells + x1];
                float top = a + (b - a) * tx, bot = c + (d - c) * tx;
                dst[y * N + x] += amp * (top + (bot - top) * ty);
            }
        }
    }

    private static float SmoothStep(float a, float b, float v)
    {
        float t = Math.Clamp((v - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Тепловизор (green/amber/mono): все цвета — оттенки базового по яркости. Остальное — как есть.</summary>
    private Rgba Tint(Rgba c)
    {
        if (!_thermal) return c;
        float lum = (0.3f * c.R + 0.59f * c.G + 0.11f * c.B) / 255f;
        return Palettes.Base(_palette).Scale(lum);
    }

    // ---------- Камера ----------

    private static float Wrap(float v)
    {
        v %= MapSize;
        return v < 0f ? v + MapSize : v;
    }

    private float HeightAt(float x, float y) =>
        _height[((((int)MathF.Floor(y)) & MapMask) << MapShift) | (((int)MathF.Floor(x)) & MapMask)];

    /// <summary>Максимум высоты впереди: по оси взгляда и по бокам в пределах поля зрения.</summary>
    private float AheadMax()
    {
        float m = HeightAt(_camX, _camY);
        for (float d = 10f; d <= LookAhead; d += 10f)
        {
            m = MathF.Max(m, HeightAt(_camX - _sin * d, _camY - _cos * d));
        }
        for (float d = 40f; d <= 120f; d += 40f)
        {
            float ox = _cos * d * 0.6f, oy = -_sin * d * 0.6f;
            m = MathF.Max(m, HeightAt(_camX - _sin * d + ox, _camY - _cos * d + oy));
            m = MathF.Max(m, HeightAt(_camX - _sin * d - ox, _camY - _cos * d - oy));
        }
        return m;
    }

    private void EnsureClearance()
    {
        float g = HeightAt(_camX, _camY) + ClearanceMin;
        if (_camH < g) _camH = g;
    }

    private void UpdateHeading()
    {
        float phi = _yawBase + _yawOff + SwayAmp * MathF.Sin(MathF.Tau * _time / SwayPeriod);
        _sin = MathF.Sin(phi);
        _cos = MathF.Cos(phi);
        int deg = (int)MathF.Round(phi * 180f / MathF.PI) % 360;
        _hdg = deg < 0 ? deg + 360 : deg;
    }

    // ---------- Рендер Voxel Space ----------

    private void Render()
    {
        int w = _w, h = _h;
        for (int y = 0; y < h; y++) Array.Fill(_frame, _sky[y], y * w, w);
        for (int i = 0; i < w; i++) _ybuf[i] = h;

        float hz = _horizonPx;
        float k = h * ScaleHeightK;
        float sin = _sin, cos = _cos;
        // Левый и правый край поля зрения на дистанции z: forward ± left.
        float fx = -sin, fy = -cos, lx = -cos, ly = sin;
        float cx = _camX + OffsetCoord, cy = _camY + OffsetCoord;
        float camH = _camH;

        float dz = DzStart;
        for (float z = DzStart; z < MaxDist; z += dz, dz *= DzFactor)
        {
            float plx = cx + (fx + lx) * z;
            float ply = cy + (fy + ly) * z;
            float prx = cx + (fx - lx) * z;
            float pry = cy + (fy - ly) * z;
            float dx = (prx - plx) / w;
            float dy = (pry - ply) / w;
            float invz = k / z;
            float ft = Math.Clamp((z - FogStart) / (MaxDist - FogStart), 0f, 1f);

            for (int i = 0; i < w; i++)
            {
                int idx = ((((int)ply) & MapMask) << MapShift) | (((int)plx) & MapMask);
                float on = (camH - _height[idx]) * invz + hz;
                int top = on <= 0f ? 0 : (int)on;
                int bot = _ybuf[i];
                if (top < bot)
                {
                    Rgba c = _color[idx];
                    if (ft > 0f) c = Rgba.Lerp(c, _fog, ft);
                    for (int y = top; y < bot; y++) _frame[y * w + i] = c;
                    _ybuf[i] = top;
                }
                plx += dx;
                ply += dy;
            }
        }
    }

    // ---------- HUD helpers (без аллокаций) ----------

    private static int Text(CharGrid g, int x, int y, string s, Rgba c)
    {
        g.Text(x, y, s, c);
        return x + s.Length;
    }

    private static int Num(CharGrid g, int x, int y, int v, int digits, Rgba c)
    {
        int lim = 1;
        for (int i = 0; i < digits; i++) lim *= 10;
        v = Math.Clamp(v, 0, lim - 1);
        for (int d = digits - 1; d >= 0; d--)
        {
            g.Put(x + d, y, (char)('0' + v % 10), c);
            v /= 10;
        }
        return x + digits;
    }
}
