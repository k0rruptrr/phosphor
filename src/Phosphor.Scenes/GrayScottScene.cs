using System.Runtime.CompilerServices;
using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// Реакция-диффузия Грея-Скотта (karlsims.com/rd.html) на торе. Поле в полублоках: W = cols, H = rows × 2.
/// Каждая ячейка экрана рисуется символом '▀': верх — пиксель поля 2·row, низ — 2·row+1.
/// Параметры (f, k) плавно дрейфуют по каталогу режимов (mrob.com/pub/comp/xmorphia), узор меняет характер.
/// Часы сцены идут с множителем options.Speed (при Speed = 0 сцена стоит).
/// </summary>
public sealed class GrayScottScene : IScene, IAudioReactive, IHandoffReceiver
{
    private const float Da = 1.0f;
    private const float Db = 0.5f;
    private const float Dt = 1.0f;

    private const float StepsPerSecond = 300f;      // × options.Speed
    private const int MaxStepsPerUpdate = 64;       // защита от лавины шагов при больших dt или скорости

    private const float HoldMin = 40f;              // с между переходами режимов
    private const float HoldMax = 80f;
    private const float TransitionSeconds = 8f;     // плавный переход (smoothstep)

    private const float DeadThreshold = 0.002f;     // средняя B ниже — поле вымерло
    private const float FlatStd = 0.03f;            // std(B) ниже — поле однородный «туман» (тоже мёртвое)
    private const int DeadSamples = 3;              // выборка раз в секунду → окно 3 с
    private const int SeedDropsMin = 8;            // полный посев: 8..20 капель (старт и возрождение)
    private const int SeedDropsSpan = 13;

    private const float BassFeed = 0.002f;          // дыхание f от баса

    private const int LutSize = 256;

    /// <summary>Каталог режимов (f, k) из mrob.com/pub/comp/xmorphia. Первый — стартовый.</summary>
    private static readonly (float F, float K)[] Regimes =
    {
        (0.0367f, 0.0649f), // митоз / пятна (старт)
        (0.0545f, 0.062f),  // кораллы (Пирсон)
        (0.030f,  0.062f),  // пятна (Пирсон)
        (0.025f,  0.060f),  // пузыри (Пирсон)
        (0.029f,  0.057f),  // лабиринт / «отпечатки пальцев»
    };

    private enum PaletteKind { Biolum, Moss, Rainbow }

    public string Id => "grayscott";

    /// <summary>Текущий параметр подпитки f (с учётом дыхания от баса). Обновляется в Update.</summary>
    public float F { get; private set; }

    /// <summary>Текущий параметр убыли k. Обновляется в Update.</summary>
    public float K { get; private set; }

    /// <summary>Сколько капель по ударам добавлено с начала сцены (для тестов).</summary>
    public int BeatDrops { get; private set; }

    private int _cols, _rows, _w, _h, _n;
    private Random _rng = new(0);
    private float _speed = 1f;

    private float[] _a = Array.Empty<float>();
    private float[] _b = Array.Empty<float>();
    private float[] _na = Array.Empty<float>();
    private float[] _nb = Array.Empty<float>();

    private float _acc;

    private float[] _deadRing = Array.Empty<float>();
    private float[] _flatRing = Array.Empty<float>();
    private int _deadCount, _deadPos;
    private float _deadSampleLeft;

    private int _regime;
    private float _baseF, _baseK;
    private float _holdLeft;
    private bool _transitioning;
    private int _target;
    private float _fFrom, _kFrom, _fTo, _kTo, _transT;

    private float _bassF;
    private bool _beatPending;

    private PaletteKind _kind;
    private Rgba[] _lut = Array.Empty<Rgba>();
    private float _hue;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = cols;
        _rows = rows;
        _w = cols;
        _h = rows * 2;
        _n = _w * _h;
        _rng = rng;
        _speed = MathF.Max(0f, options.Speed);

        _a = new float[_n];
        _b = new float[_n];
        _na = new float[_n];
        _nb = new float[_n];
        _deadRing = new float[DeadSamples];
        _flatRing = new float[DeadSamples];

        _kind = KindOf(options.Palette);
        _lut = new Rgba[LutSize];
        BuildPalette(options.Palette);
        _hue = 0f;

        _acc = 0f;
        _deadCount = 0;
        _deadPos = 0;
        _deadSampleLeft = 1f;

