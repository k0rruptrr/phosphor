using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Phosphor.Core;

namespace Phosphor.Data;

/// <summary>
/// Одна спутниковая лента (ключ "satellite", источник = лента). Кольцо кадров живёт здесь и переживает перезапуск через кеш (IRestorable).
/// meteosat — EUMETSAT WMS, окно вокруг точки, кольцо 12 кадров (2 ч), шаг 10 мин; при пустом кеше дозагружает историю.
/// himawari — NICT, весь диск, 2×2 плитки склеиваются и уменьшаются, кольцо 4 кадра, шаг 10 мин.
/// viirs — NASA GIBS, истинный цвет за вчера (UTC), один кадр, раз в 6 ч.
/// Сцена сеть не трогает: лента отдаёт снимок хабу, хаб — сцене через SatelliteCatalog.Merge.
/// </summary>
public sealed class SatelliteSource : IDataSource, IRestorable
{
    public const string MeteosatId = "meteosat";
    public const string HimawariId = "himawari";
    public const string ViirsId = "viirs";

    /// <summary>Допустимые id лент. Порядок — порядок слияния.</summary>
    public static readonly string[] Ids = { MeteosatId, HimawariId, ViirsId };

    private const string MeteosatLayer = "mtg_fd:rgb_geocolour";
    private const int MeteosatRing = 12;
    private const int MeteosatSide = 384;   // ≤ 480 (см. Data.cs); 12 кадров укладываются в ~5 МБ кеша
    private const int HimawariRing = 4;
    private const int HimawariSide = 480;
    private const int ViirsSide = 480;
    private const double HalfLatSpan = 12.5;                 // окно ±12.5° по широте
    private const double DefaultS = 45, DefaultW = 20, DefaultN = 65, DefaultE = 60;
    private const double HimawariSubLon = 140.7;
    private const double HimawariDiskScale = 0.99;           // диск почти вписан в кадр (измерено по 2×2 composite)
    private const double VisibleAngleDeg = 75;               // город виден, если угол от подспутниковой точки < 75°
    private const int EmptyTileBytes = 4096;                 // заглушка «No Image» ~2.8 КБ; настоящая плитка диска — десятки КБ
    private const int DarkChannelMax = 12;                   // пиксель VIIRS «чёрный», если все каналы ≤ 12
    private const double DarkShare = 0.6;                    // кадр «почти пустой», если чёрных больше 60%
    private static readonly (double Lat, double Lon, string Name) Tokyo = (35.68, 139.69, "ТОКИО");
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(10);

    private readonly string _id;
    private readonly Func<(double Lat, double Lon, string Name)?> _where;
    private readonly TimeSpan _requestPause;
    private readonly List<SatelliteFrame> _ring = new();
    private readonly HashSet<DateTimeOffset> _skipped = new();   // слоты истории, которые не отдались (в памяти)
    private SatelliteArea? _area;

    private SatelliteSource(string id, Func<(double Lat, double Lon, string Name)?> where, TimeSpan requestPause)
    {
        _id = id;
        _where = where;
        _requestPause = requestPause;
    }

    /// <summary>
    /// Лента по id или null для неизвестного id.
    /// where — точка интереса (город): null — нет точки, meteosat/viirs берут окно по умолчанию, himawari метит Токио.
    /// requestPause — пауза между запросами истории (по умолчанию 1 с; тесты передают ноль).
    /// </summary>
    public static IDataSource? Create(string id, Func<(double Lat, double Lon, string Name)?> where, TimeSpan? requestPause = null) =>
        id is MeteosatId or HimawariId or ViirsId
            ? new SatelliteSource(id, where, requestPause ?? TimeSpan.FromSeconds(1))
            : null;

    public string Key => "satellite";
    public string Name => "satellite." + _id;
    public TimeSpan Interval => _id == ViirsId ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(10);

    private int Capacity => _id switch
    {
        MeteosatId => MeteosatRing,
        HimawariId => HimawariRing,
        _ => 1,
    };

