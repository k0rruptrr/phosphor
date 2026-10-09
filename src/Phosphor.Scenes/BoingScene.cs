using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// Amiga Boing Ball (1984): шар в красно-белую шахматку скачет над сеткой-полом, тень смещена вправо-вниз.
/// Холст HalfBlock, логика на RetroClock(50). При музыке удар об пол совпадает с ударом: фаза подтягивается
/// плавно (через скорость, без телепортации), бас сплющивает шар при касании.
/// </summary>
public sealed class BoingScene : IScene, IAudioReactive
{
    private const float Hz = 50f;
    private const float TickDt = 1f / 50f;
    private const float BaseHop = 1f;          // с, период прыжка без музыки
    private const float PeakFrac = 0.30f;     // высота прыжка / высота холста
    private const float RadiusFrac = 0.20f;   // радиус / высота холста: диаметр ~40%
    private const float FloorFrac = 0.8f;     // строка пола / высота холста (пол — нижние ~20%)
    private const float TiltRad = 20f * MathF.PI / 180f;
    private const float SpinSpeed = 2.6f;     // рад/с
    private const float DriftFrac = 0.18f;    // горизонтальная скорость, ширин/с
    private const float SquashFlat = 0.85f;   // сплющивание при касании
    private const float SquashRecover = 10f;  // 1/с
    private const float LockBeats = 1.5f;     // дольше полутора ударов без музыки — классическая физика
    private const float BpmMin = 30f, BpmMax = 300f;
    private const int Lat = 8, Lon = 8;       // шахматка 8 широт × 8 долгот
    private const int FloorLines = 8;

    private static readonly Rgba Wall = Rgba.Hex(0xAAAAAA);
    private static readonly Rgba GridColor = Rgba.Hex(0xAA00AA);
    private static readonly Rgba Red = Rgba.Hex(0xCC0000);
    private static readonly Rgba White = Rgba.Hex(0xFFFFFF);
    private static readonly Rgba Shadow = Rgba.Hex(0x555555);

    // Ось вращения: наклон 20° от вертикали в плоскости экрана (единичный вектор).
    private static readonly float Ax = MathF.Sin(TiltRad);
    private static readonly float Ay = MathF.Cos(TiltRad);
    // Свет сверху-слева, спереди (уже нормирован).
    private const float Lx = -0.45f, Ly = 0.6f, Lz = 0.66f;

    private RetroCanvas _canvas = null!;
    private int _w, _h, _cell;
    private float _floorY, _r, _peak, _lo, _hi;
    private RetroClock _clock;

    private float _p;                 // фаза прыжка в периодах: 0 — касание пола, 0.5 — верх
    private float _x, _vx, _spin, _spinRate, _sq = 1f;
    private float _rateBeat, _beatPeriod, _pendingPeriod, _sinceBeat;
    private bool _hasBeat, _beatPending, _active;
    private float _bass;
    private int _contacts;
    private float _speed = 1f;

    public string Id => "boing";

    /// <summary>Сколько раз шар коснулся пола.</summary>
    public int FloorContacts => _contacts;

    /// <summary>Время сцены, с (кратно 1/50).</summary>
    public float Time => _clock.Time;

    /// <summary>Центр шара по горизонтали, пиксели холста.</summary>
    public float BallX => _x;

    /// <summary>Центр шара по вертикали, пиксели холста.</summary>
    public float BallY => BallCenterY();

    /// <summary>Радиус шара (без сплющивания), пиксели холста.</summary>
    public float BallRadius => _r;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _speed = MathF.Max(0f, options.Speed);
        _canvas = new RetroCanvas(cols, rows, CanvasMode.HalfBlock);
        _w = _canvas.Width;
        _h = _canvas.Height;
        _cell = Math.Max(2, _h / 12);
        _floorY = Math.Clamp((int)(_h * FloorFrac), 1, _h - 1);
        _r = _h * RadiusFrac;
        _peak = _h * PeakFrac;
        float rMax = _r / SquashFlat;
        _lo = rMax;
        _hi = _w - rMax;
        if (_hi < _lo) _lo = _hi = _w * 0.5f;