        _regime = 0;
        _baseF = Regimes[0].F;
        _baseK = Regimes[0].K;
        _holdLeft = NextHold();
        _transitioning = false;
        _target = 0;
        _fFrom = _kFrom = _fTo = _kTo = 0f;
        _transT = 0f;

        _bassF = 0f;
        _beatPending = false;
        BeatDrops = 0;

        F = _baseF;
        K = _baseK;

        SeedFull();
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;

        if (_beatPending)
        {
            _beatPending = false;
            for (int d = 0; d < 2; d++)
            {
                BeatDrop(_rng.Next(_w), _rng.Next(_h), 5 + _rng.Next(4));
                BeatDrops++;
            }
        }

        float sdt = dt * _speed;
        AdvanceRegime(sdt);

        float f = _baseF + _bassF;
        float k = _baseK;
        F = f;
        K = k;

        _acc += sdt * StepsPerSecond;
        int steps = 0;
        while (_acc >= 1f && steps < MaxStepsPerUpdate)
        {
            _acc -= 1f;
            steps++;
            Step(f, k);
        }
        if (_acc >= 1f) _acc = 0f;

        _deadSampleLeft -= sdt;
        while (_deadSampleLeft <= 0f)
        {
            _deadSampleLeft += 1f;
            SampleDead();
        }

        if (_kind == PaletteKind.Rainbow)
        {
            _hue = (_hue + 12f * sdt) % 360f;
            BuildRainbowLut();
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
        if (!audio.Active)
        {
            _bassF = 0f;
            return;
        }
        _bassF = BassFeed * audio.Bass;
        if (audio.Beat) _beatPending = true;
    }

    /// <summary>Доля пикселей поля (W×H) с B &gt; threshold. Для тестов.</summary>
    public float CoverageAbove(float threshold)
    {
        if (_n == 0) return 0f;
        int count = 0;
        for (int i = 0; i < _n; i++)
            if (_b[i] > threshold) count++;
        return (float)count / _n;
    }

    /// <summary>Стандартное отклонение B по полю. Для тестов.</summary>
    public float FieldStdB()
    {
        if (_n == 0) return 0f;
        double sum = 0, sq = 0;
        for (int i = 0; i < _n; i++) { sum += _b[i]; sq += _b[i] * _b[i]; }
        double mean = sum / _n;
        return (float)Math.Sqrt(Math.Max(0, sq / _n - mean * mean));
    }

    /// <summary>
    /// Эстафета: плесень прорастает из силуэта прошлой сцены. Пиксели поля под ячейками previous с яркостью
    /// &gt; 0.3 получают B = 0.9, A = 0.2; остальное — как после Init. Ярких ячеек &lt; 1% — обычный посев.
    /// </summary>
    public void Seed(CharGrid previous)
    {
        int bright = 0;
        foreach (var c in previous.Cells)
            if (HandoffLuma(c) > 0.3f) bright++;
        if (bright < 0.01f * previous.Cells.Length)
        {
            SeedFull();
            return;
        }

        int pc = previous.Cols, pr = previous.Rows;
        Array.Fill(_a, 1f);
        Array.Clear(_b);
        for (int y = 0; y < _h; y++)
        {
            int sy = Math.Min(pr - 1, (int)((y + 0.5f) * pr / _h));
            for (int x = 0; x < _w; x++)
            {
                int sx = Math.Min(pc - 1, (int)((x + 0.5f) * pc / _w));
                if (HandoffLuma(previous[sx, sy]) <= 0.3f) continue;
                int i = y * _w + x;
                _a[i] = 0.2f;
                _b[i] = 0.9f;
            }
        }
    }

    /// <summary>Яркость ячейки для эстафеты: max(Fg) у непробельного символа, иначе max(Bg), 0..1.</summary>
    private static float HandoffLuma(Cell c)
    {
        var k = c.Ch != ' ' ? c.Fg : c.Bg;
        return MathF.Max(k.R, MathF.Max(k.G, k.B)) / 255f;
    }

    private void Step(float f, float k)
    {
        StepField(_a, _b, _na, _nb, _w, _h, f, k);
        (_a, _na) = (_na, _a);
        (_b, _nb) = (_nb, _b);
    }