    private static string LabelOf(string id) => id switch
    {
        MeteosatId => "METEOSAT-12 · FCI · GEOCOLOUR",
        HimawariId => "HIMAWARI-9 · AHI · TRUE COLOR",
        _ => "NOAA-20 · VIIRS · TRUE COLOR",
    };

    /// <summary>Кольцо и окно из кеша. Кадры другого окна (смена города) потом сбросятся при первом обновлении.</summary>
    public void Restore(DataSnapshot cached)
    {
        if (cached is not SatelliteFeed f || f.Id != _id) return;
        _ring.Clear();
        _ring.AddRange(f.Frames);
        TrimRing();
        _area = f.Area;
    }

    public async Task<DataSnapshot?> FetchAsync(HttpClient http, CancellationToken ct)
    {
        var where = _where();
        if (_id == HimawariId)
        {
            var disk = DiskArea(where);
            await UpdateHimawariAsync(http, ct).ConfigureAwait(false);
            return Finish(disk);
        }

        var win = WindowOf(where);
        var area = EquirectArea(win, where);
        // Смена окна (город) делает кадры неверными: кольцо начинаем заново.
        if (_area is { } old && !SameWindow(old, area)) { _ring.Clear(); _skipped.Clear(); }
        if (_id == MeteosatId) await UpdateMeteosatAsync(http, win, ct).ConfigureAwait(false);
        else await UpdateViirsAsync(http, win, ct).ConfigureAwait(false);
        return Finish(area);
    }

    private SatelliteFeed Finish(SatelliteArea area)
    {

        _area = area;
        if (_ring.Count == 0) throw new InvalidOperationException("нет ни одного кадра");
        return new SatelliteFeed(_id, LabelOf(_id), area, _ring.ToArray(), DateTimeOffset.Now, false);
    }

    // ---- meteosat ----

    private async Task UpdateMeteosatAsync(HttpClient http, Window win, CancellationToken ct)
    {
        var last = await MeteosatLatestAsync(http, ct).ConfigureAwait(false);
        var (w, h) = PixelSize(win, MeteosatSide);

        // Целевое кольцо: последний кадр и 11 предыдущих с шагом 10 мин, по возрастанию времени.
        var targets = new List<DateTimeOffset>(MeteosatRing);
        for (int i = MeteosatRing - 1; i >= 0; i--) targets.Add(last - Step * i);

        var next = new List<SatelliteFrame>(MeteosatRing);
        int requests = 0;
        foreach (var t in targets)
        {
            var have = _ring.Find(f => f.TakenAt == t);
            if (have is not null)
            {
                next.Add(have);
                continue;
            }
            // Слот, который уже не отдался, больше не запрашиваем — кольцо просто короче.
            if (_skipped.Contains(t)) continue;
            if (requests++ > 0 && _requestPause > TimeSpan.Zero) await Task.Delay(_requestPause, ct).ConfigureAwait(false);
            try
            {
                var img = ImageDecode.Decode(await GetBytesAsync(http, MeteosatUrl(win, w, h, t), ct).ConfigureAwait(false));
                img = ImageDecode.Downscale(img, w, h);
                next.Add(new SatelliteFrame(t, img.Width, img.Height, img.Rgb));
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Самый свежий слот может появиться позже: его перезапросим на следующем обновлении.
                // Более старые пропуски запоминаем навсегда (до сброса окна).
                if (t != targets[^1]) _skipped.Add(t);
            }
        }
        _skipped.RemoveWhere(t => t < targets[0]);

        if (next.Count == 0) throw new InvalidOperationException("EUMETSAT: ни один кадр не получен");
        _ring.Clear();
        _ring.AddRange(next);
        TrimRing();
    }

