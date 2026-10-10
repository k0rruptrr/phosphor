using Phosphor.Core;

namespace Phosphor.Overlays;

/// <summary>
/// HUD: техническая разметка поверх любой сцены (Teenage Engineering / швейцарский технический дизайн).
/// Меняет Ch и Fg ячеек; Bg не трогает, кроме свотчей палитры. Безопасная зона ЭЛТ:
/// x в [2, cols-3], y в [1, rows-2]; всё, что вне неё, не рисуется.
/// Яркость ячейки: luma(fg) для непробельных + max(0, luma(bg) - медиана luma(bg) - 0.15).
/// Трекинг целей: тайлы 8x4, связная компонента ярких тайлов вокруг кандидата, bbox по ячейкам.
/// Статические элементы прячутся, если их прямоугольник в сцене занят; появляются после 1 с чистоты.
/// </summary>
public sealed class HudOverlay : IOverlay, IOverlayParams
{
    public string Id => "hud";

    /// <summary>true — сцена плотная (непробельных &gt; 25%): статические элементы рисуются на плашке, не прячутся.</summary>
    public bool Dense => _dense;

    // Параметры overlay.params.hud (см. Configure). Имена — контракт с окном настроек.
    private static readonly SceneParamSpec[] ParamSpecs =
    {
        new("accent", SceneParamKind.Choice, 0f, 0f, 3f, "цвет акцента", new[] { "orange", "cyan", "green", "white" }),
        new("targets", SceneParamKind.Int, 4f, 0f, 4f, "цели трекинга (0 — нет)"),
        new("stats", SceneParamKind.Bool, 1f, 0f, 1f, "строка SEC (сцена)"),
        new("clock", SceneParamKind.Bool, 1f, 0f, 1f, "часы и REC"),
        new("ruler", SceneParamKind.Bool, 1f, 0f, 1f, "линейка слева"),
        new("frame", SceneParamKind.Bool, 1f, 0f, 1f, "строка FRM (кадр, FPS)"),
        new("swatches", SceneParamKind.Bool, 1f, 0f, 1f, "свотчи палитры"),
    };

    public IReadOnlyList<SceneParamSpec> Params => ParamSpecs;

    private static readonly Rgba DefaultAccent = Rgba.Hex(0xFF6600);
    private Rgba _accent = DefaultAccent;
    private int _maxTargets = MaxTargets;
    private readonly bool[] _enabled = new bool[GateCount];   // статические элементы: включены ли (по умолчанию все)
    private static readonly Rgba Label = Rgba.Hex(0x7A7A7A);
    private static readonly Rgba Secondary = Rgba.Hex(0xBDBDBD);

    private const int SafeX = 2;
    private const int SafeY = 1;
    private const int TileW = 8;
    private const int TileH = 4;
    private const int MaxTargets = 4;
    private const int MaxCandidates = 8;
    private const float MatchDist = 12f;
    private const float MatchDist2 = MatchDist * MatchDist;
    private const double SpawnCooldown = 0.5;
    private const double TargetMinLife = 1.5;
    private const double TargetGone = 0.4;
    private const float CaptureTime = 0.3f;
    private const float SizeTau = 0.2f;              // размер bbox
    private const float CenterTau = 0.05f;           // позиция центра
    private const float VelTau = 0.15f;              // скорость центра (EMA)
    private const float LeadTime = 0.05f;            // упреждение: скорость × 0.05 с
    private const double StaleTime = 0.2;            // без измерения дольше — без подписи
    private const float DenseFraction = 0.25f;       // доля непробельных ячеек: плотная сцена
    private const float BrightThreshold = 0.35f;
    private const float BgMargin = 0.15f;
    private const float ComponentAreaLimit = 0.4f;   // компонента больше этой доли сетки — фон
    private const float SwatchMin = 0.25f;
    private const double StatsPeriod = 1.0;
    private const double BlinkPeriod = 1.0;
    private const float HandoffThreshold = 0.5f;
    private const float GateHold = 1f;               // с чистоты подряд, чтобы элемент появился
    private const int MedBins = 64;
    private const int LeaderLen = 4;
    private const string HexDigits = "0123456789ABCDEF";
    private const string HandoffText = "SIGNAL HANDOFF";

    // Индексы статических элементов для гейтинга.
    private const int GateSec = 0, GateClock = 1, GateRuler = 2, GateFrm = 3, GateSwatch = 4, GateCount = 5;