    /// <summary>Один шаг Грея-Скотта на торе. Все входные массивы w×h, выходные — отдельные буферы.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe void StepField(float[] a, float[] b, float[] na, float[] nb, int w, int h, float f, float k)
    {
        // Указатели без проверок границ: горячий цикл в Debug-сборке иначе не укладывается в бюджет.
        // Соседи берутся с переносом через границы тора (xl/xr, yu/yd); лапласиан 3×3: центр −1, кресты 0.2, диагонали 0.05.
        float fk = k + f;
        fixed (float* pa = a, pb = b, pna = na, pnb = nb)
        {
            for (int y = 0; y < h; y++)
            {
                int ro = y * w;
                int ru = (y == 0 ? h - 1 : y - 1) * w;
                int rd = (y == h - 1 ? 0 : y + 1) * w;
                for (int x = 0; x < w; x++)
                {
                    int xl = x == 0 ? w - 1 : x - 1;
                    int xr = x == w - 1 ? 0 : x + 1;
                    int c = ro + x, l = ro + xl, r = ro + xr, u = ru + x, d = rd + x;
                    int ul = ru + xl, ur = ru + xr, dl = rd + xl, dr = rd + xr;
                    float ac = pa[c], bc = pb[c];
                    float lapA = -ac + 0.2f * (pa[l] + pa[r] + pa[u] + pa[d]) + 0.05f * (pa[ul] + pa[ur] + pa[dl] + pa[dr]);
                    float lapB = -bc + 0.2f * (pb[l] + pb[r] + pb[u] + pb[d]) + 0.05f * (pb[ul] + pb[ur] + pb[dl] + pb[dr]);
                    float reaction = ac * bc * bc;
                    // Da = 1, Db = 0.5, dt = 1: множители опущены (см. константы выше).
                    float nA = ac + lapA - reaction + f * (1f - ac);
                    float nB = bc + 0.5f * lapB + reaction - fk * bc;
                    pna[c] = nA < 0f ? 0f : nA > 1f ? 1f : nA;
                    pnb[c] = nB < 0f ? 0f : nB > 1f ? 1f : nB;
                }
            }
        }
    }

    /// <summary>Дрейф параметров: выдержка 40–80 с, затем плавный переход 8 с к другому режиму каталога.</summary>
    private void AdvanceRegime(float sdt)
    {
        if (_transitioning)
        {
            _transT += sdt;
            float u = _transT / TransitionSeconds;
            if (u >= 1f)
            {
                _transitioning = false;
                _regime = _target;
                _baseF = _fTo;
                _baseK = _kTo;
                _holdLeft = NextHold();
            }
            else
            {
                float s = u * u * (3f - 2f * u);
                _baseF = _fFrom + (_fTo - _fFrom) * s;
                _baseK = _kFrom + (_kTo - _kFrom) * s;
            }
        }
        else
        {
            _holdLeft -= sdt;
            if (_holdLeft <= 0f)
            {
                _target = (_regime + 1 + _rng.Next(Regimes.Length - 1)) % Regimes.Length;
                _fFrom = _baseF;
                _kFrom = _baseK;
                _fTo = Regimes[_target].F;
                _kTo = Regimes[_target].K;
                _transT = 0f;
                _transitioning = true;
            }
        }
    }

    private float NextHold() => HoldMin + _rng.NextSingle() * (HoldMax - HoldMin);

    /// <summary>
    /// Каждую секунду — средняя B и std(B). Поле мертво, если в каждом из 3 последних замеров средняя B &lt; порога
    /// ИЛИ std(B) &lt; FlatStd (однородный туман). Тогда — полный новый посев.
    /// </summary>
    private void SampleDead()
    {
        float sum = 0f, sq = 0f;
        for (int i = 0; i < _n; i++)
        {
            float v = _b[i];
            sum += v;
            sq += v * v;
        }
        float mean = sum / _n;
        _deadRing[_deadPos] = mean;
        _flatRing[_deadPos] = MathF.Sqrt(MathF.Max(0f, sq / _n - mean * mean));
        _deadPos = (_deadPos + 1) % DeadSamples;
        if (_deadCount < DeadSamples) _deadCount++;
        if (_deadCount < DeadSamples) return;

        for (int i = 0; i < DeadSamples; i++)
            if (!(_deadRing[i] < DeadThreshold || _flatRing[i] < FlatStd)) return;

        SeedFull();
        _deadCount = 0;
        _deadPos = 0;
    }