        _clock = new RetroClock(Hz);
        _p = 0.5f;
        _x = _lo + rng.NextSingle() * (_hi - _lo);
        float dir = rng.Next(2) == 0 ? 1f : -1f;
        _vx = DriftFrac * _w * dir;
        _spinRate = SpinSpeed * dir;
        _spin = 0f;
        _sq = 1f;
        _bass = 0f;
        _rateBeat = 0f;
        _beatPeriod = BaseHop;
        _pendingPeriod = BaseHop;
        _sinceBeat = 0f;
        _hasBeat = false;
        _beatPending = false;
        _active = false;
        _contacts = 0;
    }

    public void Update(float dt)
    {
        int n = _clock.Advance(Math.Max(0f, dt) * _speed);
        for (int i = 0; i < n; i++) Tick();
    }

    public void OnAudio(in AudioFrame audio)
    {
        _active = audio.Active && audio.Bpm > 0f;
        _bass = audio.Active ? Math.Clamp(audio.Bass, 0f, 1f) : 0f;
        if (!_active)
        {
            _hasBeat = false;
            _beatPending = false;
            return;
        }
        if (audio.Beat)
        {
            _beatPending = true;
            _pendingPeriod = 60f / Math.Clamp(audio.Bpm, BpmMin, BpmMax);
        }
    }

    public void Draw(CharGrid grid)
    {
        var c = _canvas;
        c.Clear(Wall);
        DrawWall(c);
        DrawFloor(c);
        float cy = BallCenterY();
        DrawShadow(c, cy);
        DrawBall(c, cy);
        c.Present(grid);
    }

    /// <summary>Центр шара по вертикали: касание пола — низ шара на линии пола, в полёте — парабола.</summary>
    private float BallCenterY()
    {
        float u = _p;
        float h = 4f * _peak * u * (1f - u);
        return _floorY - _r * _sq - h;
    }

    private void Tick()
    {
        // Удар музыки: ошибка фазы e (в периодах, в (-0.5, 0.5]) гасится за один период — скоростью, без скачка.
        if (_beatPending)
        {
            _beatPending = false;
            float e = _p > 0.5f ? _p - 1f : _p;
            _beatPeriod = _pendingPeriod;
            _rateBeat = (1f - e) / _beatPeriod;
            _hasBeat = true;
            _sinceBeat = 0f;
        }
        _sinceBeat += TickDt;
        bool locked = _hasBeat && _sinceBeat < LockBeats * _beatPeriod;
        float rate = locked ? _rateBeat : 1f / BaseHop;

        float np = _p + rate * TickDt;
        if (np >= 1f)
        {
            np -= 1f;
            Touchdown();
        }
        _p = np;

        // Горизонталь: отскок от стенки меняет направление движения и направление вращения.
        _x += _vx * TickDt;
        if (_x < _lo) { _x = 2f * _lo - _x; _vx = MathF.Abs(_vx); _spinRate = -_spinRate; }
        else if (_x > _hi) { _x = 2f * _hi - _x; _vx = -MathF.Abs(_vx); _spinRate = -_spinRate; }

        _spin += _spinRate * TickDt;
        _sq += (1f - _sq) * MathF.Min(1f, SquashRecover * TickDt);
    }

    private void Touchdown()
    {
        _contacts++;
        // С музыкой сплющивание задаёт бас; без музыки — классические 0.85.
        _sq = _active ? 1f - (1f - SquashFlat) * _bass : SquashFlat;
    }

    private void DrawWall(RetroCanvas c)
    {
        int fl = (int)_floorY;
        for (int x = 0; x < _w; x += _cell)
            for (int y = 0; y < fl; y++) c.Set(x, y, GridColor);
        for (int y = 0; y < fl; y += _cell)
            for (int x = 0; x < _w; x++) c.Set(x, y, GridColor);
    }

    private void DrawFloor(RetroCanvas c)
    {
        // Пол — только линии на сером, без заливки.
        int fl = (int)_floorY;
        float depth = _h - fl;   // A: расстояние от горизонта до нижнего края
        // Поперечные линии: y = горизонт + A/z. Линии, которые слились бы друг с другом (<2 px), пропускаем.
        int last = int.MinValue;
        for (int z = FloorLines; z >= 2; z--)
        {
            int y = fl + (int)MathF.Round(depth / z);
            if (y >= _h || y - last < 2) continue;
            for (int x = 0; x < _w; x++) c.Set(x, y, GridColor);
            last = y;
        }
        // Продольные линии сходятся к точке схода на горизонте.
        float cx = _w * 0.5f;
        float step = _cell * 3f;
        int n = (int)(_w / (2f * step)) + 1;   // только линии, видимые у нижнего края холста
        for (int j = -n; j <= n; j++)
        {
            float xb = cx + j * step;
            int prev = (int)MathF.Floor(cx);
            for (int y = fl; y < _h; y++)
            {
                float t = (y - fl + 0.5f) / depth;
                int x = (int)MathF.Floor(cx + (xb - cx) * t);
                // Соединяем соседние строки отрезком, иначе крутые линии рвутся на точки.
                for (int xx = Math.Min(prev, x); xx <= Math.Max(prev, x); xx++) c.Set(xx, y, GridColor);
                prev = x;
            }
        }
    }

    private void DrawShadow(RetroCanvas c, float cy)
    {
        // Тёмный диск того же размера, что шар, смещён вправо на четверть диаметра (0.5 радиуса).
        float rx = _r / _sq, ry = _r * _sq;
        float sx = _x + 0.5f * _r;
        int x0 = Math.Max(0, (int)MathF.Floor(sx - rx));
        int x1 = Math.Min(_w - 1, (int)MathF.Ceiling(sx + rx));
        int y0 = Math.Max(0, (int)MathF.Floor(cy - ry));
        int y1 = Math.Min(_h - 1, (int)MathF.Ceiling(cy + ry));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float dx = (x + 0.5f - sx) / rx;
                float dy = (cy - (y + 0.5f)) / ry;
                if (dx * dx + dy * dy <= 1f) c.Set(x, y, Shadow);
            }
    }

    private void DrawBall(RetroCanvas c, float cy)
    {
        float rx = _r / _sq, ry = _r * _sq;   // сплющивание: по высоте меньше, по ширине больше
        float cx = _x;
        int x0 = Math.Max(0, (int)MathF.Floor(cx - rx));
        int x1 = Math.Min(_w - 1, (int)MathF.Ceiling(cx + rx));
        int y0 = Math.Max(0, (int)MathF.Floor(cy - ry));
        int y1 = Math.Min(_h - 1, (int)MathF.Ceiling(cy + ry));
        float cs = MathF.Cos(-_spin), sn = MathF.Sin(-_spin);

        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float dx = (x + 0.5f - cx) / rx;
                float dy = (cy - (y + 0.5f)) / ry;   // вверх — плюс
                float d2 = dx * dx + dy * dy;
                if (d2 > 1f) continue;
                float nz = MathF.Sqrt(1f - d2);

                // Точка на сфере → система шара: поворот на -spin вокруг оси A (формула Родригеса).
                float an = Ax * dx + Ay * dy;
                float qx = dx * cs + Ay * nz * sn + Ax * an * (1f - cs);
                float qy = dy * cs - Ax * nz * sn + Ay * an * (1f - cs);
                float qz = nz * cs + (Ax * dy - Ay * dx) * sn;

                // Широта — проекция на ось; долгота — угол в плоскости, перпендикулярной оси.
                float lat = Ax * qx + Ay * qy;
                float lon = MathF.Atan2(Ay * qx - Ax * qy, qz);
                int la = Math.Clamp((int)MathF.Floor((lat + 1f) * 0.5f * Lat), 0, Lat - 1);
                int li = Math.Clamp((int)MathF.Floor((lon + MathF.PI) / (2f * MathF.PI) * Lon), 0, Lon - 1);
                Rgba col = ((li + la) & 1) == 0 ? Red : White;

                float diff = Math.Max(0f, dx * Lx + dy * Ly + nz * Lz);
                c.Set(x, y, col.Scale(0.35f + 0.65f * diff));
            }
    }
}
