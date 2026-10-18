using System.Runtime.CompilerServices;
using Phosphor.Core;

[assembly: InternalsVisibleTo("Phosphor.Scenes.Tests")]

namespace Phosphor.Scenes;

/// <summary>
/// Визуализатор сортировок: столбики перемешанного массива 1..N, алгоритм переставляет их на глазах.
/// Сравниваемая пара — белая, перестановка — красная, отсортированная часть — зелёная. Алгоритмы по кругу:
/// пузырёк, вставки, выбор, Шелл, слияние, быстрая, пирамида, поразрядная LSD, шейкер и изредка bogo на 6 элементах.
/// После сортировки зелёная волна проверки слева направо, затем перемешивание. Звук: удар ускоряет шаги
/// алгоритма (до ×5 в момент удара, затухает за ~150 мс).
/// </summary>
public sealed class SortingScene : IScene, IAudioReactive, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec SizeSpec = new("size", SceneParamKind.Int, 128f, 32f, 256f, "размер массива, элементов");
    public static readonly SceneParamSpec SpeedSpec = new("speed", SceneParamKind.Float, 1f, 0.2f, 5f, "скорость шагов, ×");
    public static readonly SceneParamSpec AlgorithmsSpec = new("algorithms", SceneParamKind.Choice, 0f, 0f, 2f, "набор алгоритмов",
        new[] { "all", "fast", "slow" });
    private static readonly SceneParamSpec[] ParamList = { SizeSpec, SpeedSpec, AlgorithmsSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    public string Id => "sorting";

    /// <summary>Алгоритмы сортировки и bogo. Порядок значения не имеет, важны только имена в наборах.</summary>
    internal enum Algo { Bubble, Insertion, Selection, Shell, Merge, Quick, Heap, Radix, Cocktail, Bogo }

    private enum Phase { Shuffle, Sort, Wave }

    private static readonly string[] AlgoNames =
        { "BUBBLE", "INSERTION", "SELECTION", "SHELL", "MERGE", "QUICKSORT", "HEAPSORT", "RADIX LSD", "COCKTAIL", "BOGO" };
    private static readonly string[] PhaseNames = { "SHUFFLE", "SORTING", "VERIFY" };

    private static readonly Algo[] AllOrder =
        { Algo.Bubble, Algo.Insertion, Algo.Selection, Algo.Shell, Algo.Merge, Algo.Quick, Algo.Heap, Algo.Radix, Algo.Cocktail, Algo.Bogo };
    private static readonly Algo[] FastOrder = { Algo.Quick, Algo.Merge, Algo.Heap, Algo.Shell, Algo.Radix };
    private static readonly Algo[] SlowOrder = { Algo.Insertion, Algo.Selection, Algo.Cocktail, Algo.Bubble };

    private const int MaxN = 256;
    private const int BogoN = 6;
    private const float BogoRate = 540f;           // 6 элементов: ~720 перестановок по 5 обменов, около 7 с
    private const float TargetQuadraticSec = 6f;   // O(n²): пузырёк на 128 элементах — около 6 с
    private const float TargetLogSec = 18f;        // O(n log n): быстрая сортировка на 128 — около 12 с
    private const float ShuffleSec = 1.5f;
    private const float WaveSec = 2f;
    private const float PulseGain = 4f;           // в момент удара шаги ×5
    private const float MaxAcc = 4096f;           // предел накопленных шагов за кадр
    private const int Margin = 3;                 // отступ текста от левого края: экранные эффекты обрезают крайние колонки
    private const int WaveFrontLen = 7;           // сколько столбиков яркого фронта у волны проверки

    private static readonly Rgba Red = new(255, 70, 60);
    private static readonly Rgba WaveFront = new(150, 255, 175);   // бегущий фронт проверки
    private static readonly Rgba Dim = new(110, 135, 155);
    private static readonly Rgba Bright = new(200, 225, 240);
    private static readonly Rgba Rule = new(40, 70, 90);

    private readonly Rgba[] _grad = BuildGradient(new Rgba(34, 58, 84), new Rgba(130, 190, 235));
    private readonly Rgba[] _gradGreen = BuildGradient(new Rgba(20, 40, 28), new Rgba(56, 150, 86));   // отсортированная часть, ~60% яркости
    private readonly int[] _a = new int[MaxN];
    private readonly int[] _tmp = new int[MaxN];
    private readonly int[] _count = new int[16];
    private readonly int[] _pos = new int[16];
    private readonly char[] _digits = new char[12];

    private Algo[] _order = AllOrder;
    private int _algoIdx;
    private int _size;
    private int _n;
    private Phase _phase;
    private IEnumerator<int>? _it;
    private Random _rng = new(1);

    private int _c1, _c2, _s1, _s2;     // подсветка: сравнение, перестановка (индексы элементов, -1 — нет)
    private int _fixedFrom, _fixedTo;   // элементы [0,_fixedTo) и [_fixedFrom,_n) — отсортированы (зелёные)
    private int _compares, _swaps;
    private float _rate;                // шагов в секунду без скорости и звука
    private float _speedMul;
    private float _acc;
    private float _pulse;

    /// <summary>Сколько шагов алгоритма выполнено (для тестов).</summary>
    internal int Steps { get; private set; }
    internal int Compares => _compares;
    internal int Swaps => _swaps;
    internal bool Sorting => _phase == Phase.Sort;
    internal Algo CurrentAlgo => _order[_algoIdx];

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _size = Math.Clamp(options.GetInt(SizeSpec.Name, (int)SizeSpec.Default), (int)SizeSpec.Min, (int)SizeSpec.Max);
        _speedMul = Math.Clamp(options.GetFloat(SpeedSpec.Name, SpeedSpec.Default), SpeedSpec.Min, SpeedSpec.Max) * MathF.Max(0f, options.Speed);
        string setName = options.GetString(AlgorithmsSpec.Name, AlgorithmsSpec.Options![(int)AlgorithmsSpec.Default]);
        _order = setName switch
        {
            "fast" => FastOrder,
            "slow" => SlowOrder,
            _ => AllOrder,
        };
        _rng = rng;
        _algoIdx = 0;
        _acc = 0f;
        _pulse = 0f;
        Steps = 0;
        _c1 = _c2 = _s1 = _s2 = -1;
        StartShuffle();
    }

    public void OnAudio(in AudioFrame audio) => _pulse = audio.Active ? audio.BeatPulse : 0f;

    public void Update(float dt)
    {
        dt = Math.Clamp(dt, 0f, 0.1f);
        float rate = _rate * _speedMul * (1f + PulseGain * _pulse);
        _acc += rate * dt;
        if (_acc > MaxAcc) _acc = MaxAcc;
        while (_acc >= 1f)
        {
            _acc -= 1f;
            Step();
        }
    }

    public void Draw(CharGrid grid)
    {
        int cols = grid.Cols, rows = grid.Rows;
        grid.Clear(Rgba.Black);

        // Поле: две строки воздуха под шапкой, база под полем. Столбики по центру, одинаковой ширины.
        int top = Math.Min(3, rows - 1);
        int bot = Math.Max(top, rows - 3);
        int h = bot - top + 1;
        int levels = 2 * h;
        int n = _n;
        int slot = cols / n;                 // ширина слота в ячейках
        int fieldW = n * slot;
        int left = slot >= 1 ? (cols - fieldW) / 2 : 0;
        if (slot >= 2)
        {
            // Столбик занимает slot-1 колонок, последняя колонка слота — зазор. Высота: █ и нижняя половина ▄.
            for (int e = 0; e < n; e++)
            {
                int lv = _a[e] * levels / n;
                int full = lv / 2;
                bool half = (lv & 1) != 0;
                Rgba color = ColorOf(e);
                int bx = left + e * slot;
                for (int r = 0; r < h; r++)
                {
                    int y = bot - r;
                    char ch = r < full ? '█' : (r == full && half ? '▄' : ' ');
                    if (ch == ' ') continue;
                    for (int dx = 0; dx < slot - 1; dx++) grid.Put(bx + dx, y, ch, color, Rgba.Black);
                }
            }
        }
        else if (slot == 1)
        {
            // Одна колонка на элемент: столбик — левая половина ячейки ▌, правая половина — зазор.
            for (int e = 0; e < n; e++)
            {
                int cells = (_a[e] * h + n - 1) / n;   // округление вверх, чтобы низкие столбики были видны
                Rgba color = ColorOf(e);
                for (int r = 0; r < cells; r++) grid.Put(left + e, bot - r, '▌', color, Rgba.Black);
            }
        }
        else
        {
            // Элементов больше колонок: каждая колонка — отсчёт одного элемента, зазоров нет.
            for (int x = 0; x < cols; x++)
            {
                int e = x * n / cols;
                int lv = _a[e] * levels / n;
                int full = lv / 2;
                bool half = (lv & 1) != 0;
                Rgba color = ColorOf(e);
                for (int r = 0; r < h; r++)
                {
                    int y = bot - r;
                    if (r < full) grid.Put(x, y, '█', color, Rgba.Black);
                    else if (r == full && half) grid.Put(x, y, '▄', color, Rgba.Black);
                }
            }
        }

        if (rows >= 4)
        {
            int baseFrom = slot >= 1 ? left : 0;
            int baseTo = slot >= 1 ? left + fieldW : cols;
            for (int x = baseFrom; x < baseTo; x++) grid.Put(x, rows - 2, '─', Rule, Rgba.Black);
        }

        // Строка сверху: алгоритм, N, счётчики, задержка на шаг.
        float effRate = _rate * _speedMul;
        int delay = effRate > 0f ? Math.Max(1, (int)MathF.Round(1000f / effRate)) : 0;
        int x0 = Margin;
        x0 = Text(grid, x0, 0, AlgoNames[(int)_order[_algoIdx]], Bright);
        x0 = Text(grid, x0, 0, "  N=", Dim);
        x0 = Num(grid, x0, 0, _n, Bright);
        x0 = Text(grid, x0, 0, "  COMPARES ", Dim);
        x0 = Num(grid, x0, 0, _compares, Bright);
        x0 = Text(grid, x0, 0, "  SWAPS ", Dim);
        x0 = Num(grid, x0, 0, _swaps, Bright);
        x0 = Text(grid, x0, 0, "  DELAY ", Dim);
        x0 = Num(grid, x0, 0, delay, Bright);
        Text(grid, x0, 0, " MS", Dim);

        // Строка снизу: номер алгоритма и фаза.
        if (rows >= 3)
        {
            int idx = _algoIdx + 1;
            int xf = Num(grid, Margin, rows - 1, idx, Dim);
            xf = Text(grid, xf, rows - 1, "/", Dim);
            xf = Num(grid, xf, rows - 1, _order.Length, Dim);
            xf = Text(grid, xf, rows - 1, "  ", Dim);
            Text(grid, xf, rows - 1, PhaseNames[(int)_phase], Bright);
        }
    }

    private void Step()
    {
        _c1 = _c2 = _s1 = _s2 = -1;
        if (_it != null && _it.MoveNext())
        {
            Steps++;
            return;
        }
        NextPhase();
    }

    private void NextPhase()
    {
        switch (_phase)
        {
            case Phase.Shuffle: StartSort(); break;
            case Phase.Sort: StartWave(); break;
            default:
                _algoIdx = (_algoIdx + 1) % _order.Length;
                StartShuffle();
                break;
        }
    }

    private void StartShuffle()
    {
        _phase = Phase.Shuffle;
        _compares = 0;
        _swaps = 0;
        _n = AlgoSize(_order[_algoIdx]);
        for (int i = 0; i < _n; i++) _a[i] = i + 1;
        _fixedFrom = _n;
        _fixedTo = 0;
        _rate = Math.Max(1f, _n / ShuffleSec);
        _it = Shuffle().GetEnumerator();
    }

    private void StartSort()
    {
        _phase = Phase.Sort;
        _compares = 0;
        _swaps = 0;
        _fixedFrom = _n;
        _fixedTo = 0;
        var algo = _order[_algoIdx];
        _rate = RateFor(algo, _n);
        _it = Make(algo).GetEnumerator();
    }

    private void StartWave()
    {
        _phase = Phase.Wave;
        _fixedFrom = _n;
        _fixedTo = 0;
        _rate = Math.Max(1f, _n / WaveSec);
        _it = Wave().GetEnumerator();
    }

    private int AlgoSize(Algo a) => a == Algo.Bogo ? BogoN : _size;

    private float RateFor(Algo a, int n)
    {
        float lg = MathF.Log2(n);
        return a switch
        {
            Algo.Bubble or Algo.Insertion or Algo.Selection or Algo.Cocktail => n * n * 0.5f / TargetQuadraticSec,
            Algo.Bogo => BogoRate,
            _ => n * lg * 1.5f / TargetLogSec,
        };
    }

    private IEnumerable<int> Make(Algo a) => a switch
    {
        Algo.Bubble => Bubble(),
        Algo.Insertion => Insertion(),
        Algo.Selection => Selection(),
        Algo.Shell => Shell(),
        Algo.Merge => Merge(),
        Algo.Quick => QuickRange(0, _n - 1),
        Algo.Heap => HeapSort(),
        Algo.Radix => Radix(),
        Algo.Cocktail => Cocktail(),
        _ => Bogo(),
    };

    // ----- Фазы -----

    /// <summary>Перемешивание Фишера–Йетса: каждый обмен — шаг.</summary>
    private IEnumerable<int> Shuffle()
    {
        for (int i = _n - 1; i > 0; i--)
        {
            Swap(i, _rng.Next(i + 1));
            yield return 0;
        }
    }

    /// <summary>Проверочный проход: зелёная волна слева направо.</summary>
    private IEnumerable<int> Wave()
    {
        for (int k = 0; k < _n; k++)
        {
            _fixedTo = k + 1;
            yield return 0;
        }
    }

    // ----- Алгоритмы: каждый yield — один шаг -----

    private IEnumerable<int> Bubble()
    {
        for (int pass = _n - 1; pass > 0; pass--)
        {
            bool swapped = false;
            for (int i = 0; i < pass; i++)
            {
                if (Greater(i, i + 1))
                {
                    Swap(i, i + 1);
                    swapped = true;
                }
                yield return 0;
            }
            _fixedFrom = pass;
            if (!swapped) break;
        }
    }

    private IEnumerable<int> Insertion()
    {
        for (int i = 1; i < _n; i++)
        {
            _fixedTo = i;
            int j = i;
            while (true)
            {
                bool move = j > 0 && Greater(j - 1, j);
                if (move) Swap(j - 1, j);
                yield return 0;
                if (!move) break;
                j--;
            }
        }
    }

    private IEnumerable<int> Selection()
    {
        for (int i = 0; i < _n - 1; i++)
        {
            int min = i;
            for (int j = i + 1; j < _n; j++)
            {
                if (Less(j, min)) min = j;
                yield return 0;
            }
            if (min != i) Swap(i, min);
            _fixedTo = i + 1;
            yield return 0;
        }
    }

    private IEnumerable<int> Shell()
    {
        for (int gap = _n / 2; gap > 0; gap /= 2)
        {
            for (int i = gap; i < _n; i++)
            {
                int j = i;
                while (j >= gap)
                {
                    bool move = Greater(j - gap, j);
                    if (move)
                    {
                        Swap(j - gap, j);
                        j -= gap;
                    }
                    yield return 0;
                    if (!move) break;
                }
            }
        }
    }

    private IEnumerable<int> Merge()
    {
        for (int width = 1; width < _n; width *= 2)
        {
            for (int lo = 0; lo < _n - width; lo += 2 * width)
            {
                int mid = lo + width;
                int hi = Math.Min(lo + 2 * width, _n);
                int i = lo, j = mid, k = lo;
                while (i < mid && j < hi)
                {
                    if (Less(j, i)) _tmp[k++] = _a[j++];
                    else _tmp[k++] = _a[i++];
                    yield return 0;
                }
                while (i < mid) _tmp[k++] = _a[i++];
                while (j < hi) _tmp[k++] = _a[j++];
                for (k = lo; k < hi; k++)
                {
                    Write(k, _tmp[k]);
                    yield return 0;
                }
            }
        }
    }

    /// <summary>Быстрая сортировка (Ломуто) с рекурсией по итераторам; глубина ~log n на перемешанных данных.</summary>
    private IEnumerable<int> QuickRange(int lo, int hi)
    {
        if (lo >= hi) yield break;
        int store = lo;
        for (int i = lo; i < hi; i++)
        {
            if (Less(i, hi))
            {
                if (i != store) Swap(i, store);
                store++;
            }
            yield return 0;
        }
        if (store != hi) Swap(store, hi);
        yield return 0;
        foreach (int s in QuickRange(lo, store - 1)) yield return s;
        foreach (int s in QuickRange(store + 1, hi)) yield return s;
    }

    private IEnumerable<int> HeapSort()
    {
        for (int s = _n / 2 - 1; s >= 0; s--)
            foreach (int x in Sift(s, _n)) yield return x;
        for (int end = _n - 1; end > 0; end--)
        {
            Swap(0, end);
            _fixedFrom = end;
            yield return 0;
            foreach (int x in Sift(0, end)) yield return x;
        }
    }

    private IEnumerable<int> Sift(int root, int end)
    {
        int r = root;
        while (true)
        {
            int c = 2 * r + 1;
            if (c >= end) yield break;
            if (c + 1 < end && Less(c, c + 1)) c++;
            bool go = Less(r, c);
            if (go) Swap(r, c);
            yield return 0;
            if (!go) yield break;
            r = c;
        }
    }

    /// <summary>Поразрядная LSD, основание 16: подсчёт, раскладка во временный буфер, обратное копирование.</summary>
    private IEnumerable<int> Radix()
    {
        for (int shift = 0; (_n >> shift) > 0; shift += 4)
        {
            Array.Clear(_count, 0, _count.Length);
            for (int i = 0; i < _n; i++)
            {
                _count[(_a[i] >> shift) & 15]++;
                _c1 = i;
                _c2 = -1;
                yield return 0;
            }
            int sum = 0;
            for (int d = 0; d < 16; d++)
            {
                _pos[d] = sum;
                sum += _count[d];
            }
            for (int i = 0; i < _n; i++)
            {
                int digit = (_a[i] >> shift) & 15;
                _tmp[_pos[digit]++] = _a[i];
                _swaps++;
                _c1 = i;
                _c2 = -1;
                yield return 0;
            }
            for (int k = 0; k < _n; k++)
            {
                Write(k, _tmp[k]);
                yield return 0;
            }
        }
    }

    private IEnumerable<int> Cocktail()
    {
        int lo = 0, hi = _n - 1;
        while (lo < hi)
        {
            bool swapped = false;
            for (int i = lo; i < hi; i++)
            {
                if (Greater(i, i + 1))
                {
                    Swap(i, i + 1);
                    swapped = true;
                }
                yield return 0;
            }
            _fixedFrom = hi;
            hi--;
            if (!swapped) break;

            swapped = false;
            for (int i = hi; i > lo; i--)
            {
                if (Greater(i - 1, i))
                {
                    Swap(i - 1, i);
                    swapped = true;
                }
                yield return 0;
            }
            _fixedTo = lo + 1;
            lo++;
            if (!swapped) break;
        }
    }

    /// <summary>Bogo: перемешиваем, пока не отсортировано. Для 6 элементов — ~720 попыток.</summary>
    private IEnumerable<int> Bogo()
    {
        while (!IsSorted())
        {
            for (int i = _n - 1; i > 0; i--)
            {
                Swap(i, _rng.Next(i + 1));
                yield return 0;
            }
        }
    }

    // ----- Примитивы: сравнение и запись считают счётчики и выставляют подсветку -----

    private bool Less(int i, int j)
    {
        _c1 = i;
        _c2 = j;
        _compares++;
        return _a[i] < _a[j];
    }

    private bool Greater(int i, int j)
    {
        _c1 = i;
        _c2 = j;
        _compares++;
        return _a[i] > _a[j];
    }

    private void Swap(int i, int j)
    {
        (_a[i], _a[j]) = (_a[j], _a[i]);
        _s1 = i;
        _s2 = j;
        _swaps++;
    }

    private void Write(int i, int v)
    {
        _a[i] = v;
        _s1 = i;
        _s2 = -1;
        _swaps++;
    }

    private bool IsSorted()
    {
        for (int i = 1; i < _n; i++)
            if (_a[i - 1] > _a[i]) return false;
        return true;
    }

    private Rgba ColorOf(int e)
    {
        if (e == _s1 || e == _s2) return Red;
        if (e == _c1 || e == _c2) return Rgba.White;
        if (_phase == Phase.Wave && e < _fixedTo && e >= _fixedTo - WaveFrontLen) return WaveFront;
        if (e < _fixedTo || e >= _fixedFrom) return _gradGreen[_a[e] * 255 / _n];
        return _grad[_a[e] * 255 / _n];
    }

    private static Rgba[] BuildGradient(Rgba lo, Rgba hi)
    {
        var g = new Rgba[256];
        for (int i = 0; i < g.Length; i++) g[i] = Rgba.Lerp(lo, hi, i / 255f);
        return g;
    }

    private static int Text(CharGrid g, int x, int y, string s, Rgba fg)
    {
        g.Text(x, y, s, fg);
        return x + s.Length;
    }

    /// <summary>Число по одной цифре в ячейку, без ToString. Возвращает x после последней цифры.</summary>
    private int Num(CharGrid g, int x, int y, int v, Rgba fg)
    {
        int t = Math.Max(0, v);
        int len = 0;
        do
        {
            _digits[len++] = (char)('0' + t % 10);
            t /= 10;
        } while (t > 0);
        for (int i = len - 1; i >= 0; i--) g.Put(x + len - 1 - i, y, _digits[i], fg);
        return x + len;
    }

    // ----- Внутренние хуки для тестов -----

    /// <summary>Прогоняет алгоритм на перемешанном массиве 1..n до конца (без отрисовки). Возвращает true, если отсортировано.</summary>
    internal bool RunAlgorithmToEnd(Algo a, int n)
    {
        _n = n;
        for (int i = 0; i < n; i++) _a[i] = i + 1;
        Drain(Shuffle());
        _compares = 0;
        _swaps = 0;
        Drain(Make(a));
        return IsSorted();
    }

    private static void Drain(IEnumerable<int> seq)
    {
        foreach (int unused in seq) { }
    }
}
