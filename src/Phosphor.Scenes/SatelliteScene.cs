using System.Globalization;
using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// «Спутник»: кадр ленты на RetroCanvas (HalfBlock, точный цвет), HUD символами поверх (после Present).
/// Equirect — растянут с сохранением пропорций, таймлапс с перекрёстным смешением, трекинг облаков.
/// FullDisk — диск по центру, сетка широт/долгот на видимой полусфере. Факсовое «приём» сверху вниз при старте
/// и при новом кадре. Ночью (тёмный кадр или 21–6 по часам) — янтарный ИК. Данные — снимки хоста ("satellite",
/// "clock"); без данных — процедурный шар с «НЕТ СИГНАЛА».
/// </summary>
public sealed class SatelliteScene : IScene, IDataScene, IAudioReactive
{
    public string Id => "satellite";

    private static readonly string[] Keys = { "satellite", "clock" };
    public IReadOnlyList<string> DataKeys => Keys;

    private const float FrameStep = 0.6f;     // с на кадр таймлапса
    private const float HoldSec = 2f;         // пауза на последнем кадре
    private const float RecSec = 3f;          // длительность приёма
    private const int MinTimelapse = 3;       // кадров меньше — статично
    private const float DimLuma = 40f / 255f; // средняя яркость кадра ниже — ночь
    private const string NoSignal = "НЕТ СИГНАЛА · ОЖИДАНИЕ ПРИЁМА";
    private const string RecPrefix = "► ПРИЁМ ";

    private static readonly Rgba DayAccent = Rgba.Hex(0xFF6600);
    private static readonly Rgba DayWarm = Rgba.Hex(0xF2EEE6);
    private static readonly Rgba DayDim = Rgba.Hex(0x5E5A52);
    private static readonly Rgba DayGrid = Rgba.Hex(0xBFC5CC);
    private static readonly Rgba NightAccent = Rgba.Hex(0xFFB347);
    private static readonly Rgba NightWarm = Rgba.Hex(0xFFD58A);
    private static readonly Rgba NightDim = Rgba.Hex(0x8A5A1E);
    private static readonly Rgba AmberMid = Rgba.Hex(0x7A4200);
    private static readonly Rgba AmberHi = Rgba.Hex(0xFFD080);
    private static readonly Rgba White = Rgba.Hex(0xFFFFFF);

    private static readonly string[] LatTxt = BuildLat();
    private static readonly string[] LonTxt = BuildLon();

    private Random _rng = new(0);
    private int _cols, _rows;
    private RetroCanvas _canvas = new(1, 1, CanvasMode.HalfBlock);

    // Данные
    private SatelliteSetSnapshot? _set;
    private SatelliteFeed? _feed;
    private SatelliteFrame[] _frames = Array.Empty<SatelliteFrame>();
    private string _label = "";
    private string _markLabel = "";
    private bool _hasMark;
    private bool _dimFrame;
    private bool _hasClock;
    private int _clockHour;
    private bool _night;
    private readonly SatClouds _clouds = new();

    // Время и состояние кадра
    private double _t;               // время сцены, с
    private float _flash;            // вспышка на бит: 1 → 0
    private float _blink;            // фаза мигания курсора
    private float _recT = RecSec;    // с начала приёма; ≥ RecSec — приём окончен
    private bool _static;            // кадров меньше MinTimelapse: один кадр + Ken Burns
    private int _curA = -1, _curB = -1;
    private float _curF;             // доля смешения A → B
    private float _zoom = 1f;

