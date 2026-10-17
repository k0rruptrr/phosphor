using System.Globalization;
using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// «Пожары»: карта квадрата ±RadiusKm вокруг города (NASA FIRMS, VIIRS NOAA-20, 24 ч). Равнопромежуточная проекция
/// с поправкой cos(широты) по долготе; пропорция клетки — из SceneOptions.CellAspect (высота/ширина), квадрат квадратный
/// в пикселях. Сетка тусклая (через 2°). Очаги — «●» яркостью ≥ 0.85, у сильных (FRP &gt; 20) — «+» в соседних клетках;
/// свежие (за 6 ч до выборки) — с ореолом. Топ-5 и счётчики — по очагам (засечки ближе MergeKm склеиваются).
/// Кольца 100/250/500 км — пунктир секстантами; подписи колец на тёмной плашке, ставятся там, где нет очагов. Сканер — вертикальная линия: очаги вспыхивают при пересечении; рамка захвата
/// перескакивает по топ-5. Статика строится при снимке, Draw только копирует её и накладывает очаги. Без данных —
/// сетка без центра и градусов и «NO FIRMS DATA».
/// </summary>
public sealed class FiresScene : IScene, IDataScene, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec ScanSpec = new("scanSec", SceneParamKind.Float, 14f, 4f, 60f, "с на проход сканера");
    public static readonly SceneParamSpec FlickerSpec = new("flicker", SceneParamKind.Float, 0.6f, 0f, 1f, "мерцание очагов");
    private static readonly SceneParamSpec[] ParamList = { ScanSpec, FlickerSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    public string Id => "fires";

    private static readonly string[] Keys = { "fires" };
    public IReadOnlyList<string> DataKeys => Keys;

    private const double KmPerDeg = 111.0;
    private const double DefaultRadiusKm = 250.0;
    private const int GridStepDeg = 2;
    private static readonly double[] RingKm = { 100, 250, 500 };
    private static readonly int[] RingAngles = { 45, 225, 135, 315, 20, 200, 160, 340, 80, 260, 110, 290 };
    private const int PanelMinCols = 72, PanelMinW = 26, PanelMaxW = 34;
    private const int MaxTop = 5;
    private const double MergeKm = 3.0;     // засечки ближе — один очаг
    private const double FreshSec = 6 * 3600.0;
    private const double HopSec = 3.0;
    private const float FlashDecay = 2.2f;
    private const float Tau = 6.2831853f;
    private const float StrongFrp = 20f;
    private const string HeaderText = "FIRMS · VIIRS NOAA-20 · 24H";
    private const string NoDataText = "NO FIRMS DATA";
    private const string NoFiresText = "ОЧАГОВ НЕ ОБНАРУЖЕНО";
    private const char CoreGlyph = '●';
    private const char StrongGlyph = '+';
    private const char HaloGlyph = '·';
    private static readonly char Dot = GlyphSet.Sextant(1);   // одиночный подпиксель секстанта: сетка и кольца

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly Rgba Accent = Rgba.Hex(0xFF6600);
    private static readonly Rgba Warm = Rgba.Hex(0xF2EEE6);
    private static readonly Rgba Dim = Rgba.Hex(0x5E5A52);
    private static readonly Rgba Faint = Rgba.Lerp(Rgba.Black, Rgba.Hex(0xF2EEE6), 0.18f);   // сетка: ~0.18 яркости
    private static readonly Rgba RingCol = Rgba.Lerp(Rgba.Black, Rgba.Hex(0xF2EEE6), 0.30f); // кольца: ~0.3, между сеткой и очагами
    private static readonly Rgba ScanCol = Rgba.Hex(0x7A3A10);
    private static readonly Rgba FireLow = Rgba.Hex(0x6A1A08);
    private static readonly Rgba FireMid = Rgba.Hex(0xFF6600);
    private static readonly Rgba FireHi = Rgba.Hex(0xFFF4C2);
    private static readonly Rgba Halo = Rgba.Hex(0x2A0E04);

    private int _cols, _rows;
    private float _scanSec = 14f, _flicker = 0.6f;
    private double _aspect = 1.8;     // высота / ширина клетки (SceneOptions.CellAspect)
    private double _t;
    private FireSnapshot? _snap;

    // Статический слой: сетка, кольца, рамка, панель, ореолы, плашки подписей. Draw копирует его и кладёт поверх очаги.
    private CharGrid _base = new(1, 1);
    private int _side, _sideR, _mapX0, _mapY0;
    private double _ccx, _ccy, _kmC, _kmR, _cLat, _cLon, _radKm;
    private int _scanCol;
    private bool _scanReady;
    private int _count;               // очагов в квадрате

    // По клеткам (индекс = row * Cols + col).
    private int[] _cellBits = Array.Empty<int>();       // 1 — в клетке есть очаг
    private int[] _ringBits = Array.Empty<int>();       // биты секстантов колец (временно, при сборке)
    private float[] _cellFrp = Array.Empty<float>();
    private float[] _cellFlash = Array.Empty<float>();  // вспышка от сканера: 1 → 0
    private Rgba[] _cellFg = Array.Empty<Rgba>();
    private bool[] _cellNight = Array.Empty<bool>();
    private bool[] _cellFresh = Array.Empty<bool>();
    private bool[] _cellStrong = Array.Empty<bool>();
    private bool[] _plate = Array.Empty<bool>();        // плашка подписи колец
    private int[] _fireCells = Array.Empty<int>();
    private int _fireCount;

    // Топ-5 по FRP (по убыванию), по очагам; _topN — число засечек в очаге.
    private readonly FirePoint[] _topPts = new FirePoint[MaxTop];
    private readonly float[] _topFrp = new float[MaxTop];
    private readonly int[] _topCol = new int[MaxTop], _topRow = new int[MaxTop], _topN = new int[MaxTop];
    private int _topCount;

    // Буферы Plot (переиспользуются): засечки в квадрате и очаги-кластеры.
    private readonly List<(FirePoint P, float Frp, int Col, int Row)> _dets = new();
    private readonly List<Cluster> _clusters = new();
    private readonly record struct Cluster(FirePoint Seed, float Frp, int Col, int Row, int N);

    // Итоги последней выборки (для панели).
    private int _lastDay, _lastNight;
    private double _lastSum, _lastMax;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = cols;
        _rows = rows;
        _scanSec = Math.Clamp(options.GetFloat(ScanSpec.Name, ScanSpec.Default), ScanSpec.Min, ScanSpec.Max);
        _flicker = Math.Clamp(options.GetFloat(FlickerSpec.Name, FlickerSpec.Default), FlickerSpec.Min, FlickerSpec.Max);
        _aspect = Math.Clamp((double)options.CellAspect, 1.0, 4.0);
        _t = 0;
        _snap = null;
        _scanReady = false;
        _fireCount = 0;
        _topCount = 0;
        Rebuild();
    }

    public void OnData(string key, DataSnapshot snapshot)
    {
        if (key == "fires" && snapshot is FireSnapshot fs)
        {
            _snap = fs;
            Rebuild();
        }
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;
        _t += dt;

        for (int k = 0; k < _fireCount; k++)
        {
            int idx = _fireCells[k];
            if (_cellFlash[idx] > 0f) _cellFlash[idx] = MathF.Max(0f, _cellFlash[idx] - dt * FlashDecay);
        }

        int cur = ScanColAt(_t);
        if (_scanReady && _fireCount > 0 && cur != _scanCol)
            for (int k = 0; k < _fireCount; k++)
            {
                int idx = _fireCells[k];
                if (Crossed(idx % _cols, _scanCol, cur)) _cellFlash[idx] = 1f;
            }
        _scanCol = cur;
        _scanReady = true;
    }

    public void Draw(CharGrid grid)
    {
        if (grid.Cols != _base.Cols || grid.Rows != _base.Rows) return;
        var cells = grid.Cells;
        Array.Copy(_base.Cells, cells, cells.Length);
        int C = grid.Cols;

        // Луч сканера: тонкая линия по карте, не поверх очагов и подписей.
        if (_scanCol >= _mapX0 && _scanCol < _mapX0 + _side && _scanCol < C)
            for (int r = _mapY0; r < _mapY0 + _sideR && r < grid.Rows; r++)
            {
                int idx = r * C + _scanCol;
                if (_cellBits[idx] != 0 || _plate[idx]) continue;
                cells[idx].Ch = '│';
                cells[idx].Fg = ScanCol;
            }

        // Очаги: «●», яркость 0.85..1 (мерцание по шуму времени, у каждой клетки свой фазовый сдвиг), вспышка от сканера.
        float tf = (float)_t;
        for (int k = 0; k < _fireCount; k++)
        {
            int idx = _fireCells[k];
            float f = _cellFlash[idx];
            float ph = ((idx * 0.618034f) % 1f) * Tau;
            float w = 0.55f * MathF.Sin(tf * 3.1f + ph) + 0.3f * MathF.Sin(tf * 7.7f + ph * 2.3f)
                    + 0.15f * MathF.Sin(tf * 13.9f + ph * 0.7f);           // −1..1
            float bright = 1f - 0.15f * _flicker * (0.5f - 0.5f * w);      // 0.85..1
            bright = MathF.Min(1f, bright + f * 0.3f);
            var col = Rgba.Lerp(_cellFg[idx], Rgba.White, f * 0.35f).Scale(bright);
            cells[idx].Ch = CoreGlyph;
            cells[idx].Fg = col;

            int cx = idx % C, cy = idx / C;
            // Тусклый ореол «·» по горизонтали у всех очагов; у сильных поверх него «+» во всех соседях.
            Arm(cells, grid, cx - 1, cy, HaloGlyph, col.Scale(0.4f));
            Arm(cells, grid, cx + 1, cy, HaloGlyph, col.Scale(0.4f));
            if (!_cellStrong[idx]) continue;
            var arm = col.Scale(0.6f);
            Arm(cells, grid, cx - 1, cy, StrongGlyph, arm);
            Arm(cells, grid, cx + 1, cy, StrongGlyph, arm);
            Arm(cells, grid, cx, cy - 1, StrongGlyph, arm);
            Arm(cells, grid, cx, cy + 1, StrongGlyph, arm);
        }

        // Рамка захвата на очаге из топ-5: на самом мощном, затем перескакивает каждые 3 с.
        if (_topCount > 0)
        {
            int k = (int)(_t / HopSec) % _topCount;
            int bc = _topCol[k], br = _topRow[k];
            grid.Put(bc - 2, br - 1, '┌', Accent);
            grid.Put(bc + 2, br - 1, '┐', Accent);
            grid.Put(bc - 2, br + 1, '└', Accent);
            grid.Put(bc + 2, br + 1, '┘', Accent);
        }
    }

    private void Arm(Cell[] cells, CharGrid grid, int x, int y, char ch, Rgba col)
    {
        if (!grid.InBounds(x, y)) return;
        int idx = y * grid.Cols + x;
        if (_cellBits[idx] != 0 || _plate[idx]) return;
        cells[idx].Ch = ch;
        cells[idx].Fg = col;
    }

    // ---------- Сборка статического слоя ----------

    /// <summary>Пересобрать всё, что зависит от размера сетки, центра и снимка. Вызывается из Init и OnData.</summary>
    private void Rebuild()
    {
        int C = _cols, R = _rows;
        if (C < 1 || R < 1) return;
        int n = C * R;
        if (_base.Cols != C || _base.Rows != R) _base = new CharGrid(C, R);
        if (_cellBits.Length != n)
        {
            _cellBits = new int[n];
            _ringBits = new int[n];
            _cellFrp = new float[n];
            _cellFlash = new float[n];
            _cellFg = new Rgba[n];
            _cellNight = new bool[n];
            _cellFresh = new bool[n];
            _cellStrong = new bool[n];
            _plate = new bool[n];
            _fireCells = new int[n];
        }
        else
        {
            Array.Clear(_cellBits);
            Array.Clear(_ringBits);
            Array.Clear(_cellFrp);
            Array.Clear(_cellFlash);
            Array.Clear(_cellNight);
            Array.Clear(_cellFresh);
            Array.Clear(_cellStrong);
            Array.Clear(_plate);
        }
        _fireCount = 0;
        _topCount = 0;
        _count = 0;
        _base.Clear(Rgba.Black);

        // Без данных — ни центра, ни градусов: только сетка (радиус 1 — лишь для геометрии квадрата).
        _cLat = 0;
        _cLon = 0;
        _radKm = 1;
        if (_snap is { } snap)
        {
            _cLat = snap.CenterLat;
            _cLon = snap.CenterLon;
            _radKm = snap.RadiusKm > 0 ? snap.RadiusKm : DefaultRadiusKm;
        }

        Layout();
        DrawGrid();
        if (_snap is { } s)
        {
            Plot(s);
            DrawRings();
            DrawCenter(s);
        }

        _base.Text(2, 1, HeaderText, Accent);
        string tl = _snap is { } h
            ? h.FetchedAt.ToLocalTime().ToString("dd.MM HH:mm", Inv) + (h.FromCache ? " · КЭШ" : "")
            : "--.-- --:--";
        _base.Text(C - 3 - tl.Length, 1, tl, Warm);
        if (_snap is null) _base.Text(2, 2, NoDataText, Accent);
        else if (_count == 0) _base.Text(2, 2, NoFiresText, Warm);

        DrawPanel(_snap is not null);
        _scanCol = ScanColAt(_t);
    }

    /// <summary>
    /// Квадрат карты: side колонок по горизонтали и sideR строк по вертикали, где sideR · aspect ≈ side —
    /// тогда квадрат квадратный в пикселях при любой пропорции клетки.
    /// </summary>
    private void Layout()
    {
        int C = _cols, R = _rows;
        int pw = PanelWidth();
        int panelX = C - 2 - pw;
        int left = 9;
        int right = pw > 0 ? panelX - 4 : C - 4;
        int top = 4, bottom = R - 4;
        int availW = Math.Max(2, right - left + 1);
        int availH = Math.Max(2, bottom - top + 1);
        int side = Math.Min(availW, (int)Math.Floor(availH * _aspect));
        side -= side & 1;
        if (side < 2) side = 2;
        int sideR = Math.Clamp((int)Math.Round(side / _aspect), 1, Math.Max(1, availH));
        _side = side;
        _sideR = sideR;
        _mapX0 = left + (availW - side) / 2;
        _mapY0 = top + (availH - _sideR) / 2;
        _ccx = _mapX0 + side / 2.0;
        _ccy = _mapY0 + _sideR / 2.0;
        _kmC = (side / 2.0) / _radKm;      // клеток по горизонтали на км
        _kmR = (sideR / 2.0) / _radKm;     // строк на км
    }

    /// <summary>Сетка: с данными — через 2° с подписями градусов; без данных — пустая сетка по клеткам. Рамка.</summary>
    private void DrawGrid()
    {
        int xEnd = _mapX0 + _side - 1, yEnd = _mapY0 + _sideR - 1;

        if (_snap is null)
        {
            for (int r = _mapY0; r <= yEnd; r += 4)
                for (int c = _mapX0; c <= xEnd; c += 3) _base.Put(c, r, Dot, Faint);
            for (int c = _mapX0; c <= xEnd; c += 8)
                for (int r = _mapY0; r <= yEnd; r += 3) _base.Put(c, r, Dot, Faint);
        }
        else
        {
            double cosLat = Math.Cos(_cLat * Math.PI / 180);
            double kmLat = KmPerDeg * _kmR;                 // строк на градус широты
            double kmLon = KmPerDeg * cosLat * _kmC;        // клеток на градус долготы
            double spanLat = _radKm / KmPerDeg, spanLon = _radKm / (KmPerDeg * cosLat);

            for (int la = (int)Math.Ceiling((_cLat - spanLat) / GridStepDeg) * GridStepDeg; la <= _cLat + spanLat; la += GridStepDeg)
            {
                int row = (int)Math.Round(_ccy - (la - _cLat) * kmLat);
                if (row < _mapY0 || row > yEnd) continue;
                for (int c = _mapX0; c <= xEnd; c += 3) _base.Put(c, row, Dot, Faint);
                string t = Deg(la, "N", "S");
                _base.Text(_mapX0 - 2 - t.Length, row, t, Dim);
            }
            for (int lo = (int)Math.Ceiling((_cLon - spanLon) / GridStepDeg) * GridStepDeg; lo <= _cLon + spanLon; lo += GridStepDeg)
            {
                int col = (int)Math.Round(_ccx + (lo - _cLon) * kmLon);
                if (col < _mapX0 || col > xEnd) continue;
                for (int r = _mapY0; r <= yEnd; r += 3) _base.Put(col, r, Dot, Faint);
                string t = Deg(lo, "E", "W");
                _base.Text(col - t.Length / 2, yEnd + 2, t, Dim);
            }
        }

        _base.Put(_mapX0 - 1, _mapY0 - 1, '┌', Dim);
        _base.Put(xEnd + 1, _mapY0 - 1, '┐', Dim);
        _base.Put(_mapX0 - 1, yEnd + 1, '└', Dim);
        _base.Put(xEnd + 1, yEnd + 1, '┘', Dim);
    }

    /// <summary>Перекрестие и подпись центра (≈ — местоположение примерное).</summary>
    private void DrawCenter(FireSnapshot s)
    {
        int cc = (int)Math.Floor(_ccx), cr = (int)Math.Floor(_ccy);
        _base.Put(cc, cr, '┼', Warm);
        _base.Text(cc + 2, cr, "≈ " + GlyphSet.Sanitize(s.CenterName).ToUpperInvariant(), Accent);
    }

    /// <summary>
    /// Кольца дальности: пунктир секстантами, точка через ~2 клетки по периметру эллипса. Подписи — на тёмной плашке
    /// в одну клетку по краям, в позиции без очагов (очаги не перекрываются подписью).
    /// </summary>
    private void DrawRings()
    {
        int C = _cols, R = _rows;
        int xEnd = _mapX0 + _side - 1, yEnd = _mapY0 + _sideR - 1;
        Array.Clear(_ringBits);

        for (int i = 0; i < RingKm.Length; i++)
        {
            double rk = RingKm[i];
            if (rk > _radKm + 1e-9) continue;
            double rx = rk * _kmC, ry = rk * _kmR;
            // Периметр эллипса (приближение Рамануджана), в ширинах клетки: ry в строках × пропорция клетки.
            double a = rx, b = ry * _aspect;
            double perim = Math.PI * (3 * (a + b) - Math.Sqrt((3 * a + b) * (a + 3 * b)));
            int nPts = Math.Max(24, (int)(perim / 2));
            for (int p = 0; p < nPts; p++)
            {
                double th = 2 * Math.PI * p / nPts;
                double x = _ccx + rx * Math.Cos(th), y = _ccy - ry * Math.Sin(th);
                double sxf = Math.Floor(x * 2), syf = Math.Floor(y * 3);
                if (sxf < 0 || syf < 0) continue;
                int sx = (int)sxf, sy = (int)syf;
                int col = sx >> 1, row = sy / 3;
                if (col < _mapX0 || col > xEnd || row < _mapY0 || row > yEnd || col >= C || row >= R) continue;
                _ringBits[row * C + col] |= 1 << ((sy % 3) * 2 + (sx & 1));
            }
        }

        for (int idx = 0; idx < _ringBits.Length; idx++)
            if (_ringBits[idx] != 0) _base.Put(idx % C, idx / C, GlyphSet.Sextant(_ringBits[idx]), RingCol);

        for (int i = 0; i < RingKm.Length; i++)
        {
            double rk = RingKm[i];
            if (rk > _radKm + 1e-9) continue;
            string label = ((int)rk).ToString(Inv) + " КМ";
            double rx = rk * _kmC, ry = rk * _kmR;
            foreach (int angle in RingAngles)
            {
                double th = angle * Math.PI / 180;
                int col = (int)Math.Floor(_ccx + rx * Math.Cos(th));
                int row = (int)Math.Floor(_ccy - ry * Math.Sin(th));
                bool right = Math.Cos(th) >= 0;
                int x0 = right ? col + 2 : col - 1 - label.Length;
                int pl = x0 - 1, pr = x0 + label.Length;   // плашка: одна клетка пустоты с каждой стороны
                if (row < _mapY0 || row > yEnd || pl < _mapX0 || pr > xEnd || pr >= C) continue;
                bool free = true;
                for (int x = pl; x <= pr && free; x++) free = _cellBits[row * C + x] == 0;
                if (!free) continue;
                for (int x = pl; x <= pr; x++)
                {
                    _base.Put(x, row, ' ', RingCol, Rgba.Black);
                    _plate[row * C + x] = true;
                }
                _base.Text(x0, row, label, RingCol, Rgba.Black);
                break;
            }
        }
    }

    /// <summary>
    /// Очаги в квадрат: клетки, цвет по максимальному FRP в клетке, сильные, ореолы свежих; топ-5 и счётчики —
    /// по очагам (засечки ближе MergeKm склеиваются).
    /// </summary>
    private void Plot(FireSnapshot s)
    {
        int C = _cols;
        double cosLat = Math.Cos(_cLat * Math.PI / 180);
        DateTimeOffset freshFrom = s.FetchedAt.AddSeconds(-FreshSec);
        double max = 0;
        _dets.Clear();

        for (int i = 0; i < s.Points.Count; i++)
        {
            FirePoint p = s.Points[i];
            float frp = float.IsFinite(p.Frp) && p.Frp > 0 ? p.Frp : 0f;
            if (!CellOf(p, cosLat, out int col, out int row)) continue;

            if (frp > max) max = frp;
            _dets.Add((p, frp, col, row));

            int idx = row * C + col;
            if (_cellBits[idx] == 0) _fireCells[_fireCount++] = idx;
            _cellBits[idx] = 1;
            if (frp > StrongFrp) _cellStrong[idx] = true;
            if (frp >= _cellFrp[idx])
            {
                _cellFrp[idx] = frp;
                _cellFg[idx] = Brighten(Ramp(RampT(frp)));
                _cellNight[idx] = !p.Day;
            }
            if (p.AcquiredAt >= freshFrom) _cellFresh[idx] = true;
        }

        // Склейка засечек: по убыванию FRP, жадно; сид — сильнейшая засечка очага.
        _dets.Sort((a, b) => b.Frp.CompareTo(a.Frp));
        _clusters.Clear();
        foreach (var d in _dets)
        {
            int hit = -1;
            for (int k = 0; k < _clusters.Count && hit < 0; k++)
            {
                FirePoint sd = _clusters[k].Seed;
                double dxKm = (d.P.Lon - sd.Lon) * KmPerDeg * cosLat;
                double dyKm = (d.P.Lat - sd.Lat) * KmPerDeg;
                if (dxKm * dxKm + dyKm * dyKm <= MergeKm * MergeKm) hit = k;
            }
            if (hit >= 0) _clusters[hit] = _clusters[hit] with { N = _clusters[hit].N + 1 };
            else _clusters.Add(new Cluster(d.P, d.Frp, d.Col, d.Row, 1));
        }

        int day = 0, night = 0;
        double sum = 0;
        foreach (var c in _clusters)
        {
            if (c.Seed.Day) day++; else night++;
            sum += c.Frp;
        }
        _count = _clusters.Count;

        _topCount = Math.Min(MaxTop, _clusters.Count);   // кластеры уже по убыванию FRP
        for (int k = 0; k < _topCount; k++)
        {
            Cluster c = _clusters[k];
            _topPts[k] = c.Seed;
            _topFrp[k] = c.Frp;
            _topCol[k] = c.Col;
            _topRow[k] = c.Row;
            _topN[k] = c.N;
        }

        // Ореол свежих очагов: фон клетки и свободных соседей.
        for (int k = 0; k < _fireCount; k++)
        {
            int idx = _fireCells[k];
            if (!_cellFresh[idx]) continue;
            int col = idx % C, row = idx / C;
            Glow(col, row, false);
            Glow(col - 1, row, true);
            Glow(col + 1, row, true);
            Glow(col, row - 1, true);
            Glow(col, row + 1, true);
        }

        _lastDay = day;
        _lastNight = night;
        _lastSum = sum;
        _lastMax = max;
    }

    /// <summary>Правая колонка: счётчики по очагам, топ-5 с координатами (× N — засечки), азимутом и дальностью, шкала FRP.</summary>
    private void DrawPanel(bool has)
    {
        int pw = PanelWidth();
        if (pw == 0) return;
        int x0 = _cols - 2 - pw, xr = x0 + pw - 1;
        double cosLat = Math.Cos(_cLat * Math.PI / 180);

        _base.Text(x0, 3, "24 Ч · ОЧАГИ", Accent);
        Row(x0, xr, 4, "ВСЕГО", has ? _count.ToString(Inv) : "--", Dim, Warm);
        Row(x0, xr, 5, "FRP СУММА", has ? Num(_lastSum) + " МВТ" : "--", Dim, Warm);
        Row(x0, xr, 6, "FRP МАКС", has ? Num(_lastMax) + " МВТ" : "--", Dim, Warm);
        Row(x0, xr, 7, "ДЕНЬ / НОЧЬ", has ? _lastDay.ToString(Inv) + " / " + _lastNight.ToString(Inv) : "--", Dim, Warm);

        _base.Text(x0, 9, "ТОП-5 ПО FRP", Accent);
        if (_topCount == 0) _base.Text(x0, 10, "--", Dim);
        for (int k = 0; k < _topCount; k++)
        {
            FirePoint p = _topPts[k];
            int y = 10 + 2 * k;
            string line = (k + 1).ToString("D2", Inv) + "  " + Coord(p.Lat, "N", "S") + " " + Coord(p.Lon, "E", "W");
            if (_topN[k] > 1)
            {
                string withN = line + " ×" + _topN[k].ToString(Inv);
                if (x0 + withN.Length - 1 <= xr) line = withN;
            }
            _base.Text(x0, y, line, Warm);
            double dxKm = (p.Lon - _cLon) * KmPerDeg * cosLat;
            double dyKm = (p.Lat - _cLat) * KmPerDeg;
            double az = Math.Atan2(dxKm, dyKm) * 180 / Math.PI;
            if (az < 0) az += 360;
            double dist = Math.Sqrt(dxKm * dxKm + dyKm * dyKm);
            string az3 = ((int)Math.Round(az) % 360).ToString("D3", Inv);
            _base.Text(x0, y + 1, "   " + Num(_topFrp[k]) + " МВТ · " + az3 + "° · " + Num(dist) + " КМ", Dim);
        }

        // Шкала FRP: тот же градиент, что у очагов.
        int by = 21, bx = x0 + 4;
        _base.Text(x0, by, "FRP", Dim);
        for (int i = 0; i < 12; i++) _base.Put(bx + i, by, '█', Ramp(i / 11f));
        _base.Text(bx, by + 1, "0", Dim);
        _base.Text(bx + 8, by + 1, "300+", Dim);
    }

    // ---------- Вспомогательные ----------

    private int PanelWidth() => _cols >= PanelMinCols ? Math.Clamp(_cols / 3, PanelMinW, PanelMaxW) : 0;

    /// <summary>Клетка очага. false — вне квадрата или вне сетки.</summary>
    private bool CellOf(FirePoint p, double cosLat, out int col, out int row)
    {
        col = row = 0;
        double dxKm = (p.Lon - _cLon) * KmPerDeg * cosLat;
        double dyKm = (p.Lat - _cLat) * KmPerDeg;
        if (!(Math.Abs(dxKm) <= _radKm && Math.Abs(dyKm) <= _radKm)) return false;
        int sx = (int)Math.Floor((_ccx + dxKm * _kmC) * 2);
        int sy = (int)Math.Floor((_ccy - dyKm * _kmR) * 3);
        if (sx < 0 || sy < 0) return false;
        col = sx >> 1;
        row = sy / 3;
        return col < _cols && row < _rows;
    }

    private void Glow(int x, int y, bool onlyFree)
    {
        if (!_base.InBounds(x, y)) return;
        if (onlyFree && _cellBits[y * _cols + x] != 0) return;
        _base[x, y].Bg = Halo;
    }

    private void Row(int x0, int xr, int y, string label, string value, Rgba labelFg, Rgba valueFg)
    {
        _base.Text(x0, y, label, labelFg);
        _base.Text(xr - value.Length + 1, y, value, valueFg);
    }

    /// <summary>Номер колонки сканера для момента t: проход слева направо за ScanSec.</summary>
    private int ScanColAt(double t)
    {
        double ph = (t % _scanSec) / _scanSec;
        return _mapX0 + (int)Math.Floor(ph * _side);
    }

    /// <summary>Колонка col пересечена лучом при переходе prev → cur (с перескоком через край).</summary>
    private static bool Crossed(int col, int prev, int cur)
    {
        if (cur > prev) return col > prev && col <= cur;
        return col > prev || col <= cur;
    }

    /// <summary>Градиент FRP: тускло-красный → оранжевый → жёлто-белый. t = 0..1.</summary>
    private static Rgba Ramp(float t) =>
        t < 0.5f ? Rgba.Lerp(FireLow, FireMid, t * 2f) : Rgba.Lerp(FireMid, FireHi, (t - 0.5f) * 2f);

    /// <summary>Ядро очага светлее сетки в 3+ раза: максимум канала к 255, затем подмешать белого (тёмно-красный остаётся красным).</summary>
    private static Rgba Brighten(Rgba c)
    {
        int m = Math.Max(c.R, Math.Max(c.G, c.B));
        if (m == 0) return c;
        return Rgba.Lerp(c.Scale(255f / m), Rgba.White, 0.35f);
    }

    /// <summary>FRP → 0..1 логарифмически: 316 МВт ≈ 1.</summary>
    private static float RampT(float frp) => (float)Math.Clamp(Math.Log10(1.0 + frp) / 2.5, 0.0, 1.0);

    private static string Deg(int v, string pos, string neg) =>
        Math.Abs(v).ToString(Inv) + "°" + (v >= 0 ? pos : neg);

    private static string Coord(double v, string pos, string neg) =>
        Math.Abs(v).ToString("0.0", Inv) + "°" + (v >= 0 ? pos : neg);

    private static string Num(double v) => Math.Round(v).ToString("0", Inv);
}
