using System.Globalization;
using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// «Радар»: обзорный PPI по центру экрана. Луч — тонкая линия из секстантов (подсетка 2×3), за ним — послесвечение
/// в фоне клеток: Bg = base·exp(−отставание/τ). Край окружности — тоже секстантами. Отметки самолётов зажигаются,
/// когда по ним проходит луч, и гаснут до следующего прохода. Между снимками ADS-B (раз в 60 с) позиции экстраполируются
/// по скорости и курсу. Данные — снимок "flights"; без данных радар всё равно крутится.
/// Пропорция ячейки — из options.CellAspect (высота / ширина, шрифт): круг выглядит кругом.
/// Формуляр ставится в один из четырёх углов от отметки (СВ, ЮВ, ЮЗ, СЗ), первый не пересекающийся.
/// </summary>
public sealed class RadarScene : IScene, IDataScene, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec SweepSpec = new("sweepSec", SceneParamKind.Float, 6f, 2f, 20f, "период оборота луча, с");
    public static readonly SceneParamSpec LabelsSpec = new("labels", SceneParamKind.Bool, 1f, 0f, 1f, "формуляры у отметок");
    public static readonly SceneParamSpec RangeSpec = new("rangeNm", SceneParamKind.Choice, 0f, 0f, 3f,
        "дальность, мили (auto — по данным)", new[] { "auto", "50", "100", "150" });
    private static readonly SceneParamSpec[] ParamList = { SweepSpec, LabelsSpec, RangeSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    public string Id => "radar";

    private static readonly string[] Keys = { "flights" };
    public IReadOnlyList<string> DataKeys => Keys;

    private const int TopRow = 3;             // радар начинается ниже HUD (строки 0..2)
    private const int TableTopFromBottom = 8; // заголовок таблички: rows - 8, шесть строк под ним
    private const int TableW = 30;            // ширина строки таблички
    private const int TableMax = 6;           // ближайших бортов в табличке
    private const int TailLen = 3;            // точек хвоста у отметки
    private const float TailStepS = 8f;       // шаг хвоста, с
    private const float RingStepNm = 25f;
    private const float DefaultRangeNm = 100f;
    private const float TrailPeak = 0.30f;    // доля цвета следа у луча
    private const float TrailTau = 25f;       // τ послесвечения, градусы
    private const float MarkMin = 0.1f;       // ниже — отметка погасла до следующего прохода
    private const float InTol = 0.05f;        // допуск «внутри окружности», строки
    private const float EdgeTol = 0.17f;      // полоса края окружности, строки
    private const string NoData = "NO ADS-B DATA";
    private const string NoTargets = "ЦЕЛЕЙ НЕ ОБНАРУЖЕНО";
    private const string Remote = "НИЗКОЕ ПОКРЫТИЕ · ДАЛЬНЯЯ СТАНЦИЯ · ";

    private static readonly Rgba BgBase = Rgba.Hex(0x03100A);
    private static readonly Rgba EdgeCol = Rgba.Hex(0x2A9E5C);
    private static readonly Rgba RingCol = Rgba.Hex(0x1F7A43);
    private static readonly Rgba TickCol = Rgba.Hex(0x3DBF72);
    private static readonly Rgba LabelDim = Rgba.Hex(0x2E9A5A);
    private static readonly Rgba BeamCol = Rgba.Hex(0xD0FFDC);
    private static readonly Rgba TrailCol = Rgba.Hex(0x2FD45F);
    private static readonly Rgba MarkCol = Rgba.Hex(0xEFFFF4);
    private static readonly Rgba TailCol = Rgba.Hex(0x7BEA9E);
    private static readonly Rgba TextCol = Rgba.Hex(0x9CF5B8);
    private static readonly Rgba HudCol = Rgba.Hex(0x9CF5B8);
    private static readonly Rgba Warn = Rgba.Hex(0xFFB000);

    private static readonly string TableHeader = string.Format(CultureInfo.InvariantCulture,
        "{0,-8} {1,-4} {2,-5} {3,-4} {4,5}", "ПОЗЫВН", "ТИП", "FL", "КУРС", "НМ");

    // Центры подъячеек секстанта: бит k = строка k/2 (0..2 сверху вниз), столбец k%2 (0 — левый).
    private static readonly float[] SubDc = { -0.25f, 0.25f, -0.25f, 0.25f, -0.25f, 0.25f };
    private static readonly float[] SubDr = { -1f / 3, -1f / 3, 0f, 0f, 1f / 3, 1f / 3 };

    /// <summary>Самолёт, пересчитанный под снимок: позиция в милях от центра на момент снимка и скорость в nm/с.</summary>
    private struct Target
    {
        public double X0, Y0;      // восток, север (nm)
        public double Vx, Vy;      // скорость (nm/с)
        public bool Air;           // в воздухе; на земле отметки нет
        public int Crs;            // курс, градусы
        public string Call, Typ, Fl, Spd;
    }

    private float _aspect = 1.8f;  // столбцов на строку = высота / ширина ячейки
    private float _sweep;
    private bool _labels;
    private float _rangeParam;     // 0 — auto
    private bool _autoRange;
    private float _range;          // дальность шкалы, nm
    private int _cols, _rows;
    private int _cx, _cy, _rad;    // центр и радиус шкалы в строках (по горизонтали — A·rad столбцов)
    private int _tableTop;
    private double _t;             // время сцены, с
    private double _snapT;         // время сцены при приёме последнего снимка

    private bool _hasData;
    private bool _remote;
    private string _header = "ADS-B";
    private string _remoteText = "";
    private Target[] _targets = Array.Empty<Target>();
    private int _nTargets;

    private int[] _tIdx = Array.Empty<int>();
    private double[] _tDist = Array.Empty<double>();
    private string[] _tableRows = Array.Empty<string>();
    private int _nTable;

    // Клетки диска: индекс в сетке, азимут центра, радиус центра (строки), маска подъячеек внутри окружности и края,
    // азимут и порог ширины луча для каждой подъячейки (6 на клетку).
    private int[] _diskIdx = Array.Empty<int>();
    private double[] _diskAng = Array.Empty<double>();
    private float[] _cellR = Array.Empty<float>();
    private byte[] _cellIn = Array.Empty<byte>();
    private byte[] _cellEdge = Array.Empty<byte>();
    private float[] _subAng = Array.Empty<float>();
    private float[] _subThr = Array.Empty<float>();
    private int _nDisk;

    // Видимые отметки за кадр и занятые прямоугольники (отметки и формуляры) — без выделения памяти в Draw
    private int[] _vIdx = Array.Empty<int>();
    private int[] _vCol = Array.Empty<int>();
    private int[] _vRow = Array.Empty<int>();
    private float[] _vM = Array.Empty<float>();
    private int _nVis;
    private int[] _occ = Array.Empty<int>();
    private int _nOcc;

    private CharGrid _bg = new(1, 1);   // статичный слой: диск, край, кольца, риски, подписи, перекрестие

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = cols;
        _rows = rows;
        _aspect = Math.Clamp(options.CellAspect, 0.5f, 4f);
        _sweep = Math.Clamp(options.GetFloat(SweepSpec.Name, SweepSpec.Default), SweepSpec.Min, SweepSpec.Max);
        _labels = options.GetBool(LabelsSpec.Name, LabelsSpec.Default >= 0.5f);
        string rs = options.GetString(RangeSpec.Name, RangeSpec.Options![(int)RangeSpec.Default]);
        _rangeParam = rs == "50" ? 50f : rs == "100" ? 100f : rs == "150" ? 150f : 0f;
        _autoRange = _rangeParam <= 0f;
        _range = _autoRange ? DefaultRangeNm : _rangeParam;

        _t = 0;
        _snapT = 0;
        _hasData = false;
        _remote = false;
        _header = "ADS-B";
        _remoteText = "";
        _targets = Array.Empty<Target>();
        _nTargets = 0;
        _tIdx = new int[TableMax];
        _tDist = new double[TableMax];
        _tableRows = new string[TableMax];
        _nTable = 0;
        AllocTargetBuffers(0);

        Layout();
        BuildDisk();
        _bg = new CharGrid(cols, rows);
        BuildStatic();
    }

    public void OnData(string key, DataSnapshot snapshot)
    {
        if (key != "flights" || snapshot is not FlightSnapshot fs) return;

        _hasData = true;
        _snapT = _t;
        _remote = fs.Remote;
        string station = Clean(fs.StationName, 24);
        _remoteText = Remote + station;
        _header = station.Length > 0
            ? $"ADS-B · {station} · {fs.Aircraft.Count.ToString(CultureInfo.InvariantCulture)} AC"
            : $"ADS-B · {fs.Aircraft.Count.ToString(CultureInfo.InvariantCulture)} AC";

        // Дальность: auto — радиус из данных
        float newRange = _autoRange ? (fs.RadiusNm > 0 ? (float)fs.RadiusNm : DefaultRangeNm) : _rangeParam;
        bool rangeChanged = newRange != _range;
        _range = newRange;

        int n = fs.Aircraft.Count;
        if (_targets.Length < n)
        {
            _targets = new Target[n];
            AllocTargetBuffers(n);
        }
        _nTargets = n;
        double kx = 60.0 * Math.Cos(fs.CenterLat * Math.PI / 180.0);
        for (int i = 0; i < n; i++)
        {
            Aircraft a = fs.Aircraft[i];
            ref Target t = ref _targets[i];
            t.X0 = (a.Lon - fs.CenterLon) * kx;
            t.Y0 = (a.Lat - fs.CenterLat) * 60.0;
            double rad = a.TrackDeg * Math.PI / 180.0;
            double v = a.SpeedKt / 3600.0;            // nm/с
            t.Vx = Math.Sin(rad) * v;
            t.Vy = Math.Cos(rad) * v;
            t.Air = a.AltFt >= 0;
            t.Crs = ((int)Math.Round(a.TrackDeg) % 360 + 360) % 360;
            string call = Clean(a.Callsign, 8);
            t.Call = call.Length > 0 ? call : Clean(a.Hex, 8);
            string typ = Clean(a.Type, 4);
            t.Typ = typ.Length > 0 ? typ : "----";
            t.Fl = "FL" + Math.Round(a.AltFt / 100.0).ToString("000", CultureInfo.InvariantCulture);
            t.Spd = Math.Round(a.SpeedKt).ToString("0", CultureInfo.InvariantCulture) + "KT";
        }

        // Табличка: ближайшие шесть бортов в воздухе (сортировка вставкой, без выделения памяти)
        int nt = 0;
        for (int i = 0; i < n; i++)
        {
            ref Target t = ref _targets[i];
            if (!t.Air) continue;
            double d = Math.Sqrt(t.X0 * t.X0 + t.Y0 * t.Y0);
            if (nt == TableMax && d >= _tDist[TableMax - 1]) continue;
            int p = Math.Min(nt, TableMax - 1);
            while (p > 0 && _tDist[p - 1] > d)
            {
                _tDist[p] = _tDist[p - 1];
                _tIdx[p] = _tIdx[p - 1];
                p--;
            }
            _tDist[p] = d;
            _tIdx[p] = i;
            if (nt < TableMax) nt++;
        }
        _nTable = nt;
        for (int k = 0; k < nt; k++)
        {
            ref Target t = ref _targets[_tIdx[k]];
            _tableRows[k] = string.Format(CultureInfo.InvariantCulture, "{0,-8} {1,-4} {2,-5} {3,3}° {4,5:0.0}",
                t.Call, t.Typ, t.Fl, t.Crs, _tDist[k]);
        }

        if (rangeChanged) BuildStatic();
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;
        _t += dt;
    }

    public void Draw(CharGrid grid)
    {
        if (grid.Cols != _cols || grid.Rows != _rows)
        {
            grid.Clear(Rgba.Black);
            return;
        }

        Array.Copy(_bg.Cells, grid.Cells, grid.Cells.Length);

        double theta = (_t % _sweep) / _sweep * 360.0;
        DrawSweep(grid.Cells, theta);
        if (_hasData) DrawTargets(grid, theta);
        DrawHud(grid);
    }

    // ---------- Луч и послесвечение ----------

    /// <summary>Послесвечение — в фоне клеток; луч — тонкая линия секстантами (подъячейки, чей азимут только что прошёл лучом).</summary>
    private void DrawSweep(Cell[] cells, double theta)
    {
        for (int i = 0; i < _nDisk; i++)
        {
            int idx = _diskIdx[i];
            float tr = TrailPeak * (float)Math.Exp(-Lag(theta, _diskAng[i]) / TrailTau);
            if (tr > 0.01f) cells[idx].Bg = Rgba.Lerp(BgBase, TrailCol, tr);

            if (_cellR[i] < 1.0) continue;   // центр шкалы — перекрестие
            int mask = _cellIn[i], bits = 0;
            for (int k = 0; k < 6; k++)
            {
                if (((mask >> k) & 1) == 0) continue;
                int j = i * 6 + k;
                if (Lag(theta, _subAng[j]) < _subThr[j]) bits |= 1 << k;
            }
            if (bits != 0)
            {
                cells[idx].Ch = GlyphSet.Sextant(bits);
                cells[idx].Fg = BeamCol;
            }
        }
    }

    /// <summary>Сколько градусов назад от луча (0..360) находится азимут p.</summary>
    private static double Lag(double theta, double p)
    {
        double d = theta - p;
        return d - 360.0 * Math.Floor(d / 360.0);
    }

    // ---------- Отметки и формуляры ----------

    private void DrawTargets(CharGrid g, double theta)
    {
        double age = _t - _snapT;
        double k = _rad / (double)_range;             // строк на nm
        double limit = (_rad + 0.5) * (_rad + 0.5);
        _nVis = 0;
        _nOcc = 0;

        // 1. Отметки и хвосты
        for (int i = 0; i < _nTargets; i++)
        {
            Target a = _targets[i];
            if (!a.Air) continue;
            double x = a.X0 + a.Vx * age, y = a.Y0 + a.Vy * age;   // nm, экстраполяция по курсу и скорости
            double xr = x * k, yr = y * k;                          // строки
            if (xr * xr + yr * yr > limit) continue;                // за пределами шкалы
            float m = Decay(theta, Bearing(x, y));
            if (m < MarkMin) continue;

            // Хвост: предыдущие позиции (назад по вектору скорости), старые — тусклее
            for (int j = TailLen; j >= 1; j--)
            {
                double tx = (x - a.Vx * TailStepS * j) * k, ty = (y - a.Vy * TailStepS * j) * k;
                g.Put(CellX(tx), CellY(ty), '·', Rgba.Lerp(Rgba.Black, TailCol, m * (0.7f - 0.2f * (j - 1))));
            }

            int col = CellX(xr), row = CellY(yr);
            g.Put(col, row, '•', Rgba.Lerp(Rgba.Black, MarkCol, m));
            _vIdx[_nVis] = i;
            _vCol[_nVis] = col;
            _vRow[_nVis] = row;
            _vM[_nVis] = m;
            _nVis++;
            AddOcc(col, row, col, row);
        }

        if (!_labels) return;

        // 2. Формуляры: полный (3 строки), иначе только позывной, иначе без формуляра
        for (int v = 0; v < _nVis; v++)
        {
            Target a = _targets[_vIdx[v]];
            int col = _vCol[v], row = _vRow[v];
            Rgba c = Rgba.Lerp(Rgba.Black, TextCol, _vM[v]);
            int w = Math.Max(a.Call.Length, Math.Max(a.Fl.Length, a.Spd.Length));
            if (TryPlace(g, col, row, w, 3, a.Call, a.Fl, a.Spd, c)) continue;
            TryPlace(g, col, row, a.Call.Length, 1, a.Call, null, null, c);
        }
    }

    /// <summary>
    /// Углы от отметки: 0 — СВ, 1 — ЮВ, 2 — ЮЗ, 3 — СЗ. Ставит блок w×h в первый угол, который влезает в экран
    /// и не пересекает уже занятые прямоугольники (отметки и формуляры). Возвращает false, если ни один не подошёл.
    /// </summary>
    private bool TryPlace(CharGrid g, int col, int row, int w, int h, string l0, string? l1, string? l2, Rgba c)
    {
        for (int q = 0; q < 4; q++)
        {
            bool east = q == 0 || q == 1;
            bool north = q == 0 || q == 3;
            int x0 = east ? col + 2 : col - 2 - w;
            int y0 = north ? row - h : row + 1;
            int x1 = x0 + w - 1, y1 = y0 + h - 1;
            if (x0 < 0 || y0 < 0 || x1 > _cols - 1 || y1 > _rows - 1) continue;
            if (Overlaps(x0, y0, x1, y1)) continue;
            AddOcc(x0, y0, x1, y1);
            g.Text(x0, y0, l0, c);
            if (l1 is not null) g.Text(x0, y0 + 1, l1, c);
            if (l2 is not null) g.Text(x0, y0 + 2, l2, c);
            return true;
        }
        return false;
    }

    private bool Overlaps(int x0, int y0, int x1, int y1)
    {
        for (int r = 0; r < _nOcc; r++)
        {
            int o = r * 4;
            if (x1 < _occ[o] || x0 > _occ[o + 2] || y1 < _occ[o + 1] || y0 > _occ[o + 3]) continue;
            return true;
        }
        return false;
    }

    private void AddOcc(int x0, int y0, int x1, int y1)
    {
        int o = _nOcc * 4;
        _occ[o] = x0;
        _occ[o + 1] = y0;
        _occ[o + 2] = x1;
        _occ[o + 3] = y1;
        _nOcc++;
    }

    private void AllocTargetBuffers(int cap)
    {
        _vIdx = new int[cap];
        _vCol = new int[cap];
        _vRow = new int[cap];
        _vM = new float[cap];
        _occ = new int[cap * 8 + 8];    // на самолёт: отметка + формуляр, по 4 числа
        _nVis = 0;
        _nOcc = 0;
    }

    // ---------- HUD ----------

    private void DrawHud(CharGrid g)
    {
        g.Text(2, 1, _header, HudCol, Rgba.Black);
        if (_hasData && _remote && ((int)(_t * 2.0) & 1) == 0)
            g.Text(Math.Max(0, (_cols - _remoteText.Length) / 2), 2, _remoteText, Warn, Rgba.Black);

        int tx = Math.Max(0, (_cols - TableW) / 2);
        if (!_hasData)
        {
            g.Text(Math.Max(0, (_cols - NoData.Length) / 2), _tableTop + 1, NoData, HudCol, Rgba.Black);
            return;
        }
        if (_nTable == 0)
        {
            g.Text(Math.Max(0, (_cols - NoTargets.Length) / 2), _tableTop + 1, NoTargets, HudCol, Rgba.Black);
            return;
        }
        g.Text(tx, _tableTop, TableHeader, LabelDim, Rgba.Black);
        for (int k = 0; k < _nTable; k++)
            g.Text(tx, _tableTop + 1 + k, _tableRows[k], TextCol, Rgba.Black);
    }

    // ---------- Геометрия ----------

    private void Layout()
    {
        _cx = _cols / 2;
        _tableTop = _rows - TableTopFromBottom;
        int bottom = _tableTop - 2;
        _cy = (TopRow + bottom) / 2;
        // Радиус ограничен и высотой (строки), и шириной (A·rad столбцов влево и вправо, с запасом под подписи)
        _rad = Math.Max(1, Math.Min(_cy - TopRow - 2, (int)((_cols / 2 - 4) / _aspect)));
    }

    private int CellX(double xr) => _cx + (int)Math.Round(_aspect * xr);
    private int CellY(double yr) => _cy - (int)Math.Round(yr);

    /// <summary>Азимут точки (восток e, север n) в градусах, по часовой от севера.</summary>
    private static double Bearing(double e, double n)
    {
        double b = Math.Atan2(e, n) * 180.0 / Math.PI;
        return b < 0 ? b + 360.0 : b;
    }

    /// <summary>Яркость отметки по времени после прохода луча: 1 в момент прохода, ~0.13 через пол-оборота, ~0 перед следующим.</summary>
    private float Decay(double theta, double brg)
    {
        double diff = Lag(theta, brg);
        float u = (float)(1.0 - diff / 360.0);
        return u * u * u;
    }

    /// <summary>
    /// Анализ клетки: маска подъячеек внутри окружности (inM), маска подъячеек на краю (edM), радиус центра клетки.
    /// Клетка входит в диск, если есть хотя бы одна подъячейка внутри или на краю.
    /// </summary>
    private void Analyze(int x, int y, out byte inM, out byte edM, out float cr)
    {
        double dcC = x - _cx, drC = y - _cy;
        cr = (float)Math.Sqrt((dcC / _aspect) * (dcC / _aspect) + drC * drC);
        inM = 0;
        edM = 0;
        for (int k = 0; k < 6; k++)
        {
            double e = (dcC + SubDc[k]) / _aspect, nn = -(drC + SubDr[k]);
            double r = Math.Sqrt(e * e + nn * nn);
            if (r <= _rad + InTol) inM |= (byte)(1 << k);
            if (Math.Abs(r - _rad) <= EdgeTol) edM |= (byte)(1 << k);
        }
    }

    /// <summary>Клетки диска, азимуты и пороги луча по подъячейкам; выделяется один раз в Init.</summary>
    private void BuildDisk()
    {
        int n = 0;
        for (int y = 0; y < _rows; y++)
            for (int x = 0; x < _cols; x++)
            {
                Analyze(x, y, out byte im, out byte em, out _);
                if ((im | em) != 0) n++;
            }

        _diskIdx = new int[n];
        _diskAng = new double[n];
        _cellR = new float[n];
        _cellIn = new byte[n];
        _cellEdge = new byte[n];
        _subAng = new float[n * 6];
        _subThr = new float[n * 6];
        _nDisk = 0;
        for (int y = 0; y < _rows; y++)
            for (int x = 0; x < _cols; x++)
            {
                Analyze(x, y, out byte im, out byte em, out float cr);
                if ((im | em) == 0) continue;
                int i = _nDisk++;
                _diskIdx[i] = y * _cols + x;
                _diskAng[i] = Bearing((x - _cx) / _aspect, -(y - _cy));
                _cellR[i] = cr;
                _cellIn[i] = im;
                _cellEdge[i] = em;
                for (int k = 0; k < 6; k++)
                {
                    double e = (x - _cx + SubDc[k]) / _aspect, nn = -(y - _cy + SubDr[k]);
                    double r = Math.Sqrt(e * e + nn * nn);
                    int j = i * 6 + k;
                    _subAng[j] = (float)Bearing(e, nn);
                    // Порог луча — около полподъячейки на этом радиусе, но не меньше градуса (иначе луч рвётся между кадрами)
                    _subThr[j] = (float)Math.Max(1.0, 57.3 * 0.35 / Math.Max(r, 0.5));
                }
            }
    }

    private void PutPolar(double a, double r, char ch, Rgba fg)
    {
        _bg.Put(_cx + (int)Math.Round(_aspect * Math.Sin(a) * r), _cy - (int)Math.Round(Math.Cos(a) * r), ch, fg);
    }

    /// <summary>
    /// Статичный слой: фон диска, край окружности секстантами, кольца 25 nm с подписями,
    /// риски азимута каждые 10° (подписи каждые 30°), перекрестие.
    /// </summary>
    private void BuildStatic()
    {
        var g = _bg;
        g.Clear(Rgba.Black);
        for (int i = 0; i < _nDisk; i++) g.Cells[_diskIdx[i]].Bg = BgBase;

        for (int i = 0; i < _nDisk; i++)
        {
            if (_cellEdge[i] == 0) continue;
            int idx = _diskIdx[i];
            g.Put(idx % _cols, idx / _cols, GlyphSet.Sextant(_cellEdge[i]), EdgeCol);
        }

        for (double ring = RingStepNm; ring <= _range + 0.01; ring += RingStepNm)
        {
            double rr = ring / _range * _rad;
            int n = Math.Max(90, (int)(rr * 8));
            for (int k = 0; k < n; k++) PutPolar(2 * Math.PI * k / n, rr, '·', RingCol);
            g.Text(_cx + 1, _cy - (int)Math.Round(rr), ((int)ring).ToString(CultureInfo.InvariantCulture), LabelDim);
        }

        for (int deg = 0; deg < 360; deg += 10)
            PutPolar(deg * Math.PI / 180.0, _rad + 1, deg % 30 == 0 ? '+' : '·', TickCol);

        for (int deg = 0; deg < 360; deg += 30)
        {
            string s = deg.ToString("000", CultureInfo.InvariantCulture);
            double a = deg * Math.PI / 180.0;
            int col = CellX(Math.Sin(a) * (_rad + 2)) - s.Length / 2;
            int row = CellY(Math.Cos(a) * (_rad + 2));
            g.Text(col, row, s, LabelDim);
        }

        for (int c = -2 * _rad; c <= 2 * _rad; c++) g.Put(_cx + c, _cy, '─', RingCol);
        for (int r = -_rad; r <= _rad; r++)
            if (r != 0) g.Put(_cx, _cy + r, '│', RingCol);
        g.Put(_cx, _cy, '┼', TickCol);
    }

    private static string Clean(string? s, int max)
    {
        string t = GlyphSet.Sanitize(s ?? "").Trim().ToUpperInvariant();
        return t.Length > max ? t.Substring(0, max) : t;
    }
}
