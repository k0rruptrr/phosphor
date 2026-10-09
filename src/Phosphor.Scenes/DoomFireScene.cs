using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// Огонь из Doom (классический алгоритм PSX Doom, статья Fabien Sanglard).
/// Буфер интенсивностей 0..36, каждая ячейка сетки = 2 «пикселя» по вертикали.
/// Высота пламени задаётся долей экрана ("height"), а не числом строк, поэтому картинка
/// одинакова при любом разрешении. Вся случайность — только из rng, переданного в Init.
/// </summary>
public sealed class DoomFireScene : IScene, IAudioReactive
{
    private const int MaxIntensity = 36;
    private const int PaletteSize = 37;
    private const int ProbBits = 28;
    private const int ProbOne = 1 << ProbBits;
    private const float DefaultHeight = 0.5f;
    private const float MinHeight = 0.2f;
    private const float MaxHeight = 0.9f;
    private const float PreRunSeconds = 3f;
    private const float FlashSeconds = 0.15f;
    private const float FlashDecayMul = 0.3f;    // вспышка: затухание ×0.3 — пламя взмывает вверх, но не заливает экран
    private const float FlashStepsMul = 3f;      // вспышка: огонь «бежит» в 3 раза быстрее
    private const float FadeSeconds = 1f;
    private const float SmolderSeconds = 2f;
    private const float RekindleSeconds = 1f;
    private const float MinCycleSeconds = 25f;
    private const float CycleRangeSeconds = 20f;
    private const float RainbowDegPerSec = 6f;
    private const float DriftColsPerSec = 1f;
    private const int MinSegmentCols = 6;
    private const int SegmentColsRange = 15;     // сегмент 6..20 колонок
    private const float MinTauSeconds = 1f;
    private const float TauRangeSeconds = 2f;
    private const string AsciiRamp = " .:-=+*#%@";
    private const char BlockChar = '▀';

    /// <summary>Палитра оригинала PSX Doom: индекс = интенсивность.</summary>
    private static readonly uint[] OriginalHex =
    {
        0x070707, 0x1F0707, 0x2F0F07, 0x470F07, 0x571707, 0x671F07, 0x771F07, 0x8F2707,
        0x9F2F07, 0xAF3F07, 0xBF4707, 0xC74707, 0xDF4F07, 0xDF5707, 0xDF5707, 0xD75F07,
        0xD75F07, 0xD7670F, 0xCF6F0F, 0xCF770F, 0xCF7F0F, 0xCF8717, 0xC78717, 0xC78F17,
        0xC7971F, 0xBF9F1F, 0xBF9F1F, 0xBFA727, 0xBFA727, 0xBFAF2F, 0xB7AF2F, 0xB7B72F,
        0xB7B737, 0xCFCF6F, 0xDFDF9F, 0xEFEFC7, 0xFFFFFF,
    };

    private static readonly Rgba[] Original = Array.ConvertAll(OriginalHex, Rgba.Hex);

    private enum PaletteKind { Original, Tinted, Rainbow }

    private enum Phase { Burning, Fading, Smoldering, Rekindling }

    public string Id => "doomfire";

    private int _cols, _rows, _fireH;
    private Random _rng = new(0);
    private byte[] _fire = Array.Empty<byte>();
    private readonly Rgba[] _pal = new Rgba[PaletteSize];
    private PaletteKind _kind;
    private Rgba _tint = Rgba.White;
    private float _hue;
    private bool _ascii;
    private float _stepRate = 40f;
    private float _acc;
    private float _level;
    private Phase _phase;
    private float _phaseTime, _cycleLeft;
    private float _windLeft;
    private int _wind;
    private float _bass, _flashLeft;

    // Высота пламени: базовое среднее затухание на пиксель-строку и пороги, вычисляемые из него.
    private float _height;
    private float _baseNeed;
    private int _thinThr, _addBase, _addThr;

