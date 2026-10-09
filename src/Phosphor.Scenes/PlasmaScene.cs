using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// Плазма, как на VGA: сумма синусов от (x, y, радиус от плавающей точки, t) даёт индекс в 256-цветную таблицу.
/// Четыре палитры (огонь, C64, кислота, океан) сменяются раз в ~20 с плавным переходом за 2 с,
/// таблица всё время циклически сдвигается (palette cycling). Звук: удар — рывок сдвига палитры,
/// бас — «дыхание» пространственной частоты, уровень — скорость.
/// </summary>
public sealed class PlasmaScene : IScene, IAudioReactive
{
    private const int PaletteCount = 4;
    private const int TableSize = 256;
    private const int SinSize = 1024;             // LUT синуса, степень двойки для быстрого &
    private const float PaletteInterval = 20f;    // смена палитры, с
    private const float BlendTime = 2f;           // перекрёстный переход между таблицами, с
    private const float CycleStep = 0.1f;         // базовый сдвиг таблицы за тик (≈5 индексов/с)
    private const float KickMin = 40f, KickMax = 60f;
    private const float KickDecay = 0.35f;        // доля рывка, сгорающая за тик (≈10 тиков ≈ 200 мс)

    public string Id => "plasma";

    private RetroCanvas _canvas = null!;
    private RetroClock _clock = new(50f);
    private Random _rng = new(0);
    private float _speed = 1f;

    private float[] _sin = Array.Empty<float>();
    private Rgba[] _pal = Array.Empty<Rgba>();    // PaletteCount таблиц по TableSize, уже квантованы Amiga12
    private Rgba[] _cur = Array.Empty<Rgba>();    // текущая (смешанная) таблица

    private int _palFrom, _palTo;
    private float _palTimer, _blend;
    private float _cycle;                         // сдвиг таблицы, индексы
    private float _kick;                          // рывок сдвига, ещё не отыгранный, индексы

    private float _level, _bass;                  // сглаженный звук
    private float _tLevel, _tBass;                // цели из последнего кадра звука

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _rng = rng;
        _speed = MathF.Max(0f, options.Speed);
        _canvas = new RetroCanvas(cols, rows, CanvasMode.HalfBlock);
        _clock = new RetroClock(50f);

        _sin = new float[SinSize];
        for (int i = 0; i < SinSize; i++) _sin[i] = MathF.Sin(i * MathF.Tau / SinSize);

        var fire = new[]
        {
            Rgba.Hex(0x000000), Rgba.Hex(0x800000), Rgba.Hex(0xFF4000), Rgba.Hex(0xFFC000),
            Rgba.Hex(0xFFFFFF), Rgba.Hex(0xFFC000), Rgba.Hex(0x800000), Rgba.Hex(0x200000),
        };
        var acid = new[]
        {
            Rgba.Hex(0x003300), Rgba.Hex(0x00FF00), Rgba.Hex(0xFFFF00), Rgba.Hex(0xFF00FF), Rgba.Hex(0x00FFFF),
        };
        var ocean = new[]
        {
            Rgba.Hex(0x000020), Rgba.Hex(0x0040A0), Rgba.Hex(0x40C0FF), Rgba.Hex(0xE0FFFF), Rgba.Hex(0x0040A0),
        };
        _pal = new Rgba[PaletteCount * TableSize];
        BuildTable(0, fire);
        BuildTable(1, RetroPalettes.C64);
        BuildTable(2, acid);
        BuildTable(3, ocean);
        _cur = new Rgba[TableSize];

        _palFrom = 0;
        _palTo = 0;
        _palTimer = PaletteInterval;
        _blend = 1f;
        _cycle = 0f;
        _kick = 0f;
        _level = _bass = _tLevel = _tBass = 0f;
    }

    private void BuildTable(int p, ReadOnlySpan<Rgba> stops)
    {
        for (int i = 0; i < TableSize; i++)
            _pal[p * TableSize + i] = RetroPalettes.Amiga12(RetroPalettes.Gradient(stops, i / (float)TableSize));
    }

    public void OnAudio(in AudioFrame audio)
    {
        _tLevel = audio.Active ? audio.Level : 0f;
        _tBass = audio.Active ? audio.Bass : 0f;
        if (audio.Active && audio.Beat) _kick = KickMin + _rng.NextSingle() * (KickMax - KickMin);
    }

    public void Update(float dt)
    {
        dt = Math.Clamp(dt, 0f, 0.1f);
        float k = 1f - MathF.Exp(-dt * 12f);
        _level += (_tLevel - _level) * k;
        _bass += (_tBass - _bass) * k;

        _palTimer -= dt;
        if (_palTimer <= 0f)
        {
            _palFrom = _palTo;
            _palTo = (_palTo + 1) % PaletteCount;
            _blend = 0f;
            _palTimer += PaletteInterval;
        }
        _blend = MathF.Min(1f, _blend + dt / BlendTime);

        int ticks = _clock.Advance(dt * _speed * (1f + 0.8f * _level));
        for (int i = 0; i < ticks; i++)
        {
            float push = _kick * KickDecay;
            _cycle = (_cycle + CycleStep + push) % TableSize;
            _kick -= push;
        }

        BuildCurrentTable();
        Render();
    }

    private void BuildCurrentTable()
    {
        float b = _blend * _blend * (3f - 2f * _blend);   // smoothstep
        int a0 = _palFrom * TableSize, a1 = _palTo * TableSize;
        for (int i = 0; i < TableSize; i++)
            _cur[i] = RetroPalettes.Amiga12(Rgba.Lerp(_pal[a0 + i], _pal[a1 + i], b));
    }

    private void Render()
    {
        float t = _clock.Time;
        int w = _canvas.Width, h = _canvas.Height;
        float m = 1f + 0.4f * _bass * _bass;               // бас «дышит» частотой ×1..1.4; квадрат — резкий пик, тихий хвост
        float cx = w * (0.5f + 0.35f * Sin(t * 0.12f));
        float cy = h * (0.5f + 0.35f * Sin(t * 0.17f + 1f));
        int shift = (int)_cycle;
        var px = _canvas.Pixels;

        for (int y = 0; y < h; y++)
        {
            float fy = y * 0.07f * m - t * 0.25f;
            float dy = y - cy;
            for (int x = 0; x < w; x++)
            {
                float dx = x - cx;
                float s1 = Sin(x * 0.09f * m + t * 0.3f);
                float s2 = Sin(fy);
                float s3 = Sin((x + y) * 0.05f * m + t * 0.35f);
                float s4 = Sin(MathF.Sqrt(dx * dx + dy * dy) * 0.12f * m - t * 0.5f);
                float v = (s1 + s2 + s3 + s4) * 0.25f;     // −1..1
                int idx = ((int)(v * 127.5f + 127.5f) + shift) & (TableSize - 1);
                px[y * w + x] = _cur[idx];
            }
        }
    }

    public void Draw(CharGrid grid) => _canvas.Present(grid);

    private float Sin(float a) => _sin[(int)MathF.Floor(a * (SinSize / MathF.Tau)) & (SinSize - 1)];
}
