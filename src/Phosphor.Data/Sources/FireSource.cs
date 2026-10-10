using System.Globalization;
using Phosphor.Core;

namespace Phosphor.Data;

/// <summary>
/// Активные пожары NASA FIRMS (VIIRS NOAA-20, за 24 ч, глобальный CSV без ключа) вокруг точки — ключ "fires".
/// Центр — координаты города, округлённые до 0.5° (примерное местоположение). Квадрат ±RadiusKm, не больше 2000 очагов
/// по убыванию мощности (FRP). CSV читается построчно (файл ~8 МБ). Интервал 3 ч.
/// </summary>
public sealed class FireSource : IDataSource
{
    public const string Url = "https://firms.modaps.eosdis.nasa.gov/data/active_fire/noaa-20-viirs-c2/csv/J1_VIIRS_C2_Global_24h.csv";
    private const int MaxPoints = 2000;
    private const double KmPerDegree = 111.0;
    private static readonly TimeSpan Refresh = TimeSpan.FromHours(3);

    private readonly Func<(double Lat, double Lon, string Name)?> _where;
    private readonly double _radiusKm;

    /// <param name="where">Точка интереса (город): координаты из конфига или из погоды. null — данных нет.</param>
    /// <param name="radiusKm">Полуширина квадрата, км (кламп — в конфиге, 100..1500).</param>
    public FireSource(Func<(double Lat, double Lon, string Name)?> where, double radiusKm)
    {
        _where = where;
        _radiusKm = radiusKm;
    }

    public string Key => "fires";
    public string Name => "fires";
    public TimeSpan Interval => Refresh;
    /// <summary>CSV ~8 МБ: на медленной линии 15 с не хватает.</summary>
    public TimeSpan Timeout => TimeSpan.FromSeconds(180);

    public async Task<DataSnapshot?> FetchAsync(HttpClient http, CancellationToken ct)
    {
        if (_where() is not { } c) return null;
        var lat = RoundHalf(c.Lat);
        var lon = RoundHalf(c.Lon);

        using var resp = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        return await ParseAsync(reader, lat, lon, c.Name, _radiusKm, DateTimeOffset.Now, ct).ConfigureAwait(false);
    }

    /// <summary>Округление до ближайшей половины градуса (0.5°).</summary>
    public static double RoundHalf(double deg) => Math.Round(deg * 2, MidpointRounding.AwayFromZero) / 2;

    /// <summary>
    /// Построчный разбор CSV FIRMS. Очаги вне квадрата отбрасываются сразу; битые строки пропускаются.
    /// Заголовок ищется по именам колонок, порядок колонок не важен.
    /// </summary>
    public static async Task<FireSnapshot> ParseAsync(TextReader reader, double centerLat, double centerLon, string centerName,
        double radiusKm, DateTimeOffset now, CancellationToken ct)
    {
        var header = await reader.ReadLineAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("FIRMS: пустой CSV");
        var cols = header.Split(',');
        int iLat = Col(cols, "latitude"), iLon = Col(cols, "longitude"), iBright = Col(cols, "bright_ti4");
        int iDate = Col(cols, "acq_date"), iTime = Col(cols, "acq_time"), iConf = Col(cols, "confidence");
        int iFrp = Col(cols, "frp"), iDay = Col(cols, "daynight");
        int need = new[] { iLat, iLon, iBright, iDate, iTime, iConf, iFrp, iDay }.Max();
        if (need < 0 || new[] { iLat, iLon, iBright, iDate, iTime, iConf, iFrp, iDay }.Min() < 0)
            throw new InvalidDataException("FIRMS: неожиданный заголовок CSV");

        double dLat = radiusKm / KmPerDegree;
        double dLon = radiusKm / (KmPerDegree * Math.Cos(centerLat * Math.PI / 180));
        var points = new List<FirePoint>();

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0) continue;
            var f = line.Split(',');
            if (f.Length <= need) continue;

            if (!Num(f[iLat], out var lat) || !Num(f[iLon], out var lon)) continue;
            if (Math.Abs(lat - centerLat) > dLat || Math.Abs(lon - centerLon) > dLon) continue;
            if (!Num(f[iFrp], out var frp) || !Num(f[iBright], out var bright)) continue;
            if (!TryAcquired(f[iDate], f[iTime], out var acquired)) continue;

            points.Add(new FirePoint(lat, lon, (float)frp, (float)bright, Confidence(f[iConf]), acquired, f[iDay] == "D"));
        }

        points.Sort((a, b) => b.Frp.CompareTo(a.Frp));
        if (points.Count > MaxPoints) points.RemoveRange(MaxPoints, points.Count - MaxPoints);

        return new FireSnapshot(centerName, centerLat, centerLon, radiusKm, points, now, false);
    }

    private static int Col(string[] cols, string name)
    {
        for (int i = 0; i < cols.Length; i++)
            if (string.Equals(cols[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static bool Num(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    /// <summary>acq_date — "2026-10-09", acq_time — "HHMM" в UTC (бывает без ведущего нуля: "530").</summary>
    private static bool TryAcquired(string date, string time, out DateTimeOffset at)
    {
        at = default;
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return false;
        if (!int.TryParse(time, NumberStyles.None, CultureInfo.InvariantCulture, out var hhmm)) return false;
        int h = hhmm / 100, m = hhmm % 100;
        if (h > 23 || m > 59) return false;
        at = new DateTimeOffset(d.Year, d.Month, d.Day, h, m, 0, TimeSpan.Zero);
        return true;
    }

    /// <summary>VIIRS C2: "l"/"n"/"h" или "low"/"nominal"/"high" → "low" | "nominal" | "high".</summary>
    private static string Confidence(string s) => s.Trim().ToLowerInvariant() switch
    {
        "l" or "low" => "low",
        "h" or "high" => "high",
        _ => "nominal",
    };
}