    private sealed class Target
    {
        public bool Alive;
        public bool Matched;
        public int Id;
        public float Score;                       // балл тайла (сумма яркостей)
        public float MeasX0, MeasY0, MeasX1, MeasY1;   // последний измеренный bbox, ячейки (включительно)
        public float CX, CY;                      // центр последнего измерения
        public float CenterX, CenterY;            // сглаженный центр (tau 0.05)
        public float VX, VY;                      // скорость центра (EMA tau 0.15), ячеек/с
        public float LastCX, LastCY;              // центр измерения на прошлом Update
        public float HalfW, HalfH;                // сглаженный полуразмер (tau 0.2)
        public float Age, Unseen, CapAge;         // секунды: с появления, без измерения, с захвата
    }

    private readonly Target[] _targets = new Target[MaxTargets];

    private int _cols, _rows;
    private double _time;                         // накопленное время (Update)
    private float _dt;                            // последний dt (Update)
    private float[] _bright = Array.Empty<float>();   // яркость ячейки
    private float _median;                        // медиана luma(bg) по сетке
    private readonly int[] _medBins = new int[MedBins];
    private int _tilesX, _tilesY;
    private float[] _tileScore = Array.Empty<float>();
    private float[] _sortBuf = Array.Empty<float>();
    private int[] _candIdx = Array.Empty<int>();
    private float[] _candScore = Array.Empty<float>();
    private int[] _mark = Array.Empty<int>();     // серия компоненты, которой принадлежит тайл
    private int[] _queue = Array.Empty<int>();
    private int _serial;
    private readonly float[] _pcx = new float[MaxCandidates];
    private readonly float[] _pcy = new float[MaxCandidates];
    private float _frameMax;
    private bool _dense;
    private bool _keepScene;   // true во время отрисовки целей: не затирать сцену на краевых строках

    private readonly bool[] _vis = new bool[GateCount];
    private readonly float[] _clearFor = new float[GateCount];

    private readonly int[] _hist = new int[4096];
    private readonly Rgba[] _swatch = new Rgba[4];
    private int _swatchCount;
    private double _statsDue;

    private int _nextId;
    private double _lastSpawn;

    private readonly char[] _buf = new char[128];
    private int _bn;

    public HudOverlay()
    {
        for (int i = 0; i < _targets.Length; i++) _targets[i] = new Target();
        Array.Fill(_enabled, true);
    }

    /// <summary>Применяет overlay.params.hud. Вызывается хостом до Init; без вызова — значения по умолчанию.</summary>
    public void Configure(SceneOptions options)
    {
        _accent = options.GetString("accent", "orange") switch
        {
            "cyan" => Rgba.Hex(0x00C8FF),
            "green" => Rgba.Hex(0x39FF14),
            "white" => Rgba.Hex(0xF0F0F0),
            _ => DefaultAccent,
        };
        _maxTargets = Math.Clamp(options.GetInt("targets", MaxTargets), 0, MaxTargets);
        _enabled[GateSec] = options.GetBool("stats", true);
        _enabled[GateClock] = options.GetBool("clock", true);
        _enabled[GateRuler] = options.GetBool("ruler", true);
        _enabled[GateFrm] = options.GetBool("frame", true);
        _enabled[GateSwatch] = options.GetBool("swatches", true);
    }

    public void Init(int cols, int rows, Random rng)
    {
        Resize(cols, rows);
        _time = 0;
        _dt = 0f;
        _nextId = 1;
        _lastSpawn = double.NegativeInfinity;
        _statsDue = 0;
        _swatchCount = 0;
        _frameMax = 0f;
        Array.Clear(_vis);
        Array.Clear(_clearFor);
    }

    /// <summary>Измеренный bbox главной цели (без сглаживания), для проверок. false — цели нет.</summary>
    public bool TryGetMainBox(out int x0, out int y0, out int x1, out int y1)
    {
        Target? main = null;
        foreach (var tg in _targets)
        {
            if (!tg.Alive) continue;
            if (main == null || tg.Score > main.Score) main = tg;
        }
        if (main == null)
        {
            x0 = y0 = x1 = y1 = 0;
            return false;
        }
        x0 = (int)MathF.Round(main.MeasX0);
        y0 = (int)MathF.Round(main.MeasY0);
        x1 = (int)MathF.Round(main.MeasX1);
        y1 = (int)MathF.Round(main.MeasY1);
        return true;
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) dt = 0f;
        _dt = dt;
        _time += dt;
        float kC = dt > 0f ? 1f - MathF.Exp(-dt / CenterTau) : 0f;
        float kV = dt > 0f ? 1f - MathF.Exp(-dt / VelTau) : 0f;
        float kS = dt > 0f ? 1f - MathF.Exp(-dt / SizeTau) : 0f;