    // Геометрия последнего Render (для HUD и маркеров; Draw только читает)
    private double _picS, _picX0, _picY0, _picFw, _picFh;
    private double _diskCx, _diskCy, _diskR;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = cols;
        _rows = rows;
        _rng = rng;
        _canvas = new RetroCanvas(cols, rows, CanvasMode.HalfBlock);
        _t = 0;
        _flash = 0;
        _blink = 0;
        _recT = RecSec;
        PickFeed(); // следующая лента по rng: дирижёр передаёт разный rng — разные ленты
        if (_feed is not null) StartReception();
        Timeline();
        Render();
    }

    public void OnData(string key, DataSnapshot snapshot)
    {
        if (key == "clock" && snapshot is ClockSnapshot clock)
        {
            _hasClock = true;
            _clockHour = clock.Local.Hour;
        }
        else if (key == "satellite" && snapshot is SatelliteSetSnapshot set)
        {
            _set = set;
            SatelliteFeed? same = null;
            if (_feed is not null)
                for (int i = 0; i < set.Feeds.Count; i++)
                    if (set.Feeds[i].Id == _feed.Id && HasFrames(set.Feeds[i])) same = set.Feeds[i];
            if (same is not null) Apply(same);
            else PickFeed();
        }
    }

    public void OnAudio(in AudioFrame audio)
    {
        if (audio.Active && audio.Beat) _flash = 1f;
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;
        _t += dt;
        _blink += dt;
        _flash = MathF.Max(0f, _flash - dt * 6f);
        if (_recT < RecSec) _recT += dt;
        _night = _dimFrame || (_hasClock && (_clockHour >= 21 || _clockHour < 6));
        _zoom = _static ? 1f + 0.04f * (0.5f - 0.5f * MathF.Cos((float)_t * 0.07f)) : 1f; // Ken Burns ≤ 4%
        Timeline();
        Render();
    }

    public void Draw(CharGrid grid)
    {
        _canvas.Present(grid);

        bool night = _night;
        Rgba acc = night ? NightAccent : DayAccent;
        Rgba warm = night ? NightWarm : DayWarm;
        Rgba dim = night ? NightDim : DayDim;

        if (_feed is null || _curA < 0)
        {
            DrawNoSignal(grid, warm);
            return;
        }

        bool eq = _feed.Area.Projection == SatelliteProjection.Equirect;
        if (eq) DrawEqGrid(grid, dim, warm);
        DrawMarker(grid, acc);
        if (eq && _curA == _frames.Length - 1 && _clouds.Count > 0)
            DrawClouds(grid, _flash > 0.3f ? warm : acc);
        DrawHeader(grid, acc, warm);
        if (_recT < RecSec) DrawProgress(grid, acc);
    }

    // ---------- Данные ----------

    private static bool Valid(SatelliteFrame f) =>
        f.Width > 0 && f.Height > 0 && f.Rgb is not null && f.Rgb.Length >= f.Width * f.Height * 3;

    private static bool HasFrames(SatelliteFeed f)
    {
        for (int i = 0; i < f.Frames.Count; i++) if (Valid(f.Frames[i])) return true;
        return false;
    }

    /// <summary>
    /// Выбрать ленту по rng (детерминированно для данного rng). Ленты с тёмным последним кадром пропускаются,
    /// если есть светлые; если других нет — тёмная показывается.
    /// </summary>
    private void PickFeed()
    {
        if (_set is null) { Apply(null); return; }
        var feeds = _set.Feeds;
        int nAll = 0, nLight = 0;
        for (int i = 0; i < feeds.Count; i++)
        {
            if (!HasFrames(feeds[i])) continue;
            nAll++;
            if (!IsDark(feeds[i])) nLight++;
        }
        if (nAll == 0) { Apply(null); return; }
        bool onlyLight = nLight > 0;
        int n = onlyLight ? nLight : nAll;
        int k = _rng.Next(n);
        for (int i = 0; i < feeds.Count; i++)
        {
            if (!HasFrames(feeds[i]) || (onlyLight && IsDark(feeds[i]))) continue;
            if (k-- == 0) { Apply(feeds[i]); return; }
        }
    }

    /// <summary>Последний кадр почти чёрный: доля пикселей яркости &lt; 12/255 больше 70% (для диска — внутри диска).</summary>
    private static bool IsDark(SatelliteFeed feed)
    {
        SatelliteFrame? last = null;
        for (int i = feed.Frames.Count - 1; i >= 0 && last is null; i--)
            if (Valid(feed.Frames[i])) last = feed.Frames[i];
        if (last is null) return false;

        int w = last.Width, h = last.Height;
        bool disk = feed.Area.Projection == SatelliteProjection.FullDisk;
        double ds = feed.Area.DiskScale > 0 ? feed.Area.DiskScale : 1.0;
        double cx = w * 0.5, cy = h * 0.5, r = w * 0.5 * ds;
        int total = 0, dark = 0;
        byte[] d = last.Rgb;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (disk)
                {
                    double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                    if (dx * dx + dy * dy > r * r) continue;
                }
                int i = (y * w + x) * 3;
                total++;
                if (d[i] * 299 + d[i + 1] * 587 + d[i + 2] * 114 < 12 * 1000) dark++; // яркость < 12/255
            }
        return total > 0 && dark / (double)total > 0.7;
    }

    private void Apply(SatelliteFeed? feed)
    {
        if (feed is null)
        {
            _feed = null;
            _frames = Array.Empty<SatelliteFrame>();
            _clouds.Clear();
            return;
        }

        int n = 0;
        for (int i = 0; i < feed.Frames.Count; i++) if (Valid(feed.Frames[i])) n++;
        if (n == 0) { Apply(null); return; }

        var frames = new SatelliteFrame[n];
        int k = 0;
        for (int i = 0; i < feed.Frames.Count; i++) if (Valid(feed.Frames[i])) frames[k++] = feed.Frames[i];
        SatelliteFrame last = frames[n - 1];

        // Тот же ряд кадров (та же лента, тот же последний кадр) — пересчёт трекинга и приём не нужны.
        bool same = _feed is not null && _feed.Id == feed.Id && _frames.Length == n && _frames[n - 1].TakenAt == last.TakenAt;
        _feed = feed;
        _frames = frames;
        if (same) return;

        var area = feed.Area;
        _label = GlyphSet.Sanitize(feed.Label).ToUpperInvariant();
        _hasMark = !double.IsNaN(area.MarkLat) && !double.IsNaN(area.MarkLon);
        _markLabel = GlyphSet.Sanitize(area.MarkName ?? "").ToUpperInvariant();
        _dimFrame = MeanLuma(last) < DimLuma;
        _static = n < MinTimelapse;
        // Базовый кадр для скорости: последний кадр не раньше 20 минут назад; если таких нет — предыдущий.
        SatelliteFrame? baseF = null;
        for (int i = n - 2; i >= 0 && baseF is null; i--)
            if (last.TakenAt - frames[i].TakenAt >= TimeSpan.FromMinutes(20)) baseF = frames[i];
        if (baseF is null && n >= 2) baseF = frames[n - 2];
        if (area.Projection == SatelliteProjection.Equirect && baseF is not null) _clouds.Compute(last, baseF, area);
        else _clouds.Clear();
        StartReception();
    }

    private void StartReception() => _recT = 0f;

    private static float MeanLuma(SatelliteFrame f)
    {
        double acc = 0;
        int n = 0;
        byte[] d = f.Rgb;
        for (int p = 0; p < f.Width * f.Height; p += 4)
        {
            int i = p * 3;
            acc += (d[i] * 299 + d[i + 1] * 587 + d[i + 2] * 114) / 1000.0 / 255.0;
            n++;
        }
        return n == 0 ? 0f : (float)(acc / n);
    }

    // ---------- Время и выбор кадра ----------

    /// <summary>Таймлапс: кадры по FrameStep с перекрёстным смешением, пауза HoldSec на последнем. Меньше 3 кадров — статично.</summary>
    private void Timeline()
    {
        int n = _frames.Length;
        if (n == 0) { _curA = _curB = -1; _curF = 0; return; }
        if (_static) { _curA = _curB = n - 1; _curF = 0; return; }

        double seg = (n - 1) * FrameStep;
        double period = seg + HoldSec;
        double tau = _t % period;
        if (tau < seg)
        {
            int i = Math.Min((int)(tau / FrameStep), n - 2);
            _curA = i;
            _curB = i + 1;
            _curF = (float)Math.Clamp((tau - i * FrameStep) / FrameStep, 0.0, 1.0);
        }
        else
        {
            _curA = _curB = n - 1;
            _curF = 0;
        }
    }

    // ---------- Рендер канваса ----------

    private void Render()
    {
        var c = _canvas;
        int w = c.Width, h = c.Height;
        Rgba[] px = c.Pixels;

        if (_feed is null || _curA < 0)
        {
            // Заглушка: пустой шар с сеткой.
            Array.Fill(px, Rgba.Black);
            double cx = w * 0.5, cy = h * 0.5, r = Math.Max(1.0, h * 0.5 - 0.5);
            SetDisk(cx, cy, r);
            DrawGlobeGrid(px, w, h, cx, cy, r, 0.0, DayDim, 2.0, 0.5f);
        }
        else if (_feed.Area.Projection == SatelliteProjection.FullDisk)
        {
            RenderDisk(px, w, h);
        }
        else
        {
            RenderEquirect(px, w, h);
        }

        if (_feed is not null && _curA >= 0) ApplyReveal(px, w, h);
        if (_night)
            for (int i = 0; i < px.Length; i++) px[i] = Amber(px[i]);
    }

    private void SetDisk(double cx, double cy, double r)
    {
        _diskCx = cx;
        _diskCy = cy;
        _diskR = r;
    }

    private void RenderEquirect(Rgba[] px, int w, int h)
    {
        var a = _frames[_curA];
        var b = _frames[_curB];
        double fw = a.Width, fh = a.Height;
        double s = Math.Min(w / fw, h / fh) * _zoom;      // вписать с сохранением пропорций
        double picW = fw * s, picH = fh * s;
        double pan = _static ? w * 0.01 * Math.Sin(_t * 0.04) : 0;
        double x0 = (w - picW) * 0.5 + pan, y0 = (h - picH) * 0.5;
        _picS = s;
        _picX0 = x0;
        _picY0 = y0;
        _picFw = fw;
        _picFh = fh;

        double bsx = b.Width / fw, bsy = b.Height / fh;
        float f = _curB != _curA ? _curF : 0f;
        for (int y = 0; y < h; y++)
        {
            double v = (y + 0.5 - y0) / s;
            for (int x = 0; x < w; x++)
            {
                double u = (x + 0.5 - x0) / s;
                Rgba col = Rgba.Black;
                if (u >= 0 && v >= 0 && u < fw && v < fh)
                {
                    col = Sample(a, u, v);
                    if (f > 0f) col = Rgba.Lerp(col, Sample(b, u * bsx, v * bsy), f);
                }
                px[y * w + x] = col;
            }
        }
    }

    private void RenderDisk(Rgba[] px, int w, int h)
    {
        var a = _frames[_curA];
        var b = _frames[_curB];
        double cx = w * 0.5, cy = h * 0.5;
        double r = Math.Max(1.0, h * 0.5 - 0.5);            // диск вписан по высоте
        double ds = _feed!.Area.DiskScale > 0 ? _feed.Area.DiskScale : 1.0;
        double rf = a.Width * 0.5 * ds;                     // радиус диска в пикселях кадра
        double bsx = b.Width / (double)a.Width, bsy = b.Height / (double)a.Height;
        float f = _curB != _curA ? _curF : 0f;
        float bri = 0.92f + 0.08f * MathF.Sin((float)_t * 0.5f); // медленное «дыхание» яркости

        SetDisk(cx, cy, r);
        for (int y = 0; y < h; y++)
        {
            double dy = (y + 0.5 - cy) / r;
            for (int x = 0; x < w; x++)
            {
                double dx = (x + 0.5 - cx) / r;
                if (dx * dx + dy * dy > 1)
                {
                    px[y * w + x] = Rgba.Black;
                    continue;
                }
                double fx = a.Width * 0.5 + dx * rf, fy = a.Height * 0.5 + dy * rf;
                Rgba col = Sample(a, fx, fy);
                if (f > 0f) col = Rgba.Lerp(col, Sample(b, fx * bsx, fy * bsy), f);
                px[y * w + x] = col.Scale(bri);
            }
        }
        DrawGlobeGrid(px, w, h, cx, cy, r, _feed.Area.SubLon, DayGrid, 4.0, 0.15f);
    }

    /// <summary>Сетка широт (каждые 15°) и долгот (каждые 15°) на видимой полусфере, ортографическая проекция.</summary>
    /// <param name="step">Шаг точек вдоль линий, градусы (реже — меньше точек).</param>
    /// <param name="alpha">Сила линии: 0.5 — заглушка, 0.15 — сетка поверх картинки диска (≈30% от прежней).</param>
    private static void DrawGlobeGrid(Rgba[] px, int w, int h, double cx, double cy, double r, double sub, Rgba col,
        double step, float alpha)
    {
        for (int lat = -75; lat <= 75; lat += 15)
            for (double lon = -180; lon <= 180; lon += step)
                PlotGlobe(px, w, h, cx, cy, r, lat, lon, sub, col, alpha);
        for (int lon = -180; lon < 180; lon += 15)
            for (double lat = -90; lat <= 90; lat += step)
                PlotGlobe(px, w, h, cx, cy, r, lat, lon, sub, col, alpha);
    }

    private static void PlotGlobe(Rgba[] px, int w, int h, double cx, double cy, double r,
        double lat, double lon, double sub, Rgba col, float alpha)
    {
        double phi = lat * Math.PI / 180, dl = (lon - sub) * Math.PI / 180;
        double cphi = Math.Cos(phi);
        if (cphi * Math.Cos(dl) <= 0) return; // обратная сторона
        int ix = (int)Math.Floor(cx + r * cphi * Math.Sin(dl));
        int iy = (int)Math.Floor(cy - r * Math.Sin(phi));
        if ((uint)ix >= (uint)w || (uint)iy >= (uint)h) return;
        px[iy * w + ix] = Rgba.Lerp(px[iy * w + ix], col, alpha);
    }

    /// <summary>Приём: строки ниже фронта чёрные, последняя принятая строка подсвечена (вспышка — ярче).</summary>
    private void ApplyReveal(Rgba[] px, int w, int h)
    {
        float p = Math.Clamp(_recT / RecSec, 0f, 1f);
        int rows = (int)(h * p);
        if (rows >= h) return;
        for (int i = rows * w; i < px.Length; i++) px[i] = Rgba.Black;
        if (rows <= 0) return;
        Rgba hi = Rgba.Lerp(DayAccent, White, _flash * 0.5f);
        int line = (rows - 1) * w;
        for (int x = 0; x < w; x++) px[line + x] = Rgba.Lerp(px[line + x], hi, 0.8f);
    }

    /// <summary>Выборка кадра (билинейная) по координатам пикселей кадра; u, v — центры пикселей в целых координатах.</summary>
    private static Rgba Sample(SatelliteFrame f, double u, double v)
    {
        byte[] d = f.Rgb;
        int w = f.Width, h = f.Height;
        double fx = Math.Clamp(u - 0.5, 0.0, w - 1), fy = Math.Clamp(v - 0.5, 0.0, h - 1);
        int x0 = (int)fx, y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = (float)(fx - x0), ty = (float)(fy - y0);
        int i00 = (y0 * w + x0) * 3, i10 = (y0 * w + x1) * 3, i01 = (y1 * w + x0) * 3, i11 = (y1 * w + x1) * 3;
        return new Rgba(
            Bil(d, 0, i00, i10, i01, i11, tx, ty),
            Bil(d, 1, i00, i10, i01, i11, tx, ty),
            Bil(d, 2, i00, i10, i01, i11, tx, ty));
    }

    private static byte Bil(byte[] d, int ch, int i00, int i10, int i01, int i11, float tx, float ty)
    {
        float top = d[i00 + ch] + (d[i10 + ch] - d[i00 + ch]) * tx;
        float bot = d[i01 + ch] + (d[i11 + ch] - d[i01 + ch]) * tx;
        return (byte)(top + (bot - top) * ty + 0.5f);
    }

    /// <summary>Ночь: яркость → чёрный → тёмно-янтарный → светло-янтарный.</summary>
    private static Rgba Amber(Rgba c)
    {
        float l = (0.299f * c.R + 0.587f * c.G + 0.114f * c.B) / 255f;
        return l < 0.5f
            ? Rgba.Lerp(Rgba.Black, AmberMid, l * 2f)
            : Rgba.Lerp(AmberMid, AmberHi, (l - 0.5f) * 2f);
    }

    // ---------- HUD ----------

    private void DrawNoSignal(CharGrid g, Rgba warm)
    {
        int y = g.Rows / 2;
        int x = (g.Cols - NoSignal.Length) / 2;
        g.Text(x, y, NoSignal, warm, Rgba.Black);
        if (((int)(_blink * 2f) & 1) == 0) g.Put(x + NoSignal.Length + 1, y, '█', warm, null);
    }

    /// <summary>Сетка 5° для Equirect: пунктир по всему экрану (и в полях вне кадра), подписи у края.</summary>
    private void DrawEqGrid(CharGrid g, Rgba dim, Rgba warm)
    {
        var a = _feed!.Area;
        double lonSpan = a.East - a.West, latSpan = a.North - a.South;
        if (lonSpan <= 0 || latSpan <= 0 || _picS <= 0) return;
        double s = _picS, x0 = _picX0, y0 = _picY0, fw = _picFw, fh = _picFh;
        int cols = g.Cols, rows = g.Rows;
        double pxH = rows * 2.0;

        double lonA = a.West + (0 - x0) / s / fw * lonSpan, lonB = a.West + (cols - x0) / s / fw * lonSpan;
        double latA = a.North - (0 - y0) / s / fh * latSpan, latB = a.North - (pxH - y0) / s / fh * latSpan;
        double lonMin = Math.Max(-180, Math.Min(lonA, lonB)), lonMax = Math.Min(180, Math.Max(lonA, lonB));
        double latMin = Math.Max(-90, Math.Min(latA, latB)), latMax = Math.Min(90, Math.Max(latA, latB));

        for (int lon = (int)Math.Ceiling(lonMin / 5) * 5; lon <= lonMax; lon += 5)
        {
            int col = (int)Math.Floor(x0 + (lon - a.West) / lonSpan * fw * s);
            if (col < 0 || col >= cols) continue;
            for (int row = 2; row <= rows - 3; row += 2) g.Put(col, row, '·', dim, null);
            if (col < cols - 30) g.Text(col + 1, rows - 2, LonTxt[(lon + 180) / 5], dim, Rgba.Black);
        }

        for (int lat = (int)Math.Ceiling(latMin / 5) * 5; lat <= latMax; lat += 5)
        {
            int row = (int)Math.Floor((y0 + (a.North - lat) / latSpan * fh * s) / 2);
            if (row < 2 || row > rows - 3) continue;
            for (int col = 0; col < cols; col += 2) g.Put(col, row, '·', dim, null);
            g.Text(2, row, LatTxt[(lat + 90) / 5], warm, Rgba.Black);
        }
    }

    /// <summary>Крестик ┼ и подпись города, если точка попадает в кадр.</summary>
    private void DrawMarker(CharGrid g, Rgba acc)
    {
        if (!_hasMark || _feed is null) return;
        if (!MarkerCell(_feed.Area, out int col, out int row)) return;
        g.Put(col, row, '┼', acc, null);
        for (int k = 0; k < _markLabel.Length; k++) g.Put(col + 2 + k, row, _markLabel[k], acc, null);
    }

    private bool MarkerCell(SatelliteArea a, out int col, out int row)
    {
        col = row = 0;
        double lat = a.MarkLat, lon = a.MarkLon;
        if (a.Projection == SatelliteProjection.Equirect)
        {
            if (a.East <= a.West || a.North <= a.South) return false;
            if (lat < a.South || lat > a.North || lon < a.West || lon > a.East) return false;
            double px = _picX0 + (lon - a.West) / (a.East - a.West) * _picFw * _picS;
            double py = _picY0 + (a.North - lat) / (a.North - a.South) * _picFh * _picS;
            col = (int)Math.Floor(px);
            row = (int)Math.Floor(py / 2);
            return true;
        }

        double phi = lat * Math.PI / 180, dl = (lon - a.SubLon) * Math.PI / 180;
        if (Math.Cos(phi) * Math.Cos(dl) <= 0) return false;
        col = (int)Math.Floor(_diskCx + _diskR * Math.Cos(phi) * Math.Sin(dl));
        row = (int)Math.Floor((_diskCy - _diskR * Math.Sin(phi)) / 2);
        return true;
    }

    /// <summary>Рамки-уголки вокруг 2–3 облаков (только на последнем кадре, где трекинг посчитан); «дышат» на ±1 клетку.</summary>
    private void DrawClouds(CharGrid g, Rgba col)
    {
        double picW = _picFw * _picS, picH = _picFh * _picS;
        int pulse = Math.Sin(_t * 2.2) > 0 ? 1 : 0;
        for (int k = 0; k < _clouds.Count; k++)
        {
            var cl = _clouds.Items[k];
            int c0 = (int)Math.Floor(_picX0 + cl.U0 * picW) - pulse;
            int c1 = (int)Math.Ceiling(_picX0 + cl.U1 * picW) - 1 + pulse;
            int r0 = (int)Math.Floor((_picY0 + cl.V0 * picH) / 2) - pulse;
            int r1 = (int)Math.Ceiling((_picY0 + cl.V1 * picH) / 2) - 1 + pulse;
            if (c1 < c0) c1 = c0;
            if (r1 < r0) r1 = r0;
            // Рамка не уходит за экран: углы — в безопасной зоне (x ∈ [2, cols-3], y ∈ [1, rows-2]).
            c0 = Math.Max(0, Math.Min(g.Cols - 1, c0));
            c1 = Math.Max(0, Math.Min(g.Cols - 1, c1));
            r0 = Math.Max(0, Math.Min(g.Rows - 1, r0));
            r1 = Math.Max(0, Math.Min(g.Rows - 1, r1));
            g.Put(c0, r0, '┌', col, null);
            g.Put(c1, r0, '┐', col, null);
            g.Put(c0, r1, '└', col, null);
            g.Put(c1, r1, '┘', col, null);

            // Подпись над рамкой (или под ней), целиком внутри экрана.
            int ly = r0 - 1 >= 2 ? r0 - 1 : r1 + 1;
            int lx = Math.Min(c0, g.Cols - 3 - cl.Label.Length);
            if (lx < 2) lx = 2;
            if (ly >= 2 && ly <= g.Rows - 3) g.Text(lx, ly, cl.Label, col, Rgba.Black);
        }
    }

    /// <summary>Шапка: подпись ленты слева; справа «дд.мм чч:мм UTC · КАДР нн/мм» по текущему кадру.</summary>
    private void DrawHeader(CharGrid g, Rgba acc, Rgba warm)
    {
        int n = _frames.Length;
        int idx = _curF < 0.5f ? _curA : _curB;
        DateTime u = _frames[idx].TakenAt.UtcDateTime;

        Span<char> b = stackalloc char[28];
        int i = 0;
        i = Put2(b, i, u.Day);
        b[i++] = '.';
        i = Put2(b, i, u.Month);
        b[i++] = ' ';
        i = Put2(b, i, u.Hour);
        b[i++] = ':';
        i = Put2(b, i, u.Minute);
        foreach (char ch in " UTC · КАДР ") b[i++] = ch;
        i = Put2(b, i, idx + 1);
        b[i++] = '/';
        Put2(b, i, n);

        int rightX = g.Cols - 3 - b.Length;
        int maxLabel = rightX - 4;
        for (int k = 0; k < Math.Min(_label.Length, maxLabel); k++) g.Put(2 + k, 1, _label[k], acc, Rgba.Black);
        for (int k = 0; k < b.Length; k++) g.Put(rightX + k, 1, b[k], warm, Rgba.Black);
    }

    /// <summary>«► ПРИЁМ ████████░░ 82%» внизу справа, пока идёт приём.</summary>
    private void DrawProgress(CharGrid g, Rgba acc)
    {
        int pct = (int)(Math.Clamp(_recT / RecSec, 0f, 1f) * 100f);
        Span<char> b = stackalloc char[24];
        int i = 0;
        foreach (char ch in RecPrefix) b[i++] = ch;
        for (int k = 0; k < 10; k++) b[i++] = k < pct / 10 ? '█' : '░';
        b[i++] = ' ';
        if (pct >= 100)
        {
            b[i++] = '1';
            b[i++] = '0';
            b[i++] = '0';
        }
        else
        {
            if (pct >= 10) b[i++] = (char)('0' + pct / 10);
            b[i++] = (char)('0' + pct % 10);
        }
        b[i++] = '%';

        int x = g.Cols - 3 - i, y = g.Rows - 2;
        for (int k = 0; k < i; k++) g.Put(x + k, y, b[k], acc, Rgba.Black);
    }

    private static int Put2(Span<char> b, int at, int v)
    {
        v = Math.Abs(v) % 100;
        b[at] = (char)('0' + v / 10);
        b[at + 1] = (char)('0' + v % 10);
        return at + 2;
    }

    private static string[] BuildLat()
    {
        var r = new string[37];
        for (int i = 0; i < r.Length; i++)
        {
            int v = (i - 18) * 5;
            r[i] = v == 0 ? "0°"
                : v > 0 ? v.ToString(CultureInfo.InvariantCulture) + "°N"
                : (-v).ToString(CultureInfo.InvariantCulture) + "°S";
        }
        return r;
    }

    private static string[] BuildLon()
    {
        var r = new string[73];
        for (int i = 0; i < r.Length; i++)
        {
            int v = (i - 36) * 5;
            r[i] = v == 0 ? "0°"
                : v == 180 || v == -180 ? "180°"
                : v > 0 ? v.ToString(CultureInfo.InvariantCulture) + "°E"
                : (-v).ToString(CultureInfo.InvariantCulture) + "°W";
        }
        return r;
    }
}