    /// <summary>Время последнего кадра слоя — default у Dimension time в GetCapabilities (UTC).</summary>
    private static async Task<DateTimeOffset> MeteosatLatestAsync(HttpClient http, CancellationToken ct)
    {
        var xml = await http.GetStringAsync(
            "https://view.eumetsat.int/geoserver/ows?service=WMS&version=1.3.0&request=GetCapabilities", ct).ConfigureAwait(false);
        var layer = XDocument.Parse(xml).Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Layer"
                                 && e.Elements().Any(n => n.Name.LocalName == "Name" && n.Value == MeteosatLayer))
            ?? throw new InvalidOperationException("EUMETSAT: слоя " + MeteosatLayer + " нет в GetCapabilities");
        var time = layer.Elements().FirstOrDefault(e => e.Name.LocalName == "Dimension" && (string?)e.Attribute("name") == "time");
        var def = (string?)time?.Attribute("default") ?? throw new InvalidOperationException("EUMETSAT: у слоя нет времени по умолчанию");
        return DateTimeOffset.Parse(def, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    private static string MeteosatUrl(Window win, int w, int h, DateTimeOffset t) =>
        FormattableString.Invariant($"https://view.eumetsat.int/geoserver/ows?service=WMS&version=1.3.0&request=GetMap&layers={MeteosatLayer}&crs=EPSG:4326&bbox={Num(win.S)},{Num(win.W)},{Num(win.N)},{Num(win.E)}&width={w}&height={h}&format=image/png&time={Iso(t)}");

    // ---- himawari ----

    private async Task UpdateHimawariAsync(HttpClient http, CancellationToken ct)
    {
        var json = await http.GetStringAsync("https://himawari.asia/img/D531106/latest.json", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var date = DateTime.ParseExact(doc.RootElement.GetProperty("date").GetString()!, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var latest = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc));

        // Последний слот может быть ещё не выложен (404) или заглушкой — откат на 10 мин, не больше трёх раз.
        for (int back = 0; back <= 3; back++)
        {
            var t = latest - Step * back;
            if (_ring.Any(f => f.TakenAt == t)) return;   // уже есть — новее нет
            var frame = await HimawariSlotAsync(http, t, ct).ConfigureAwait(false);
            if (frame is null) continue;
            _ring.Add(frame);
            TrimRing();
            return;
        }
    }

    /// <summary>Один слот полного диска: 2×2 плитки 550 px → 1100 → 480. null — слота нет (404) или заглушка.</summary>
    private static async Task<SatelliteFrame?> HimawariSlotAsync(HttpClient http, DateTimeOffset t, CancellationToken ct)
    {
        var u = t.ToUniversalTime();
        var bytes = new byte[2][][];
        for (int y = 0; y < 2; y++)
        {
            bytes[y] = new byte[2][];
            for (int x = 0; x < 2; x++)
            {
                var url = FormattableString.Invariant($"https://himawari.asia/img/D531106/2d/550/{u:yyyy}/{u:MM}/{u:dd}/{u:HHmmss}_{x}_{y}.png");
                using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.NotFound) return null;
                resp.EnsureSuccessStatusCode();
                bytes[y][x] = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
        }

        // «No Image»: все плитки крошечные (почти чёрные) — слота по сути нет.
        if (bytes.All(row => row.All(b => b.Length <= EmptyTileBytes))) return null;

        var grid = new RgbImage[2][];
        for (int y = 0; y < 2; y++)
        {
            grid[y] = new RgbImage[2];
            for (int x = 0; x < 2; x++) grid[y][x] = ImageDecode.Decode(bytes[y][x]);
        }
        var disk = ImageDecode.Stitch(grid);
        var img = ImageDecode.Downscale(disk, HimawariSide, HimawariSide);
        return new SatelliteFrame(u, img.Width, img.Height, img.Rgb);
    }

    // ---- viirs ----

    private async Task UpdateViirsAsync(HttpClient http, Window win, CancellationToken ct)
    {
        var (w, h) = PixelSize(win, ViirsSide);
        // Вчера (UTC); если почти чёрный — позавчера. Уже лежащий в кольце день не перекачиваем.
        for (int back = 1; back <= 2; back++)
        {
            var day = DateTime.UtcNow.Date.AddDays(-back);
            var t = new DateTimeOffset(DateTime.SpecifyKind(day, DateTimeKind.Utc));
            if (_ring.Any(f => f.TakenAt == t)) return;

            var img = ImageDecode.Decode(await GetBytesAsync(http, ViirsUrl(win, w, h, day), ct).ConfigureAwait(false));
            img = ImageDecode.Downscale(img, w, h);
            if (back == 1 && ImageDecode.DarkFraction(img, DarkChannelMax) > DarkShare) continue;

            _ring.Clear();
            _ring.Add(new SatelliteFrame(t, img.Width, img.Height, img.Rgb));
            return;
        }
    }

    private static string ViirsUrl(Window win, int w, int h, DateTime day) =>
        FormattableString.Invariant($"https://gibs.earthdata.nasa.gov/wms/epsg4326/best/wms.cgi?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&LAYERS=VIIRS_NOAA20_CorrectedReflectance_TrueColor&CRS=EPSG:4326&BBOX={Num(win.S)},{Num(win.W)},{Num(win.N)},{Num(win.E)}&WIDTH={w}&HEIGHT={h}&FORMAT=image/jpeg&TIME={day:yyyy-MM-dd}");

    // ---- геометрия ----

    /// <summary>Окно в градусах: S, W, N, E.</summary>
    private readonly record struct Window(double S, double W, double N, double E);

    /// <summary>
    /// Окно вокруг точки: высота ±12.5° по широте, ширина = высота / cos(lat), чтобы пропорции на экране были верны.
    /// Без точки — 45..65N, 20..60E (как в ТЗ).
    /// </summary>
    private static Window WindowOf((double Lat, double Lon, string Name)? where)
    {
        if (where is not { } p) return new Window(DefaultS, DefaultW, DefaultN, DefaultE);

        double s = p.Lat - HalfLatSpan, n = p.Lat + HalfLatSpan;
        if (n > 90) { s -= n - 90; n = 90; }
        if (s < -90) { n += -90 - s; s = -90; }
        // У полюсов cos мал: ширину ограничиваем, иначе окно станет почти на весь мир.
        double cos = Math.Max(Math.Cos(p.Lat * Math.PI / 180), Math.Cos(75 * Math.PI / 180));
        double span = Math.Min(360, (n - s) / cos);
        return new Window(s, p.Lon - span / 2, n, p.Lon + span / 2);
    }

    /// <summary>Размер кадра при заданной ширине: высота пропорциональна окну в градусах.</summary>
    private static (int W, int H) PixelSize(Window win, int side)
    {
        double ratio = (win.N - win.S) / (win.E - win.W);
        return (side, Math.Max(1, (int)Math.Round(side * ratio)));
    }

    private static SatelliteArea EquirectArea(Window win, (double Lat, double Lon, string Name)? where) =>
        new(SatelliteProjection.Equirect, win.W, win.S, win.E, win.N, 0, 1,
            where?.Lat ?? double.NaN, where?.Lon ?? double.NaN, where?.Name);

    private static SatelliteArea DiskArea((double Lat, double Lon, string Name)? where)
    {
        var mark = where is { } p && Visible(p.Lat, p.Lon, HimawariSubLon) ? p : Tokyo;
        return new SatelliteArea(SatelliteProjection.FullDisk, -180, -90, 180, 90,
            HimawariSubLon, HimawariDiskScale, mark.Lat, mark.Lon, mark.Name);
    }

    /// <summary>Точка на видимой полусфере: угол от подспутниковой точки (0, subLon) меньше 75°.</summary>
    private static bool Visible(double lat, double lon, double subLon)
    {
        double cos = Math.Cos(lat * Math.PI / 180) * Math.Cos((lon - subLon) * Math.PI / 180);
        double angle = Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI;
        return angle < VisibleAngleDeg;
    }

    private static bool SameWindow(SatelliteArea a, SatelliteArea b) =>
        a.Projection == b.Projection && Near(a.West, b.West) && Near(a.South, b.South)
        && Near(a.East, b.East) && Near(a.North, b.North);

    private static bool Near(double x, double y) => Math.Abs(x - y) < 0.01;

    // ---- общее ----

    private void TrimRing()
    {
        while (_ring.Count > Capacity) _ring.RemoveAt(0);
    }

    private static async Task<byte[]> GetBytesAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
