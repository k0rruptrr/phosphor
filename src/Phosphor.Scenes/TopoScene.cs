using System.Runtime.CompilerServices;
using Phosphor.Core;

[assembly: InternalsVisibleTo("Phosphor.Scenes.Tests")]

namespace Phosphor.Scenes;

/// <summary>
/// Топокарта: медленно плывёт фрактальный рельеф (тайл 512×512, value-noise в 5 октав, предрасчёт в Init).
/// Изолинии — переходы через уровни шага interval; каждая пятая жирнее и с подписью высоты в разрыве линии.
/// Отмывка по высоте тусклыми тонами, реки — по тальвегам от истоков, озёра в локальных минимумах.
/// Иногда курсор едет к вершине и рисует профиль высот в окошке внизу. Звука нет.
/// </summary>
public sealed class TopoScene : IScene, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec IntervalSpec = new("interval", SceneParamKind.Int, 20f, 10f, 50f, "шаг изолиний, м");
    public static readonly SceneParamSpec DriftSpec = new("drift", SceneParamKind.Float, 1f, 0f, 3f, "скорость прокрутки карты");
    public static readonly SceneParamSpec ShadingSpec = new("shading", SceneParamKind.Bool, 1f, 0f, 1f, "отмывка рельефа по высоте");
    private static readonly SceneParamSpec[] ParamList = { IntervalSpec, DriftSpec, ShadingSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    public string Id => "topo";

    private const int Tile = 512, TileMask = Tile - 1;
    private const float HeightMin = 150f, HeightMax = 700f; // м, перепад рельефа на экране: ~15–30 уровней при interval 20
    private const float HighLevel = HeightMin + 0.7f * (HeightMax - HeightMin); // выше — вершины и истоки рек
    private const float SubStep = 0.75f;          // текселей на субпиксель по горизонтали (тексель ≈ 100 м)
    private const float LatTexels = 18f;          // текселей в угловой минуте широты
    private const float LonTexels = 11f;          // текселей в угловой минуте долготы (55°N)
    private const float RiverSpacing = 120f;      // истоки рек разнесены не ближе, текселей
    private const int MaxRivers = 4;
    private const float PeakProminence = 2f;      // превышение вершины над кольцом, в интервалах изолиний
    private const float PeakRing = 30f;           // радиус кольца сравнения, текселей
    private const float PeakSpacing = 60f;        // минимальное расстояние между вершинами, текселей
    private const int MaxPeaks = 14;
    private const int MaxLabels = 6;
    private const int ProfileW = 36, ProfileH = 9; // окошко профиля, с рамкой

    // Флаги субпикселя.
    private const byte FContour = 1, FMajor = 2, FRiver = 4, FLake = 8;
    // Вид ячейки.
    private const byte KNone = 0, KMinor = 1, KMajor = 2, KRiver = 3;

    private static readonly Rgba ColMinor = Rgba.Hex(0x6E5C40);   // тонкие: тусклые коричневые
    private static readonly Rgba ColMajor = Rgba.Hex(0xB39A6A);   // каждая пятая: тёплый светло-коричневый, толще
    private static readonly Rgba ColRiver = Rgba.Hex(0x62AEFF);
    private static readonly Rgba ColLake = Rgba.Hex(0x1F4F8F);
    private static readonly Rgba ColText = Rgba.Hex(0xC9C3A0);
    private static readonly Rgba ColDim = Rgba.Hex(0x6F7F62);
    private static readonly Rgba ColMark = Rgba.Hex(0xFFE9A0);

    // Отмывка: низины тёмно-зелёные, выше коричневый, потом серый и снег. Тона заметно темнее линий.
    private static readonly Rgba[] ToneStops =
    {
        Rgba.Hex(0x101E14), Rgba.Hex(0x1F2C19), Rgba.Hex(0x33281A), Rgba.Hex(0x46443F), Rgba.Hex(0x5E6268),
    };
    private static readonly float[] ToneAt = { 0f, 0.35f, 0.6f, 0.8f, 1f };

    private static readonly string[] Syllables = { "KOR", "VAL", "BEL", "ZAR", "TOM", "DRI", "SAN", "GOR", "NAV", "LIT" };

    private Random _rng = new(1);
    private int _cols, _rows, _sw, _sh, _step;
    private float _drift, _speed, _rowK;
    private bool _shading;
    private float _ox, _oy;                       // окно карты в текселях тайла, 0..512
    private int _frame;                           // счётчик Update для пересчёта сетки через кадр

    private float[] _tile = Array.Empty<float>();
    private byte[] _water = Array.Empty<byte>();  // 0 — суша, 1 — река, 2 — озеро
    private readonly List<int[]> _rivers = new(); // пути рек от истока вниз (индексы тайла), для теста

    // Индексы тайла по колонкам и строкам субпикселей (пересчёт каждый кадр, без аллокаций).
    private int[] _colI0 = Array.Empty<int>(), _colI1 = Array.Empty<int>(), _colR = Array.Empty<int>();
    private float[] _colF = Array.Empty<float>();
    private int[] _rowI0 = Array.Empty<int>(), _rowI1 = Array.Empty<int>(), _rowR = Array.Empty<int>();
    private float[] _rowF = Array.Empty<float>();

    // Субпиксельная сетка (2×3 на ячейку).
    private float[] _hSub = Array.Empty<float>();
    private int[] _lvlSub = Array.Empty<int>();
    private byte[] _flagSub = Array.Empty<byte>();

    // Ячейки, заполняются в Refresh.
    private byte[] _cKind = Array.Empty<byte>();
    private byte[] _cBits = Array.Empty<byte>();
    private int[] _cLvl = Array.Empty<int>();
    private Rgba[] _cBg = Array.Empty<Rgba>();

    // Вершины.
    private float[] _peakX = Array.Empty<float>(), _peakY = Array.Empty<float>();
    private int[] _peakH = Array.Empty<int>();
    private string?[] _peakName = Array.Empty<string?>();
    private int _peakCount;

    // Подписи высот.
    private int _lblCount;
    private readonly int[] _lblX = new int[MaxLabels], _lblY = new int[MaxLabels], _lblVal = new int[MaxLabels];

    // Событие: курсор к вершине и профиль.
    private bool _profOn;
    private int _evState;                        // 0 — ждёт, 1 — едет, 2 — профиль
    private float _evTimer, _evT;
    private int _evPeak;
    private float _mx, _my;                      // курсор, субпиксели экрана
    private float _tgtX, _tgtY;                  // вершина в субпикселях экрана
    private readonly float[] _prof = new float[2 * (ProfileW - 2)];
    private readonly byte[] _profBits = new byte[(ProfileW - 2) * (ProfileH - 2)];

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _rng = rng;
        _cols = cols; _rows = rows;
        _sw = cols * 2; _sh = rows * 3;
        _step = Math.Clamp(options.GetInt(IntervalSpec.Name, (int)IntervalSpec.Default), (int)IntervalSpec.Min, (int)IntervalSpec.Max);
        _drift = Math.Clamp(options.GetFloat(DriftSpec.Name, DriftSpec.Default), DriftSpec.Min, DriftSpec.Max);
        _shading = options.GetBool(ShadingSpec.Name, ShadingSpec.Default >= 0.5f);
        _speed = MathF.Max(0f, options.Speed);
        // Высота субпикселя / его ширина = (CellAspect/3) / (1/2): строка субпикселя по тексели, чтобы карта не сплющилась.
        _rowK = SubStep * 2f * MathF.Max(1f, options.CellAspect) / 3f;

        BuildTerrain(rng);
        _ox = (float)(rng.NextDouble() * Tile);
        _oy = (float)(rng.NextDouble() * Tile);
        if (_rivers.Count > 0)
        {
            // Стартовое окно — по центру истока первой реки, чтобы на экране сразу была река. Дальше карта ползёт.
            int src = _rivers[0][0];
            _ox = ((src & TileMask) - 0.5f * _sw * SubStep + Tile) % Tile;
            _oy = ((src >> 9) - 0.5f * _sh * _rowK + Tile) % Tile;
        }
        BuildPeaks(rng);

        _colI0 = new int[_sw]; _colI1 = new int[_sw]; _colR = new int[_sw]; _colF = new float[_sw];
        _rowI0 = new int[_sh]; _rowI1 = new int[_sh]; _rowR = new int[_sh]; _rowF = new float[_sh];
        _hSub = new float[_sw * _sh];
        _lvlSub = new int[_sw * _sh];
        _flagSub = new byte[_sw * _sh];
        _cKind = new byte[cols * rows];
        _cBits = new byte[cols * rows];
        _cLvl = new int[cols * rows];
        _cBg = new Rgba[cols * rows];

        _profOn = cols >= ProfileW + 4 && rows >= ProfileH + 6;
        _evState = 0;
        _evT = 0f;
        _evTimer = 15f + 20f * (float)rng.NextDouble();
        _mx = _my = _tgtX = _tgtY = 0f;
        _lblCount = 0;

        Refresh();
    }

    public void Update(float dt)
    {
        dt = Math.Clamp(dt, 0f, 0.1f);
        float v = _drift * _speed * dt;
        _ox = (_ox + 2f * v) % Tile;               // диагональная прокрутка: быстрее по X
        _oy = (_oy + 1.2f * v) % Tile;
        StepEvent(dt);
        // Карта ползёт медленно: сетку пересчитываем через кадр (30 раз в секунду), Draw рисует последний пересчёт.
        if ((++_frame & 1) == 0) Refresh();
    }

    public void Draw(CharGrid grid)
    {
        if (grid.Cols != _cols || grid.Rows != _rows)
        {
            grid.Clear(Rgba.Black);
            return;
        }
        int cols = _cols, rows = _rows;

        // Карта: каждая ячейка уже посчитана в Update.
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < cols; x++)
        {
            int i = y * cols + x;
            byte bits = _cBits[i];
            char ch = bits == 0 ? ' ' : GlyphSet.Sextant(bits);
            Rgba fg = _cKind[i] == KRiver ? ColRiver : _cKind[i] == KMajor ? ColMajor : ColMinor;
            grid.Put(x, y, ch, fg, _cBg[i]);
        }

        // Вершины: ▲ и высота, у каждой третьей — название.
        for (int p = 0; p < _peakCount; p++)
        {
            if (!ScreenOf(p, out float sx, out float sy)) continue;
            int cx = (int)(sx / 2f), cy = (int)(sy / 3f);
            if (cx >= cols || cy >= rows) continue;
            grid.Put(cx, cy, '▲', ColMajor, null);
            int xe = Num(grid, cx + 2, cy, _peakH[p], ColText, null);
            if (_peakName[p] != null) Str(grid, xe + 1, cy, _peakName[p]!, ColText, null);
        }

        // Подписи высот в разрыве самых жирных изолиний.
        for (int l = 0; l < _lblCount; l++)
            Num(grid, _lblX[l], _lblY[l], _lblVal[l], ColMajor, null);

        // Курсор и профиль.
        if (_evState == 1 || _evState == 2)
        {
            int cx = (int)(_mx / 2f), cy = (int)(_my / 3f);
            grid.Put(cx, cy, '┼', ColMark, null);
            grid.Put(cx - 2, cy, '─', ColMark, null);
            grid.Put(cx - 1, cy, '─', ColMark, null);
            grid.Put(cx + 1, cy, '─', ColMark, null);
            grid.Put(cx + 2, cy, '─', ColMark, null);
            grid.Put(cx, cy - 1, '│', ColMark, null);
            grid.Put(cx, cy + 1, '│', ColMark, null);
        }
        if (_evState == 2) DrawProfile(grid);

        // Координаты по краям и служебные строки.
        for (int y = 2; y < rows - 2; y += 10)
        {
            int min = 3340 - (int)((_oy + 3 * y * _rowK) / LatTexels);   // y — строка ячейки, в субпикселях это 3·y
            int xe = Num(grid, 0, y, min / 60, ColDim, Rgba.Black);
            grid.Put(xe, y, '°', ColDim, Rgba.Black);
            Num2(grid, xe + 1, y, min % 60, ColDim);
            grid.Put(xe + 3, y, '\'', ColDim, Rgba.Black);
            grid.Put(xe + 4, y, 'N', ColDim, Rgba.Black);
        }
        if (rows >= 2)
        {
            for (int x = 9; x + 7 < cols; x += 24)
            {
                int min = 2255 + (int)((_ox + 2 * x * SubStep) / LonTexels);  // x — колонка ячейки, в субпикселях это 2·x
                int xe = Num(grid, x, rows - 1, min / 60, ColDim, Rgba.Black);
                grid.Put(xe, rows - 1, '°', ColDim, Rgba.Black);
                Num2(grid, xe + 1, rows - 1, min % 60, ColDim);
                grid.Put(xe + 3, rows - 1, '\'', ColDim, Rgba.Black);
                grid.Put(xe + 4, rows - 1, 'E', ColDim, Rgba.Black);
            }
        }
        if (rows >= 6 && cols >= 20)
            Str(grid, cols - 17, rows - 3, "0 ├────┤ 1 KM", ColDim, Rgba.Black);
        if (rows >= 4 && cols >= 4)
        {
            grid.Put(cols - 2, 1, 'N', ColText, Rgba.Black);
            grid.Put(cols - 2, 2, '↑', ColText, Rgba.Black);
        }
        if (rows >= 1)
        {
            for (int x = 0; x < cols; x++) grid.Put(x, 0, ' ', ColText, Rgba.Black);
            int xe = Str(grid, 3, 0, "TOPO 1:50000  SHEET 27  CONTOUR ", ColText, Rgba.Black);
            Num(grid, xe, 0, _step, ColText, Rgba.Black);
            Str(grid, xe + DigitCount(_step) + 1, 0, "M", ColText, Rgba.Black);
        }
    }

    // ---------- Рельеф ----------

    /// <summary>Тайл: сумма 5 октав value-noise (решётка 4,8,16,32,64), затем нормировка в высоты.</summary>
    private void BuildTerrain(Random rng)
    {
        _tile = new float[Tile * Tile];
        for (int o = 0; o < 5; o++)
        {
            int n = 2 << o;
            float amp = MathF.Pow(0.45f, o);
            var vals = new float[n * n];
            for (int i = 0; i < vals.Length; i++) vals[i] = (float)rng.NextDouble();
            int cell = Tile / n;
            for (int y = 0; y < Tile; y++)
            {
                int gy = y / cell;
                float fy = (y % cell) / (float)cell;
                fy = fy * fy * (3f - 2f * fy);
                int r0 = gy * n, r1 = ((gy + 1) % n) * n;
                for (int x = 0; x < Tile; x++)
                {
                    int gx = x / cell;
                    float fx = (x % cell) / (float)cell;
                    fx = fx * fx * (3f - 2f * fx);
                    int gx1 = (gx + 1) % n;
                    float a = vals[r0 + gx], b = vals[r0 + gx1], c = vals[r1 + gx], d = vals[r1 + gx1];
                    float top = a + (b - a) * fx, bot = c + (d - c) * fx;
                    _tile[y * Tile + x] += amp * (top + (bot - top) * fy);
                }
            }
        }

        float mn = float.MaxValue, mx = float.MinValue;
        for (int i = 0; i < _tile.Length; i++)
        {
            mn = MathF.Min(mn, _tile[i]);
            mx = MathF.Max(mx, _tile[i]);
        }
        float range = MathF.Max(1e-6f, mx - mn);
        for (int i = 0; i < _tile.Length; i++)
            _tile[i] = HeightMin + (HeightMax - HeightMin) * ((_tile[i] - mn) / range);

        BuildWater(rng);
    }

    /// <summary>
    /// Реки: до MaxRivers истоков выше HighLevel, разнесённых не ближе RiverSpacing. От каждого истока река
    /// только спускается по самому крутому соседу до локального минимума — там озеро.
    /// </summary>
    private void BuildWater(Random rng)
    {
        _water = new byte[Tile * Tile];
        _rivers.Clear();
        var sources = new List<int>();
        for (int tries = 0; sources.Count < MaxRivers && tries < 800; tries++)
        {
            int p = rng.Next(Tile * Tile);
            if (_tile[p] < HighLevel) continue;
            int px = p & TileMask, py = p >> 9;
            bool far = true;
            foreach (int q in sources)
            {
                float dx = (q & TileMask) - px, dy = (q >> 9) - py;
                if (dx * dx + dy * dy < RiverSpacing * RiverSpacing) { far = false; break; }
            }
            if (!far) continue;
            sources.Add(p);
            Descend(p);
        }
    }

    private void Descend(int p)
    {
        int x = p & TileMask, y = p >> 9;
        var path = new List<int>();
        for (int step = 0; step < 1500; step++)
        {
            path.Add(y * Tile + x);
            if (_water[y * Tile + x] == 0) _water[y * Tile + x] = 1;
            float cur = _tile[y * Tile + x];
            int bx = x, by = y;
            float best = cur;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = (x + dx) & TileMask, ny = (y + dy) & TileMask;
                float h = _tile[ny * Tile + nx];
                if (h < best) { best = h; bx = nx; by = ny; }
            }
            if (bx == x && by == y) break;     // локальный минимум
            x = bx; y = by;
        }
        _rivers.Add(path.ToArray());

        // Озеро в минимуме: пятно радиусом 4 текселя там, где рельеф почти ровный.
        float low = _tile[y * Tile + x];
        const int r = 4;
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (dx * dx + dy * dy > r * r) continue;
            int i = ((y + dy) & TileMask) * Tile + ((x + dx) & TileMask);
            if (_water[i] == 0 && _tile[i] <= low + 15f) _water[i] = 2;
        }
    }

    /// <summary>Вершины: максимумы в блоках 16×16 выше HighLevel, случайные MaxPeaks из них.</summary>
    private void BuildPeaks(Random rng)
    {
        var cand = new List<int>();
        for (int by = 0; by < Tile; by += 16)
        for (int bx = 0; bx < Tile; bx += 16)
        {
            int best = -1;
            float bh = float.MinValue;
            for (int yy = 0; yy < 16; yy++)
            for (int xx = 0; xx < 16; xx++)
            {
                int idx = (by + yy) * Tile + bx + xx;
                if (_tile[idx] > bh) { bh = _tile[idx]; best = idx; }
            }
            if (bh >= HighLevel && Prominence(best) >= PeakProminence * _step) cand.Add(best);
        }

        // Высокие первыми; вершины ближе PeakSpacing друг к другу не ставим (остаётся самая высокая).
        cand.Sort((a, b) => _tile[b].CompareTo(_tile[a]));
        var keep = new List<int>();
        foreach (int idx in cand)
        {
            bool near = false;
            foreach (int q in keep)
            {
                int dx = Math.Abs((idx & TileMask) - (q & TileMask));
                int dy = Math.Abs((idx >> 9) - (q >> 9));
                dx = Math.Min(dx, Tile - dx); dy = Math.Min(dy, Tile - dy);
                if (dx * dx + dy * dy < PeakSpacing * PeakSpacing) { near = true; break; }
            }
            if (!near) keep.Add(idx);
        }
        cand = keep;
        for (int i = cand.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (cand[i], cand[j]) = (cand[j], cand[i]);
        }

        _peakCount = Math.Min(MaxPeaks, cand.Count);
        _peakX = new float[_peakCount];
        _peakY = new float[_peakCount];
        _peakH = new int[_peakCount];
        _peakName = new string?[_peakCount];
        for (int k = 0; k < _peakCount; k++)
        {
            int idx = cand[k];
            _peakX[k] = (idx & TileMask) + 0.5f;
            _peakY[k] = (idx >> 9) + 0.5f;
            _peakH[k] = (int)_tile[idx];
            if (k % 3 == 0)
                _peakName[k] = "MT " + Syllables[rng.Next(Syllables.Length)] + Syllables[rng.Next(Syllables.Length)];
        }
    }

    /// <summary>Превышение текселя idx над самой низкой точкой кольца радиусом PeakRing (16 направлений).</summary>
    private float Prominence(int idx)
    {
        int px = idx & TileMask, py = idx >> 9;
        float low = float.MaxValue;
        for (int k = 0; k < 16; k++)
        {
            float a = k * MathF.Tau / 16f;
            int x = (px + (int)MathF.Round(PeakRing * MathF.Cos(a))) & TileMask;
            int y = (py + (int)MathF.Round(PeakRing * MathF.Sin(a))) & TileMask;
            low = MathF.Min(low, _tile[y * Tile + x]);
        }
        return _tile[idx] - low;
    }

    private float SampleH(float x, float y)
    {
        float fx = MathF.Floor(x), fy = MathF.Floor(y);
        int x0 = (int)fx & TileMask, y0 = (int)fy & TileMask;
        int x1 = (x0 + 1) & TileMask, y1 = (y0 + 1) & TileMask;
        float tx = x - fx, ty = y - fy;
        float a = _tile[y0 * Tile + x0], b = _tile[y0 * Tile + x1];
        float c = _tile[y1 * Tile + x0], d = _tile[y1 * Tile + x1];
        float top = a + (b - a) * tx, bot = c + (d - c) * tx;
        return top + (bot - top) * ty;
    }

    private static Rgba Tone(float h)
    {
        float t = Math.Clamp((h - HeightMin) / (HeightMax - HeightMin), 0f, 1f);
        int i = 0;
        while (i < ToneStops.Length - 2 && t > ToneAt[i + 1]) i++;
        float k = (t - ToneAt[i]) / (ToneAt[i + 1] - ToneAt[i]);
        return Rgba.Lerp(ToneStops[i], ToneStops[i + 1], k);
    }

    /// <summary>Пересчёт сетки: высоты субпикселей, переходы через уровни, реки и озёра, ячейки, подписи.</summary>
    private void Refresh()
    {
        int sw = _sw, sh = _sh, step = _step;
        float inv = 1f / step;

        // Индексы тайла по колонкам и строкам субпикселей — один раз на кадр (координаты неотрицательны).
        for (int sx = 0; sx < sw; sx++)
        {
            float tx = _ox + sx * SubStep;
            int xi = (int)tx;
            _colI0[sx] = xi & TileMask;
            _colI1[sx] = (xi + 1) & TileMask;
            _colF[sx] = tx - xi;
            _colR[sx] = (int)(tx + 0.5f) & TileMask;
        }
        for (int sy = 0; sy < sh; sy++)
        {
            float ty = _oy + sy * _rowK;
            int yi = (int)ty;
            _rowI0[sy] = (yi & TileMask) * Tile;
            _rowI1[sy] = ((yi + 1) & TileMask) * Tile;
            _rowF[sy] = ty - yi;
            _rowR[sy] = ((int)(ty + 0.5f) & TileMask) * Tile;
        }
        var t = _tile;
        for (int sy = 0; sy < sh; sy++)
        {
            float fy = _rowF[sy];
            int r0 = _rowI0[sy], r1 = _rowI1[sy], rr = _rowR[sy];
            for (int sx = 0; sx < sw; sx++)
            {
                int c0 = _colI0[sx], c1 = _colI1[sx];
                float fx = _colF[sx];
                float top = t[r0 + c0] + (t[r0 + c1] - t[r0 + c0]) * fx;
                float bot = t[r1 + c0] + (t[r1 + c1] - t[r1 + c0]) * fx;
                int i = sy * sw + sx;
                _hSub[i] = top + (bot - top) * fy;
                byte w = _water[rr + _colR[sx]];
                _flagSub[i] = w == 1 ? FRiver : w == 2 ? FLake : (byte)0;
            }
        }

        // Изолинии: субпиксель отмечается, если к его правому или нижнему соседу уровень меняется.
        // Тонкая линия рисуется, только если склон не круче полутора субпикселя на изолинию: иначе изолинии
        // сливаются в муар и тонкие пропускаем. Жирная (каждая пятая) рисуется всегда.
        for (int sy = 0; sy < sh; sy++)
        for (int sx = 0; sx < sw; sx++)
        {
            int i = sy * sw + sx;
            float h = _hSub[i];
            int a = (int)(h * inv);
            int lvl = -1;
            float dh = 0f;
            if (sx + 1 < sw)
            {
                float hr = _hSub[i + 1];
                int b = (int)(hr * inv);
                dh = MathF.Max(dh, MathF.Abs(hr - h));
                if (b != a) lvl = Math.Max(lvl, Math.Max(a, b));
            }
            if (sy + 1 < sh)
            {
                float hd = _hSub[i + sw];
                int b = (int)(hd * inv);
                dh = MathF.Max(dh, MathF.Abs(hd - h));
                if (b != a) lvl = Math.Max(lvl, Math.Max(a, b));
            }
            _lvlSub[i] = lvl;
            if (lvl < 0) continue;
            if (lvl % 5 == 0) _flagSub[i] |= FContour | FMajor;
            else if (dh <= 0.5f * step) _flagSub[i] |= FContour;
        }

        // Жирные изолинии толщиной ровно 2 субпикселя: дублируем только на правого соседа (толщина в одну сторону).
        for (int i = sw * sh - 1; i >= 0; i--)
        {
            if ((_flagSub[i] & FMajor) == 0) continue;
            if (i % sw + 1 < sw)
            {
                _flagSub[i + 1] |= FContour | FMajor;
                _lvlSub[i + 1] = _lvlSub[i];
            }
        }

        // Ячейки 2×3 субпикселя.
        for (int cy = 0; cy < _rows; cy++)
        for (int cx = 0; cx < _cols; cx++)
        {
            int riverBits = 0, conBits = 0, lv = -1;
            bool major = false;
            for (int dy = 0; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                int j = (cy * 3 + dy) * sw + cx * 2 + dx;
                byte f = _flagSub[j];
                int bit = dy * 2 + dx;
                if ((f & FRiver) != 0) riverBits |= 1 << bit;
                if ((f & FContour) != 0)
                {
                    conBits |= 1 << bit;
                    if ((f & FMajor) != 0 && !major) { major = true; lv = _lvlSub[j]; }
                }
            }
            int ci = cy * _cols + cx;
            byte kind; int bits;
            if (riverBits != 0) { kind = KRiver; bits = riverBits; }
            else if (conBits != 0) { kind = major ? KMajor : KMinor; bits = conBits; }
            else { kind = KNone; bits = 0; }
            _cKind[ci] = kind;
            _cBits[ci] = (byte)bits;
            _cLvl[ci] = kind == KMajor ? lv : -1;

            int c = (cy * 3 + 1) * sw + cx * 2 + 1;
            Rgba bg = _shading ? Tone(_hSub[c]) : Rgba.Black;
            if ((_flagSub[c] & FLake) != 0) bg = ColLake;
            _cBg[ci] = bg;
        }

        // Подписи: в горизонтальном куске самой жирной изолинии (4 ячейки одного уровня), с интервалом между подписями.
        _lblCount = 0;
        for (int y = 1; y < _rows - 1 && _lblCount < MaxLabels; y++)
        for (int x = 9; x < _cols && _lblCount < MaxLabels; x++)
        {
            int i = y * _cols + x;
            if (_cKind[i] != KMajor) continue;
            int lv = _cLvl[i];
            int value = lv * step;
            int len = DigitCount(value);
            if (x + len > _cols) continue;
            bool ok = true;
            for (int k = 0; k < len && ok; k++) ok = _cKind[i + k] == KMajor && _cLvl[i + k] == lv;
            if (!ok) continue;
            bool near = false;
            for (int l = 0; l < _lblCount && !near; l++)
                near = Math.Abs(_lblX[l] - x) < 18 && Math.Abs(_lblY[l] - y) < 4;
            if (near) continue;
            _lblX[_lblCount] = x;
            _lblY[_lblCount] = y;
            _lblVal[_lblCount] = value;
            _lblCount++;
        }
    }

    // ---------- Курсор и профиль ----------

    /// <summary>Экранные субпиксели вершины p (с учётом прокрутки). false — вне окна.</summary>
    private bool ScreenOf(int p, out float sx, out float sy)
    {
        float dx = _peakX[p] - _ox; dx -= Tile * MathF.Floor(dx / Tile);
        float dy = _peakY[p] - _oy; dy -= Tile * MathF.Floor(dy / Tile);
        sx = dx / SubStep;
        sy = dy / _rowK;
        return sx < _sw && sy < _sh;
    }

    private void StepEvent(float dt)
    {
        if (!_profOn) return;
        switch (_evState)
        {
            case 0:
                _evTimer -= dt;
                if (_evTimer <= 0f) StartEvent();
                break;
            case 1:
            {
                _evT += dt;
                float k = MathF.Min(1f, _evT / 2.5f);
                float e = k * k * (3f - 2f * k);
                if (ScreenOf(_evPeak, out float tx, out float ty)) { _tgtX = tx; _tgtY = ty; }
                _mx = _tgtX * e;
                _my = _tgtY * e;
                if (k >= 1f)
                {
                    _evState = 2;
                    _evT = 0f;
                    BuildProfile();
                }
                break;
            }
            case 2:
                _evT += dt;
                if (ScreenOf(_evPeak, out float px, out float py)) { _tgtX = px; _tgtY = py; }
                _mx = _tgtX;
                _my = _tgtY;
                if (_evT >= 6f)
                {
                    _evState = 0;
                    _evTimer = 40f + 50f * (float)_rng.NextDouble();
                }
                break;
        }
    }

    private void StartEvent()
    {
        for (int t = 0; t < 24 && _peakCount > 0; t++)
        {
            int p = _rng.Next(_peakCount);
            if (!ScreenOf(p, out float sx, out float sy)) continue;
            if (sx < 4 || sy < 4 || sx > _sw - 4 || sy > _sh - 4) continue;
            _evPeak = p;
            _tgtX = sx; _tgtY = sy;
            _mx = 0f; _my = 0f;             // курсор въезжает из левого верхнего угла
            _evState = 1;
            _evT = 0f;
            return;
        }
        _evTimer = 6f;                       // видимой вершины нет — попробуем позже
    }

    /// <summary>Профиль высот по горизонтали через курсор: 2 субпикселя на колонку окошка.</summary>
    private void BuildProfile()
    {
        int iw = ProfileW - 2, ih = ProfileH - 2, n = 2 * iw, sh = 3 * ih;
        float wx = _ox + _mx * SubStep, wy = _oy + _my * _rowK;
        float mn = float.MaxValue, mx = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            float h = SampleH(wx + (i - iw) * SubStep, wy);
            _prof[i] = h;
            mn = MathF.Min(mn, h);
            mx = MathF.Max(mx, h);
        }
        Array.Clear(_profBits);
        float range = MathF.Max(1f, mx - mn);
        int prevY = 0;
        for (int sx = 0; sx < n; sx++)
        {
            int y = (int)((mx - _prof[sx]) / range * (sh - 1) + 0.5f);
            int y0 = sx == 0 ? y : Math.Min(prevY, y);
            int y1 = sx == 0 ? y : Math.Max(prevY, y);
            for (int yy = y0; yy <= y1; yy++)
                _profBits[(yy / 3) * iw + sx / 2] |= (byte)(1 << ((yy % 3) * 2 + sx % 2));
            prevY = y;
        }
    }

    private void DrawProfile(CharGrid grid)
    {
        int x0 = 9, y0 = grid.Rows - ProfileH - 3;      // левый нижний угол, справа внизу — масштаб и координаты
        if (x0 < 0 || y0 < 2) return;
        for (int x = x0 + 1; x < x0 + ProfileW - 1; x++)
        {
            grid.Put(x, y0, '─', ColDim, Rgba.Black);
            grid.Put(x, y0 + ProfileH - 1, '─', ColDim, Rgba.Black);
        }
        for (int y = y0 + 1; y < y0 + ProfileH - 1; y++)
        {
            grid.Put(x0, y, '│', ColDim, Rgba.Black);
            grid.Put(x0 + ProfileW - 1, y, '│', ColDim, Rgba.Black);
        }
        grid.Put(x0, y0, '┌', ColDim, Rgba.Black);
        grid.Put(x0 + ProfileW - 1, y0, '┐', ColDim, Rgba.Black);
        grid.Put(x0, y0 + ProfileH - 1, '└', ColDim, Rgba.Black);
        grid.Put(x0 + ProfileW - 1, y0 + ProfileH - 1, '┘', ColDim, Rgba.Black);
        Str(grid, x0 + 2, y0, "PROFILE", ColText, Rgba.Black);

        int iw = ProfileW - 2, ih = ProfileH - 2;
        for (int cy = 0; cy < ih; cy++)
        for (int cx = 0; cx < iw; cx++)
        {
            byte b = _profBits[cy * iw + cx];
            grid.Put(x0 + 1 + cx, y0 + 1 + cy, b == 0 ? ' ' : GlyphSet.Sextant(b), ColMajor, Rgba.Black);
        }
    }

    // ---------- Помощники записи текста (без аллокаций) ----------

    private static int Str(CharGrid g, int x, int y, string s, Rgba fg, Rgba? bg)
    {
        for (int i = 0; i < s.Length; i++) g.Put(x + i, y, s[i], fg, bg);
        return x + s.Length;
    }

    private static int Num(CharGrid g, int x, int y, int v, Rgba fg, Rgba? bg)
    {
        int n = Math.Max(0, v), digits = DigitCount(n);
        for (int k = digits - 1; k >= 0; k--)
        {
            g.Put(x + k, y, (char)('0' + n % 10), fg, bg);
            n /= 10;
        }
        return x + digits;
    }

    private static void Num2(CharGrid g, int x, int y, int v, Rgba fg)
    {
        g.Put(x, y, (char)('0' + (v / 10) % 10), fg, Rgba.Black);
        g.Put(x + 1, y, (char)('0' + v % 10), fg, Rgba.Black);
    }

    private static int DigitCount(int v)
    {
        int n = 1;
        for (int t = Math.Max(0, v); t >= 10; t /= 10) n++;
        return n;
    }

    // ---------- Внутренние хуки для тестов ----------

    internal int SubWidth => _sw;
    internal int SubHeight => _sh;
    internal int Interval => _step;

    /// <summary>Высота субпикселя (sx, sy) в метрах — после последнего Update/Init.</summary>
    internal float HeightAtSub(int sx, int sy) => _hSub[sy * _sw + sx];

    /// <summary>Вид ячейки: 0 — пусто, 1 — тонкая изолиния, 2 — жирная, 3 — река.</summary>
    internal int CellKind(int x, int y) => _cKind[y * _cols + x];

    /// <summary>Подписи высот: позиция ячейки и значение в метрах.</summary>
    internal (int X, int Y, int Value)[] Labels
    {
        get
        {
            var r = new (int X, int Y, int Value)[_lblCount];
            for (int l = 0; l < _lblCount; l++) r[l] = (_lblX[l], _lblY[l], _lblVal[l]);
            return r;
        }
    }

    /// <summary>Принудительно поставить курсор в профиль (как после прибытия), для теста профиля.</summary>
    internal void ForceProfileAt(int sx, int sy)
    {
        if (!_profOn) return;
        _mx = sx; _my = sy;
        _evState = 2; _evT = 0f;
        BuildProfile();
    }

    /// <summary>Пути рек в тайле: от истока вниз, индексы текселей (y*512+x).</summary>
    internal IReadOnlyList<int[]> RiverPaths => _rivers;

    internal int PeakCount => _peakCount;

    internal (float X, float Y) PeakAt(int i) => (_peakX[i], _peakY[i]);

    /// <summary>Превышение вершины i над кольцом (тексели тайла, метры).</summary>
    internal float PeakProminenceOf(int i) => Prominence((int)_peakY[i] * Tile + (int)_peakX[i]);

    /// <summary>Высота текселя тайла по индексу y*512+x.</summary>
    internal float TexelHeight(int idx) => _tile[idx];

    /// <summary>Заполненные биты профиля (для теста).</summary>
    internal int ProfileBitsSet => _profBits.Count(b => b != 0);
}