    /// <summary>Полный посев: A = 1 везде, B = 0, затем 8..20 капель радиусом 4..9 в случайных местах.</summary>
    private void SeedFull()
    {
        Array.Fill(_a, 1f);
        Array.Clear(_b);
        int drops = SeedDropsMin + _rng.Next(SeedDropsSpan);
        for (int i = 0; i < drops; i++)
        {
            int cx = _rng.Next(_w), cy = _rng.Next(_h);
            int r = 4 + _rng.Next(6);
            SeedDrop(cx, cy, r);
        }
    }

    /// <summary>Капля посева: внутри A = 0.5, B = 0.25, каждое значение с шумом ±0.05 от _rng; перенос через границы тора.</summary>
    private void SeedDrop(int cx, int cy, int r)
    {
        int r2 = r * r;
        for (int dy = -r; dy <= r; dy++)
        {
            int row = Wrap(cy + dy, _h) * _w;
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dy * dy > r2) continue;
                int i = row + Wrap(cx + dx, _w);
                _a[i] = Math.Clamp(0.5f + Noise(), 0f, 1f);
                _b[i] = Math.Clamp(0.25f + Noise(), 0f, 1f);
            }
        }
    }

    /// <summary>Шум равномерно в ±0.05.</summary>
    private float Noise() => (_rng.NextSingle() * 2f - 1f) * 0.05f;

    /// <summary>Капля по удару: внутри A = 0, B = 1, радиусом r в пикселях поля, с переносом через границы тора.</summary>
    private void BeatDrop(int cx, int cy, int r)
    {
        int r2 = r * r;
        for (int dy = -r; dy <= r; dy++)
        {
            int row = Wrap(cy + dy, _h) * _w;
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dy * dy > r2) continue;
                int i = row + Wrap(cx + dx, _w);
                _a[i] = 0f;
                _b[i] = 1f;
            }
        }
    }

    private static int Wrap(int v, int n)
    {
        v %= n;
        return v < 0 ? v + n : v;
    }

    private Rgba ColorAt(int i)
    {
        float v = _b[i] * 3f;
        int idx = v <= 0f ? 0 : v >= 1f ? LutSize - 1 : (int)(v * (LutSize - 1));
        return _lut[idx];
    }

    private static PaletteKind KindOf(string? name) => (name ?? string.Empty).ToLowerInvariant() switch
    {
        "green" or "amber" or "mono" => PaletteKind.Moss,
        "rainbow" => PaletteKind.Rainbow,
        _ => PaletteKind.Biolum,
    };

    private void BuildPalette(string? name)
    {
        switch (_kind)
        {
            case PaletteKind.Moss:
                {
                    var b = Palettes.Base(name);
                    FillGradient(_lut, new (float T, Rgba C)[]
                    {
                        (0f, Rgba.Black),
                        (0.45f, b.Scale(0.45f)),
                        (0.8f, b),
                        (1f, Rgba.Lerp(b, Rgba.White, 0.6f)),
                    });
                    break;
                }
            case PaletteKind.Rainbow:
                BuildRainbowLut();
                break;
            default:
                FillGradient(_lut, new (float T, Rgba C)[]
                {
                    (0f, Rgba.Black),
                    (0.3f, Rgba.Hex(0x2A0A3A)),
                    (0.6f, Rgba.Hex(0xB0308A)),
                    (1f, Rgba.Hex(0xFFD6F0)),
                });
                break;
        }
    }

    private void BuildRainbowLut()
    {
        for (int i = 0; i < LutSize; i++)
        {
            float t = i / (float)(LutSize - 1);
            _lut[i] = Rgba.Hsv(280f - 240f * t + _hue, 0.8f, t);
        }
    }

    private static void FillGradient(Rgba[] lut, (float T, Rgba C)[] stops)
    {
        for (int i = 0; i < lut.Length; i++)
        {
            float t = i / (float)(lut.Length - 1);
            int s = 0;
            while (s < stops.Length - 2 && t > stops[s + 1].T) s++;
            var (t0, c0) = stops[s];
            var (t1, c1) = stops[s + 1];
            float u = t1 > t0 ? Math.Clamp((t - t0) / (t1 - t0), 0f, 1f) : 1f;
            lut[i] = Rgba.Lerp(c0, c1, u);
        }
    }
}