    // Источник (нижняя строка): сегменты колонок с плавающей интенсивностью.
    private int _segCount;
    private int[] _colSeg = Array.Empty<int>();
    private float[] _segCur = Array.Empty<float>();
    private float[] _segTarget = Array.Empty<float>();
    private float[] _segTau = Array.Empty<float>();
    private float _drift;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = cols;
        _rows = rows;
        _fireH = rows * 2;
        _rng = rng;

        _stepRate = MathF.Max(0f, options.GetFloat("stepsPerSec", 40f)) * MathF.Max(0f, options.Speed);
        _ascii = string.Equals(options.GetString("mode", "blocks"), "ascii", StringComparison.OrdinalIgnoreCase);

        _height = Math.Clamp(options.GetFloat("height", DefaultHeight), MinHeight, MaxHeight);
        _baseNeed = MaxIntensity / (_height * _fireH);

        string name = (options.Palette ?? "default").ToLowerInvariant();
        _kind = name switch
        {
            "green" or "amber" or "mono" => PaletteKind.Tinted,
            "rainbow" => PaletteKind.Rainbow,
            _ => PaletteKind.Original,
        };
        _tint = Palettes.Base(name);
        _hue = 0f;
        BuildPalette();

        _fire = new byte[cols * _fireH];
        _acc = 0f;
        _level = MaxIntensity;
        _phase = Phase.Burning;
        _phaseTime = 0f;
        _cycleLeft = MinCycleSeconds + rng.NextSingle() * CycleRangeSeconds;
        _windLeft = 3f + rng.NextSingle() * 5f;
        _wind = 0;
        _bass = 0f;
        _flashLeft = 0f;
        _drift = 0f;

        InitSegments(cols, rng);
        ComputeDecay();

        // Источник сразу горит; затем прогон ~3 с, чтобы огонь уже был на экране.
        FillSourceRow();
        int frames = (int)MathF.Round(PreRunSeconds * 60f);
        for (int i = 0; i < frames; i++) Update(1f / 60f);
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;

        if (_flashLeft > 0f) _flashLeft -= dt;

        if (_kind == PaletteKind.Rainbow)
        {
            _hue = (_hue + dt * RainbowDegPerSec) % 360f;
            BuildPalette();
        }

        _windLeft -= dt;
        if (_windLeft <= 0f)
        {
            _windLeft = 3f + _rng.NextSingle() * 5f;
            int roll = _rng.Next(6);
            _wind = roll == 0 ? -1 : roll == 1 ? 1 : 0;
        }

        AdvanceLife(dt);
        AdvanceSources(dt);
        ComputeDecay();