        for (int i = 0; i < _targets.Length; i++)
        {
            Target t = _targets[i];
            if (!t.Alive) continue;
            t.Age += dt;
            t.Unseen += dt;
            t.CapAge = MathF.Min(t.CapAge + dt, CaptureTime);

            float mcx = (t.MeasX0 + t.MeasX1) * 0.5f, mcy = (t.MeasY0 + t.MeasY1) * 0.5f;
            float mhw = (t.MeasX1 - t.MeasX0) * 0.5f, mhh = (t.MeasY1 - t.MeasY0) * 0.5f;
            if (dt > 0f)
            {
                t.VX += ((mcx - t.LastCX) / dt - t.VX) * kV;
                t.VY += ((mcy - t.LastCY) / dt - t.VY) * kV;
            }
            t.LastCX = mcx;
            t.LastCY = mcy;
            t.CenterX += (mcx - t.CenterX) * kC;
            t.CenterY += (mcy - t.CenterY) * kC;
            t.HalfW += (mhw - t.HalfW) * kS;
            t.HalfH += (mhh - t.HalfH) * kS;

            if (t.Age >= TargetMinLife && t.Unseen >= TargetGone) t.Alive = false;
        }
    }

    public void Apply(CharGrid grid, in OverlayContext ctx)
    {
        // Редкий путь: сетка другого размера, чем в Init (буферы пересоздаются).
        if (grid.Cols != _cols || grid.Rows != _rows) Resize(grid.Cols, grid.Rows);

        Brightness(grid);
        if (_time >= _statsDue)
        {
            _statsDue = _time + StatsPeriod;
            Stats(grid);
        }
        if (_maxTargets > 0) Track();   // targets=0: трекинг не считается

        // Гейтинг проверяет сцену до того, как HUD что-то нарисует в этом кадре.
        ComputeGates(grid, in ctx);

        DrawTopLeft(grid, in ctx);
        DrawTopRight(grid, in ctx);
        DrawBottomLeft(grid, in ctx);
        DrawSwatches(grid);
        DrawRuler(grid);

        if (ctx.Organism.Transition > HandoffThreshold) DrawHandoff(grid);
        else
        {
            _keepScene = true;
            DrawTargets(grid);
            _keepScene = false;
        }
    }

    // ---------- Подготовка буферов ----------

    private void Resize(int cols, int rows)
    {
        _cols = cols;
        _rows = rows;
        _bright = new float[cols * rows];
        _tilesX = (cols + TileW - 1) / TileW;
        _tilesY = (rows + TileH - 1) / TileH;
        int nt = _tilesX * _tilesY;
        _tileScore = new float[nt];
        _sortBuf = new float[nt];
        _candIdx = new int[nt];
        _candScore = new float[nt];
        _mark = new int[nt];
        _queue = new int[nt];
        _serial = 0;
        for (int i = 0; i < _targets.Length; i++) _targets[i].Alive = false;
    }

    private static float Luma(Rgba c) => (0.299f * c.R + 0.587f * c.G + 0.114f * c.B) / 255f;

    private void Brightness(CharGrid grid)
    {
        var cells = grid.Cells;

        // Медиана luma(bg) через гистограмму на 64 корзины (без аллокаций).
        Array.Clear(_medBins);
        for (int i = 0; i < cells.Length; i++)
        {
            int b = (int)(Luma(cells[i].Bg) * MedBins);
            if (b < 0) b = 0;
            else if (b >= MedBins) b = MedBins - 1;
            _medBins[b]++;
        }
        int half = cells.Length / 2, acc = 0, medBin = 0;
        for (int b = 0; b < MedBins; b++)
        {
            acc += _medBins[b];
            if (acc > half) { medBin = b; break; }
        }
        _median = (medBin + 0.5f) / MedBins;

        int nonSpace = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i].Ch != ' ') nonSpace++;
        }
        _dense = nonSpace > DenseFraction * cells.Length;

        for (int i = 0; i < cells.Length; i++)
        {
            Cell c = cells[i];
            float v = MathF.Max(0f, Luma(c.Bg) - _median - BgMargin);
            if (c.Ch != ' ') v += Luma(c.Fg);
            _bright[i] = v;
        }
    }

    /// <summary>Раз в секунду: четыре самых частых ярких цвета Fg (квантование 4 бит на канал) и самый частый.</summary>
    private void Stats(CharGrid grid)
    {
        Array.Clear(_hist);
        var cells = grid.Cells;
        for (int i = 0; i < cells.Length; i++)
        {
            Cell c = cells[i];
            if (c.Ch == ' ') continue;
            int mx = Math.Max(c.Fg.R, Math.Max(c.Fg.G, c.Fg.B));
            if (mx <= SwatchMin * 255f) continue;
            int idx = ((c.Fg.R >> 4) << 8) | ((c.Fg.G >> 4) << 4) | (c.Fg.B >> 4);
            _hist[idx]++;
        }

        _swatchCount = 0;
        for (int k = 0; k < _swatch.Length; k++)
        {
            int best = -1, bestN = 0;
            for (int b = 0; b < _hist.Length; b++)
            {
                if (_hist[b] > bestN) { bestN = _hist[b]; best = b; }
            }
            if (best < 0) break;
            _swatch[k] = new Rgba((byte)(((best >> 8) & 15) * 17), (byte)(((best >> 4) & 15) * 17), (byte)((best & 15) * 17));
            _hist[best] = 0;
            _swatchCount++;
        }
    }

    // ---------- Гейтинг статических элементов ----------

    /// <summary>Прямоугольник занят, если в нём есть непробельная ячейка или фон заметно ярче медианы.</summary>
    private bool RectClear(CharGrid g, int x0, int y0, int x1, int y1)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        x1 = Math.Min(g.Cols - 1, x1); y1 = Math.Min(g.Rows - 1, y1);
        float limit = _median + BgMargin;
        for (int y = y0; y <= y1; y++)
        {
            int row = y * g.Cols;
            for (int x = x0; x <= x1; x++)
            {
                Cell c = g.Cells[row + x];
                if (c.Ch != ' ') return false;
                if (Luma(c.Bg) > limit) return false;
            }
        }
        return true;
    }

    private void Gate(int i, bool clear)
    {
        if (!clear)
        {
            _clearFor[i] = 0f;
            _vis[i] = false;
            return;
        }
        _clearFor[i] += _dt;
        if (_clearFor[i] >= GateHold) _vis[i] = true;
    }

    private void ComputeGates(CharGrid g, in OverlayContext ctx)
    {
        if (_dense)
        {
            // Плотная сцена: своего HUD здесь нет, элементы рисуются на плашке.
            for (int i = 0; i < GateCount; i++)
            {
                _vis[i] = _enabled[i];
                _clearFor[i] = 0f;
            }
            return;
        }

        // Выключенный элемент не участвует в гейтинге (RectClear не вызывается).
        // Ширины прямоугольников: SEC — 12 + id сцены; FRM — с запасом под BPM; свотчи — hex + свотчи.
        int secW = 12 + ctx.SceneId.Length;
        Gate(GateSec, _enabled[GateSec] && RectClear(g, SafeX, SafeY, SafeX + secW - 1, SafeY));

        int clockX = _cols - 3 - 15 + 1;
        Gate(GateClock, _enabled[GateClock] && RectClear(g, clockX, SafeY, clockX + 14, SafeY));

        Gate(GateRuler, _enabled[GateRuler] && RectClear(g, SafeX, 3, SafeX + 3, _rows - 4));

        Gate(GateFrm, _enabled[GateFrm] && RectClear(g, SafeX, _rows - 2, SafeX + 30, _rows - 2));

        int sx = _cols - 3 - 14;
        Gate(GateSwatch, _enabled[GateSwatch] && RectClear(g, sx - 8, _rows - 2, _cols - 3, _rows - 2));
    }

    // ---------- Трекинг целей ----------

    private void Track()
    {
        int cols = _cols, rows = _rows, tx = _tilesX, ty = _tilesY, n = tx * ty;

        for (int j = 0; j < ty; j++)
        {
            for (int i = 0; i < tx; i++)
            {
                int x0 = i * TileW, y0 = j * TileH;
                int x1 = Math.Min(cols, x0 + TileW), y1 = Math.Min(rows, y0 + TileH);
                float s = 0f;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * cols;
                    for (int x = x0; x < x1; x++) s += _bright[row + x];
                }
                _tileScore[j * tx + i] = s;
            }
        }

        Array.Copy(_tileScore, _sortBuf, n);
        Array.Sort(_sortBuf, 0, n);
        float p60 = _sortBuf[(int)(0.6f * (n - 1))];
        _frameMax = _sortBuf[n - 1];

        int nc = 0;
        for (int j = 0; j < ty; j++)
        {
            for (int i = 0; i < tx; i++)
            {
                int t = j * tx + i;
                float s = _tileScore[t];
                if (s <= 0f || s <= p60) continue;
                bool peak = true;
                for (int dj = -1; dj <= 1 && peak; dj++)
                {
                    int jj = j + dj;
                    if (jj < 0 || jj >= ty) continue;
                    for (int di = -1; di <= 1; di++)
                    {
                        int ii = i + di;
                        if (ii < 0 || ii >= tx) continue;
                        if (_tileScore[jj * tx + ii] > s) { peak = false; break; }
                    }
                }
                if (peak)
                {
                    _candIdx[nc] = t;
                    _candScore[nc] = s;
                    nc++;
                }
            }
        }
        if (nc > 0) Array.Sort(_candScore, _candIdx, 0, nc);

        foreach (var tg in _targets) tg.Matched = false;

        int frameStart = _serial + 1;   // компоненты, созданные в этом кадре, имеют серию >= frameStart
        int np = 0, processed = 0;
        for (int k = nc - 1; k >= 0; k--)
        {
            int t = _candIdx[k];
            float sc = _candScore[k];
            if (_mark[t] >= frameStart) continue;     // тайл уже в компоненте этого кадра
            if (processed >= MaxCandidates) break;
            processed++;
            if (!ComponentBbox(t, p60, out int bx0, out int by0, out int bx1, out int by1)) continue;

            float cx = (bx0 + bx1 + 1) * 0.5f, cy = (by0 + by1 + 1) * 0.5f;

            bool dup = false;
            for (int q = 0; q < np; q++)
            {
                float dx = _pcx[q] - cx, dy = _pcy[q] - cy;
                if (dx * dx + dy * dy <= MatchDist2) { dup = true; break; }
            }
            if (dup) continue;
            _pcx[np] = cx;
            _pcy[np] = cy;
            np++;

            Target? best = null;
            float bd2 = MatchDist2;
            foreach (var tg in _targets)
            {
                if (!tg.Alive || tg.Matched) continue;
                float dx = tg.CX - cx, dy = tg.CY - cy;
                float d2 = dx * dx + dy * dy;
                if (d2 <= bd2) { bd2 = d2; best = tg; }
            }

            if (best != null)
            {
                best.Matched = true;
                SetMeasure(best, bx0, by0, bx1, by1, cx, cy, sc);
                best.Unseen = 0;
                continue;
            }

            if (_time - _lastSpawn < SpawnCooldown) continue;
            Target? slot = null;
            int alive = 0;
            foreach (var tg in _targets)
            {
                if (tg.Alive) alive++;
                else if (slot == null) slot = tg;
            }
            if (slot == null || alive >= _maxTargets) continue;

            slot.Alive = true;
            slot.Matched = true;
            slot.Id = _nextId++;
            slot.Age = 0f;
            slot.Unseen = 0f;
            slot.CapAge = 0f;
            SetMeasure(slot, bx0, by0, bx1, by1, cx, cy, sc);
            slot.CenterX = (slot.MeasX0 + slot.MeasX1) * 0.5f;
            slot.CenterY = (slot.MeasY0 + slot.MeasY1) * 0.5f;
            slot.LastCX = slot.CenterX;
            slot.LastCY = slot.CenterY;
            slot.VX = 0f;
            slot.VY = 0f;
            slot.HalfW = (slot.MeasX1 - slot.MeasX0) * 0.5f;
            slot.HalfH = (slot.MeasY1 - slot.MeasY0) * 0.5f;
            _lastSpawn = _time;
        }
    }

    private static void SetMeasure(Target tg, int x0, int y0, int x1, int y1, float cx, float cy, float score)
    {
        tg.MeasX0 = x0; tg.MeasY0 = y0; tg.MeasX1 = x1; tg.MeasY1 = y1;
        tg.CX = cx; tg.CY = cy;
        tg.Score = score;
    }

    private int TileArea(int ci, int cj)
    {
        int x0 = ci * TileW, y0 = cj * TileH;
        return (Math.Min(_cols, x0 + TileW) - x0) * (Math.Min(_rows, y0 + TileH) - y0);
    }

    /// <summary>
    /// Связная компонента ярких тайлов (балл &gt; p60, BFS по 8 соседям), содержащая seed.
    /// Компонента больше 40% сетки — фон, отбрасывается. Внутри компоненты bbox по ячейкам яркости &gt; 0.35.
    /// </summary>
    private bool ComponentBbox(int seed, float p60, out int bx0, out int by0, out int bx1, out int by1)
    {
        int tx = _tilesX, ty = _tilesY;
        int serial = ++_serial;
        float areaLimit = ComponentAreaLimit * _cols * _rows;

        _mark[seed] = serial;
        _queue[0] = seed;
        int head = 0, tail = 1;
        float area = 0f;
        int ti0 = int.MaxValue, ti1 = -1, tj0 = int.MaxValue, tj1 = -1;

        while (head < tail)
        {
            int cur = _queue[head++];
            int ci = cur % tx, cj = cur / tx;
            area += TileArea(ci, cj);
            if (area > areaLimit)
            {
                bx0 = by0 = bx1 = by1 = 0;
                return false;
            }
            if (ci < ti0) ti0 = ci;
            if (ci > ti1) ti1 = ci;
            if (cj < tj0) tj0 = cj;
            if (cj > tj1) tj1 = cj;

            for (int dj = -1; dj <= 1; dj++)
            {
                int nj = cj + dj;
                if (nj < 0 || nj >= ty) continue;
                for (int di = -1; di <= 1; di++)
                {
                    int ni = ci + di;
                    if (ni < 0 || ni >= tx) continue;
                    int nIdx = nj * tx + ni;
                    if (_mark[nIdx] == serial) continue;
                    if (_tileScore[nIdx] > p60 && _tileScore[nIdx] > 0f)
                    {
                        _mark[nIdx] = serial;
                        _queue[tail++] = nIdx;
                    }
                }
            }
        }

        int mnx = int.MaxValue, mny = int.MaxValue, mxx = int.MinValue, mxy = int.MinValue;
        for (int tj = tj0; tj <= tj1; tj++)
        {
            for (int ti = ti0; ti <= ti1; ti++)
            {
                if (_mark[tj * tx + ti] != serial) continue;
                int x0 = ti * TileW, y0 = tj * TileH;
                int x1 = Math.Min(_cols, x0 + TileW) - 1, y1 = Math.Min(_rows, y0 + TileH) - 1;
                for (int y = y0; y <= y1; y++)
                {
                    int row = y * _cols;
                    for (int x = x0; x <= x1; x++)
                    {
                        if (_bright[row + x] <= BrightThreshold) continue;
                        if (x < mnx) mnx = x;
                        if (x > mxx) mxx = x;
                        if (y < mny) mny = y;
                        if (y > mxy) mxy = y;
                    }
                }
            }
        }

        if (mnx > mxx)
        {
            bx0 = by0 = bx1 = by1 = 0;
            return false;
        }
        // Полосы (песок, рамки текста на всю ширину/высоту) — фон, а не цель.
        if (mxx - mnx + 1 > 0.6f * _cols || mxy - mny + 1 > 0.6f * _rows)
        {
            bx0 = by0 = bx1 = by1 = 0;
            return false;
        }
        bx0 = mnx; by0 = mny; bx1 = mxx; by1 = mxy;
        return true;
    }

    // ---------- Отрисовка ----------

    private bool Safe(int x, int y) => x >= SafeX && x <= _cols - 1 - SafeX && y >= SafeY && y <= _rows - 1 - SafeY;

    private void Mark(CharGrid g, int x, int y, char ch, Rgba fg)
    {
        if (!Safe(x, y)) return;
        // Уголки и подписи целей не затирают непробельные ячейки сцены в строках y ≤ 1 и y ≥ rows-2.
        if (_keepScene && (y <= SafeY || y >= _rows - 2) && g.Cells[y * g.Cols + x].Ch != ' ') return;
        g.Put(x, y, ch, fg);
    }

    private void Str(CharGrid g, int x, int y, string s, Rgba fg)
    {
        for (int i = 0; i < s.Length; i++) Mark(g, x + i, y, s[i], fg);
    }

    /// <summary>Плашка под элементом в плотной сцене: Bg чёрный на [x0-1, x1+1] в строке y.</summary>
    private void Plate(CharGrid g, int x0, int y, int x1)
    {
        if (!_dense || y < 0 || y >= g.Rows) return;
        int a = Math.Max(0, x0 - 1), b = Math.Min(g.Cols - 1, x1 + 1);
        for (int x = a; x <= b; x++) g.Cells[y * g.Cols + x].Bg = Rgba.Black;
    }

    private void PutBuf(CharGrid g, int x, int y, Rgba fg)
    {
        for (int i = 0; i < _bn; i++) Mark(g, x + i, y, _buf[i], fg);
    }

    private void BReset() => _bn = 0;

    private void BChar(char c)
    {
        if (_bn < _buf.Length) _buf[_bn++] = c;
    }

    private void BStr(string s)
    {
        for (int i = 0; i < s.Length; i++) BChar(s[i]);
    }

    /// <summary>Неотрицательное число, не короче width цифр (с ведущими нулями). Без аллокаций.</summary>
    private void BInt(long v, int width)
    {
        if (v < 0) v = 0;
        int digits = 1;
        for (long t = v; t >= 10; t /= 10) digits++;
        int total = Math.Max(width, digits);
        long pow = 1;
        for (int i = 1; i < total; i++) pow *= 10;
        for (int i = 0; i < total; i++)
        {
            BChar((char)('0' + (int)((v / pow) % 10)));
            pow /= 10;
        }
    }

    private void DrawTopLeft(CharGrid g, in OverlayContext ctx)
    {
        if (!_vis[GateSec]) return;
        BReset();
        BStr("SEC ");
        BInt(ctx.MonitorIndex + 1, 2);
        BChar('/');
        BInt(ctx.MonitorCount, 2);
        BStr(" · ");
        foreach (char c in ctx.SceneId) BChar(char.ToUpperInvariant(c));
        Plate(g, SafeX, SafeY, SafeX + _bn - 1);
        PutBuf(g, SafeX, SafeY, Label);
    }

    private void DrawTopRight(CharGrid g, in OverlayContext ctx)
    {
        if (!_vis[GateClock]) return;
        // Строка «HH:mm:ss  ● REC» (15 символов), последний символ — в колонке cols-3.
        int x0 = _cols - 3 - 15 + 1;
        BReset();
        if (ctx.Clock is { } clk)
        {
            BInt(clk.Hour, 2);
            BChar(':');
            BInt(clk.Minute, 2);
            BChar(':');
            BInt(clk.Second, 2);
        }
        else BStr("--:--:--");
        Plate(g, x0, SafeY, x0 + 14);
        PutBuf(g, x0, SafeY, Label);

        Rgba recFg = BlinkOn() ? _accent : Label;
        Mark(g, x0 + 10, SafeY, '●', recFg);
        Str(g, x0 + 12, SafeY, "REC", recFg);
    }

    private void DrawBottomLeft(CharGrid g, in OverlayContext ctx)
    {
        if (!_vis[GateFrm]) return;
        BReset();
        BStr("FRM ");
        BInt(ctx.Frame % 1000000, 6);
        BStr(" · FPS ");
        int tenths = (int)MathF.Round(MathF.Max(0f, ctx.Fps) * 10f);
        BInt(tenths / 10, 1);
        BChar('.');
        BInt(tenths % 10, 1);
        if (ctx.Audio.Active && ctx.Audio.Bpm > 0f)
        {
            BStr(" · BPM ");
            BInt((long)MathF.Round(ctx.Audio.Bpm), 1);
        }
        Plate(g, SafeX, _rows - 2, SafeX + _bn - 1);
        PutBuf(g, SafeX, _rows - 2, Label);
    }

    /// <summary>Справа: hex самого частого цвета серым, затем четыре свотча «█» (3 ячейки каждый, зазор 1).</summary>
    private void DrawSwatches(CharGrid g)
    {
        if (!_vis[GateSwatch]) return;
        int y = _rows - 2;
        int sx = _cols - 3 - 14;
        for (int s = 0; s < _swatch.Length; s++)
        {
            if (s >= _swatchCount) break;
            Rgba c = _swatch[s];
            for (int k = 0; k < 3; k++)
            {
                int x = sx + s * 4 + k;
                if (Safe(x, y)) g.Put(x, y, '█', c, c);
            }
        }

        if (_swatchCount > 0)
        {
            Rgba d = _swatch[0];
            BReset();
            BChar('#');
            BChar(HexDigits[(d.R >> 4) & 15]); BChar(HexDigits[d.R & 15]);
            BChar(HexDigits[(d.G >> 4) & 15]); BChar(HexDigits[d.G & 15]);
            BChar(HexDigits[(d.B >> 4) & 15]); BChar(HexDigits[d.B & 15]);
            Plate(g, sx - 8, y, sx - 8 + _bn - 1);
            PutBuf(g, sx - 8, y, Label);
        }
    }

    /// <summary>Линейка по левому краю: каждые 3 строки «├», каждые 9 — «┼» и номер строки (2 цифры).</summary>
    private void DrawRuler(CharGrid g)
    {
        if (!_vis[GateRuler]) return;
        Rgba rc = Label.Scale(0.6f);
        // На плотной сцене подписи светлее и без затемнения: читаются на ярком фоне.
        Rgba lc = _dense ? Rgba.Hex(0xA8A8A8) : rc;
        for (int y = 3; y <= _rows - 4; y++)
        {
            if (y % 9 == 0)
            {
                // Плашка только под подписью числа: засечка + число + 1 клетка запаса справа (свечение).
                Plate(g, SafeX, y, SafeX + 4);
                Mark(g, SafeX, y, '┼', lc);
                BReset();
                BInt(y % 100, 2);
                PutBuf(g, SafeX + 2, y, lc);
            }
            else if (y % 3 == 0)
            {
                Mark(g, SafeX, y, '├', rc);
            }
        }
    }

    private void DrawHandoff(CharGrid g)
    {
        if (!BlinkOn()) return;
        Str(g, (_cols - HandoffText.Length) / 2, _rows / 2, HandoffText, _accent);
    }

    private void DrawTargets(CharGrid g)
    {
        int budget = _cols * _rows * 6 / 100;   // не больше 6% ячеек под разметку целей
        if (budget <= 0) return;

        Target? main = null;
        foreach (var tg in _targets)
        {
            if (!tg.Alive) continue;
            if (main == null || tg.Score > main.Score) main = tg;
        }
        if (main == null) return;

        DrawTarget(g, main, true, ref budget);
        foreach (var tg in _targets)
        {
            if (!tg.Alive || tg == main) continue;
            DrawTarget(g, tg, false, ref budget);
        }
    }

    private void DrawTarget(CharGrid g, Target tg, bool main, ref int budget)
    {
        const int frameCost = 8;
        if (budget < frameCost) return;

        // Отображаемая рамка: центр с упреждением (скорость × 0.05 с), полуразмер сглажен отдельно.
        float cx = tg.CenterX + tg.VX * LeadTime, cy = tg.CenterY + tg.VY * LeadTime;
        float fx0 = cx - tg.HalfW, fx1 = cx + tg.HalfW, fy0 = cy - tg.HalfH, fy1 = cy + tg.HalfH;

        // Захват: уголки стартуют в 2 раза дальше от рамки и съезжаются за CaptureTime.
        float m = 1f + MathF.Max(0f, 1f - tg.CapAge / CaptureTime);
        int x0 = (int)MathF.Round(fx0 - m), x1 = (int)MathF.Round(fx1 + m);
        int y0 = (int)MathF.Round(fy0 - m), y1 = (int)MathF.Round(fy1 + m);
        // Рамка зажата в безопасную зону, чтобы уголки оставались видимыми (подпись стоит под правым уголком).
        x0 = Math.Max(x0, SafeX);
        x1 = Math.Min(x1, _cols - 3);
        y0 = Math.Max(y0, SafeY);
        y1 = Math.Min(y1, _rows - 2);
        if (x1 < x0 + 3) x0 = Math.Max(SafeX, x1 - 3);
        if (y1 < y0 + 2) y0 = Math.Max(SafeY, y1 - 2);

        Rgba fg = main ? _accent : Secondary;
        Mark(g, x0, y0, '┌', fg); Mark(g, x0 + 1, y0, '─', fg);
        Mark(g, x1 - 1, y0, '─', fg); Mark(g, x1, y0, '┐', fg);
        Mark(g, x0, y1, '└', fg); Mark(g, x0 + 1, y1, '─', fg);
        Mark(g, x1 - 1, y1, '─', fg); Mark(g, x1, y1, '┘', fg);
        budget -= frameCost;

        // Подпись и выноска — только у цели с уголками в этом кадре и с недавним измерением.
        if (tg.Unseen > StaleTime) return;

        float norm = _frameMax > 0f ? Math.Clamp(tg.Score / _frameMax, 0f, 1f) : 0f;
        int v = (int)MathF.Round(norm * 100f);
        BReset();
        BStr("TGT-");
        BInt(tg.Id, 2);
        BStr(" · ");
        BInt(v / 100, 1);
        BChar('.');
        BInt(v % 100, 2);
        int len = _bn;

        // Подпись под правым нижним уголком; если не влезает справа — сдвиг влево; совсем не влезает — не рисуем.
        int labelY = y1 + 1;
        int lx = Math.Min(x1 - len + 1, _cols - 3 - len + 1);
        bool labelFits = lx >= SafeX && lx + len - 1 <= _cols - 3 && Safe(lx, labelY) && Safe(lx + len - 1, labelY);

        if (main)
        {
            if (labelFits && budget >= len)
            {
                PutBuf(g, lx, labelY, _accent);
                budget -= len;
            }
            int lockCost = LeaderLen + 4;
            if (budget >= lockCost)
            {
                for (int i = 1; i <= LeaderLen; i++) Mark(g, x1 + i, y0, '─', _accent);
                Str(g, x1 + 1, y0 - 1, "LOCK", Label);
                budget -= lockCost;
            }
        }
        else if (labelFits && budget >= len)
        {
            // Подписи второстепенных — только если уложились в бюджет.
            PutBuf(g, lx, labelY, Label);
            budget -= len;
        }
    }

    private bool BlinkOn() => (_time % BlinkPeriod) < BlinkPeriod * 0.5;
}
