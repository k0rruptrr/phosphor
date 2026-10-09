using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// Туннель на таблицах: для каждого пикселя заранее посчитаны угол и обратное расстояние до центра (LUT вдвое
/// больше холста — центр гуляет сдвигом окна выборки). Текстура 64×64: шахматка 8×8 клеток в одной гамме C64,
/// контраст по u и по v, клетки мельчают к центру. Туман плавно уводит центр в чёрный на внутренних 25% радиуса.
/// Звук: удар — рывок скорости вперёд ×3 (затухает ~300 мс), бас — центр раскачивается сильнее,
/// высокие — подкрутка поворота.
/// </summary>
public sealed class TunnelScene : IScene, IAudioReactive
{
    private const int TexSize = 64;
    private const int TexMask = TexSize - 1;
    private const int CellSize = 8;               // сторона клетки шахматки, текселей
    private const float TickDt = 1f / 50f;        // логика на 50 Гц
    private const float BaseForward = 12f;        // текселей/с вперёд
    private const float BaseRotate = 6f;          // текселей/с поворот
    private const float BoostTime = 0.1f;         // экспонента рывка: ×3 гаснет за ~300 мс
    private const float FogRadius = 0.25f;        // внутренние 25% радиуса уходят в чёрный

    private static readonly float BoostMul = MathF.Exp(-TickDt / BoostTime);

    public string Id => "tunnel";

    private RetroCanvas _canvas = null!;
    private RetroClock _clock = new(50f);
    private float _speed = 1f;
    private Rgba[] _tex = Array.Empty<Rgba>();

    // LUT размером 2W×2H, центр туннеля в (W, H).
    private int _lw;
    private float[] _lutAng = Array.Empty<float>();   // угол / 2π, 0..1
    private float[] _lutInv = Array.Empty<float>();   // 32 / (расстояние / полуразмер холста)
    private float[] _lutFog = Array.Empty<float>();   // туман 0..1

    private float _fwd;      // движение вперёд, текселей (по модулю 64)
    private float _rot;      // поворот, текселей (по модулю 64)
    private float _boost;    // 1 в момент удара, затухает
    private float _bass, _tBass;
    private float _high, _tHigh;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _speed = MathF.Max(0f, options.Speed);
        _canvas = new RetroCanvas(cols, rows, CanvasMode.HalfBlock);
        _clock = new RetroClock(50f);
        _tex = BuildTexture();
        BuildLut(_canvas.Width, _canvas.Height);

        _fwd = 0f;
        _rot = 0f;
        _boost = 0f;
        _bass = _tBass = _high = _tHigh = 0f;
    }

    /// <summary>Шахматка 8×8 клеток: два тона одной гаммы C64 (синий и светло-синий → белый), квантуется Amiga12.</summary>
    private static Rgba[] BuildTexture()
    {
        var c64 = RetroPalettes.C64;
        var dark = RetroPalettes.Amiga12(c64[6]);                                  // тёмно-синий
        var light = RetroPalettes.Amiga12(Rgba.Lerp(c64[14], c64[1], 0.5f));     // светло-синий к белому
        var tex = new Rgba[TexSize * TexSize];
        for (int y = 0; y < TexSize; y++)
        for (int x = 0; x < TexSize; x++)
        {
            int cell = (x / CellSize + y / CellSize) & 1;
            tex[y * TexSize + x] = cell == 0 ? dark : light;
        }
        return tex;
    }

    private void BuildLut(int w, int h)
    {
        _lw = 2 * w;
        int lh = 2 * h;
        int n = _lw * lh;
        _lutAng = new float[n];
        _lutInv = new float[n];
        _lutFog = new float[n];
        float half = MathF.Min(w, h) * 0.5f;          // полуразмер холста: единица расстояния для 32/dist
        float fogR = FogRadius * half;
        for (int y = 0; y < lh; y++)
        for (int x = 0; x < _lw; x++)
        {
            float dx = x + 0.5f - w, dy = y + 0.5f - h;
            float dist = MathF.Max(0.5f, MathF.Sqrt(dx * dx + dy * dy));
            float a = MathF.Atan2(dy, dx) / MathF.Tau;
            if (a < 0f) a += 1f;
            float f = MathF.Min(1f, dist / fogR);
            f = f * f * (3f - 2f * f);
            int i = y * _lw + x;
            _lutAng[i] = a;
            _lutInv[i] = 32f * half / dist;
            _lutFog[i] = f;
        }
    }

    public void OnAudio(in AudioFrame audio)
    {
        _tBass = audio.Active ? audio.Bass : 0f;
        _tHigh = audio.Active ? audio.High : 0f;
        if (audio.Active && audio.Beat) _boost = 1f;
    }

    public void Update(float dt)
    {
        dt = Math.Clamp(dt, 0f, 0.1f);
        float k = 1f - MathF.Exp(-dt * 12f);
        _bass += (_tBass - _bass) * k;
        _high += (_tHigh - _high) * k;

        int ticks = _clock.Advance(dt * _speed);
        for (int i = 0; i < ticks; i++)
        {
            _fwd = (_fwd + BaseForward * (1f + 2f * _boost) * TickDt) % TexSize;
            _rot = (_rot + (BaseRotate + 2f * _high) * TickDt) % TexSize;
            _boost *= BoostMul;
        }

        int w = _canvas.Width, h = _canvas.Height;
        float t = _clock.Time;
        float amp = 0.3f + 0.5f * _bass;                   // бас раскачивает центр сильнее
        int ox = Math.Clamp((int)(w * 0.5f + w * 0.5f * amp * MathF.Sin(t * 0.7f)), 0, w);
        int oy = Math.Clamp((int)(h * 0.5f + h * 0.5f * amp * MathF.Sin(t * 0.5f + 1.3f)), 0, h);

        var px = _canvas.Pixels;
        for (int y = 0; y < h; y++)
        {
            int row = (y + oy) * _lw + ox;
            for (int x = 0; x < w; x++)
            {
                int li = row + x;
                float u = _lutAng[li] * TexSize + _rot;
                float v = _lutInv[li] + _fwd;
                int tu = (int)MathF.Floor(u) & TexMask;
                int tv = (int)MathF.Floor(v) & TexMask;
                px[y * w + x] = RetroPalettes.Amiga12(_tex[tv * TexSize + tu].Scale(_lutFog[li]));
            }
        }
    }

    public void Draw(CharGrid grid) => _canvas.Present(grid);
}