        _acc += dt * _stepRate * (_flashLeft > 0f ? FlashStepsMul : 1f);
        while (_acc >= 1f)
        {
            Step();
            _acc -= 1f;
        }
    }

    public void Draw(CharGrid grid)
    {
        int w = _cols;
        int cols = grid.Cols;
        var cells = grid.Cells;
        byte[] fire = _fire;

        if (_ascii)
        {
            for (int y = 0; y < grid.Rows; y++)
            {
                int top = 2 * y * w;
                for (int x = 0; x < cols; x++)
                {
                    int a = fire[top + x], b = fire[top + w + x];
                    int i = a > b ? a : b;
                    char ch = AsciiRamp[i * AsciiRamp.Length / (MaxIntensity + 1)];
                    cells[y * cols + x] = new Cell(ch, _pal[i], Rgba.Black);
                }
            }
        }
        else
        {
            for (int y = 0; y < grid.Rows; y++)
            {
                int top = 2 * y * w;
                for (int x = 0; x < cols; x++)
                {
                    int a = fire[top + x], b = fire[top + w + x];
                    cells[y * cols + x] = new Cell(BlockChar, _pal[a], _pal[b]);
                }
            }
        }
    }

    public void OnAudio(in AudioFrame audio)
    {
        if (!audio.Active)
        {
            _bass = 0f;
            _flashLeft = 0f;
            return;
        }
        _bass = audio.Bass;
        if (audio.Beat) _flashLeft = FlashSeconds;
    }

    /// <summary>Жизненный цикл огня: горит → гаснет за 1 с → тлеет 2 с → разгорается за 1 с.</summary>
    private void AdvanceLife(float dt)
    {
        switch (_phase)
        {
            case Phase.Burning:
                _level = MaxIntensity;
                _cycleLeft -= dt;
                if (_cycleLeft <= 0f)
                {
                    _phase = Phase.Fading;
                    _phaseTime = 0f;
                }
                break;

            case Phase.Fading:
                _phaseTime += dt;
                _level = MaxIntensity * (1f - MathF.Min(1f, _phaseTime / FadeSeconds));
                if (_phaseTime >= FadeSeconds)
                {
                    _phase = Phase.Smoldering;
                    _phaseTime = 0f;
                    _level = 0f;
                }
                break;

            case Phase.Smoldering:
                _level = 0f;
                _phaseTime += dt;
                if (_phaseTime >= SmolderSeconds)
                {
                    _phase = Phase.Rekindling;
                    _phaseTime = 0f;
                }
                break;

            case Phase.Rekindling:
                _phaseTime += dt;
                _level = MaxIntensity * MathF.Min(1f, _phaseTime / RekindleSeconds);
                if (_phaseTime >= RekindleSeconds)
                {
                    _phase = Phase.Burning;
                    _phaseTime = 0f;
                    _level = MaxIntensity;
                    _cycleLeft = MinCycleSeconds + _rng.NextSingle() * CycleRangeSeconds;
                }
                break;
        }
    }

    /// <summary>
    /// Пересчёт среднего затухания на пиксель-строку: need = 36 / (height × H) × (1 − 0.5 × bass).
    /// Вспышка (Beat) снижает затухание до ×0.3. Затем превращаем need в пороги для rng в Step:
    /// need &lt; 0.5 — (r &amp; 1) применяется с вероятностью need/0.5; иначе (r &amp; 1) + целая часть
    /// (need − 0.5) + единица с вероятностью дробной части. Среднее затухание = need.
    /// </summary>
    private void ComputeDecay()
    {
        float need = _baseNeed * (1f - 0.5f * Math.Clamp(_bass, 0f, 1f));
        if (_flashLeft > 0f) need *= FlashDecayMul;

        if (need < 0.5f)
        {
            _thinThr = (int)MathF.Round(need / 0.5f * ProbOne);
            _addBase = 0;
            _addThr = 0;
        }
        else
        {
            float m = need - 0.5f;
            int b = (int)MathF.Floor(m);
            _thinThr = ProbOne;
            _addBase = b;
            _addThr = (int)((m - b) * ProbOne);
        }
    }

    /// <summary>Один шаг распространения огня (как spreadFire в PSX Doom).</summary>
    private void Step()
    {
        int w = _cols, h = _fireH;
        byte[] fire = _fire;

        FillSourceRow();

        int thin = _thinThr, addBase = _addBase, addThr = _addThr;
        int wind = _wind;

        for (int x = 0; x < w; x++)
        {
            for (int y = 1; y < h; y++)
            {
                int s = y * w + x;
                int p = fire[s];
                if (p == 0)
                {
                    fire[s - w] = 0;
                    continue;
                }

                // Один вызов rng на пиксель: два младших бита — сдвиг и (r & 1), остальные — вероятности.
                int u = _rng.Next(1 << 30);
                int r = u & 3;
                int t = u >> 2;

                int dec = r & 1;
                if (t >= thin) dec = 0;
                dec += addBase + (t < addThr ? 1 : 0);

                int rowStart = y * w;
                int dst = s - r + 1 + wind;
                if (dst < rowStart) dst = rowStart;
                else if (dst >= rowStart + w) dst = rowStart + w - 1;

                int v = p - dec;
                fire[dst - w] = (byte)(v < 0 ? 0 : v);
            }
        }
    }

    /// <summary>Нижняя строка = интенсивность сегментов, сдвинутая на drift, × фаза жизни огня.</summary>
    private void FillSourceRow()
    {
        int w = _cols;
        int bottom = (_fireH - 1) * w;
        // Вспышка: нижний ряд сразу раскаляется (подброс жара), затем снова по фазе жизни огня.
        float k = _flashLeft > 0f ? 1f : _level / MaxIntensity;
        int shift = (int)_drift;
        byte[] fire = _fire;

        for (int x = 0; x < w; x++)
        {
            int idx = x - shift;
            if (idx < 0) idx += w;
            float v = _segCur[_colSeg[idx]] * k;
            fire[bottom + x] = (byte)Math.Clamp((int)MathF.Round(v), 0, MaxIntensity);
        }
    }

    /// <summary>Сегменты (6–20 колонок) плавно тянутся к целевым значениям: горячие 36 или холодные 28–32.</summary>
    private void AdvanceSources(float dt)
    {
        for (int s = 0; s < _segCount; s++)
        {
            float cur = _segCur[s], tg = _segTarget[s];
            cur += (tg - cur) * MathF.Min(1f, dt / _segTau[s]);
            if (MathF.Abs(tg - cur) < 0.5f)
            {
                _segTarget[s] = PickTarget(_rng);
                _segTau[s] = PickTau(_rng);
            }
            _segCur[s] = cur;
        }
        _drift = (_drift + dt * DriftColsPerSec) % _cols;
    }

    private void InitSegments(int cols, Random rng)
    {
        _colSeg = new int[cols];
        _segCur = new float[cols];
        _segTarget = new float[cols];
        _segTau = new float[cols];
        _segCount = 0;

        int x = 0;
        while (x < cols)
        {
            int len = MinSegmentCols + rng.Next(SegmentColsRange + 1);
            int seg = _segCount++;
            float t = PickTarget(rng);
            _segTarget[seg] = t;
            _segCur[seg] = t;
            _segTau[seg] = PickTau(rng);
            for (int i = 0; i < len && x < cols; i++, x++) _colSeg[x] = seg;
        }
    }

    private static float PickTarget(Random rng) =>
        rng.Next(2) == 0 ? MaxIntensity : 28 + rng.Next(5);

    private static float PickTau(Random rng) => MinTauSeconds + rng.NextSingle() * TauRangeSeconds;

    private void BuildPalette()
    {
        _pal[0] = Rgba.Black;
        for (int i = 1; i < PaletteSize; i++)
        {
            Rgba o = Original[i];
            _pal[i] = _kind switch
            {
                PaletteKind.Tinted => _tint.Scale(ColorMath.MaxChannel(o) / 255f),
                PaletteKind.Rainbow => ColorMath.ShiftHue(o, _hue),
                _ => o,
            };
        }
    }

    private static class ColorMath
    {
        public static float MaxChannel(Rgba c) => MathF.Max(c.R, MathF.Max(c.G, c.B));

        public static Rgba ShiftHue(Rgba c, float shift)
        {
            ToHsv(c, out float h, out float s, out float v);
            return Rgba.Hsv(h + shift, s, v);
        }

        public static void ToHsv(Rgba c, out float h, out float s, out float v)
        {
            float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
            float max = MathF.Max(r, MathF.Max(g, b));
            float min = MathF.Min(r, MathF.Min(g, b));
            float d = max - min;
            v = max;
            s = max <= 0f ? 0f : d / max;
            if (d <= 0f) h = 0f;
            else if (max == r) h = 60f * ((g - b) / d);
            else if (max == g) h = 60f * ((b - r) / d + 2f);
            else h = 60f * ((r - g) / d + 4f);
            if (h < 0f) h += 360f;
        }
    }
}
