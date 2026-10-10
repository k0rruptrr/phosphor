using System.Globalization;
using System.Text.Json;
using Phosphor.Core;

namespace Phosphor.Data;

/// <summary>
/// Самолёты вокруг точки (ADS-B, adsb.lol, ключ "flights"). Опрос раз в 60 с.
/// Сначала город. Если в воздухе над городом меньше 3 самолётов — следующие опросы идут по кругу к «дальним станциям»
/// из конфига (Remote = true), пока не пройдено 10 станционных опросов; затем снова город.
/// Ответ не JSON — ошибка источника (хаб оставит прошлый снимок и статус).
/// </summary>
public sealed class FlightSource : IDataSource
{
    public const string Endpoint = "https://api.adsb.lol/v2/point/";
    private const int MaxAircraft = 300;
    private const int MinAirborne = 3;
    private const int StationPollsBeforeCity = 10;
    private const double KmPerDegree = 111.0;
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(60);

    /// <summary>Дальняя станция: имя (капсом) и координаты.</summary>
    public readonly record struct Station(string Name, double Lat, double Lon);

    private readonly Func<(double Lat, double Lon, string Name)?> _where;
    private readonly double _radiusNm;
    private readonly IReadOnlyList<Station> _stations;

    // Состояние опроса: сейчас в режиме станций, индекс следующей станции, станционных опросов с последнего города.
    private bool _onStations;
    private int _stationIdx;
    private int _stationPolls;

    /// <param name="where">Город: координаты из конфига или из погоды. null — данных нет.</param>
    /// <param name="radiusNm">Радиус, морские мили (кламп — в конфиге, 20..250).</param>
    /// <param name="stations">Дальние станции (см. ParseStations).</param>
    public FlightSource(Func<(double Lat, double Lon, string Name)?> where, double radiusNm, IReadOnlyList<Station> stations)
    {
        _where = where;
        _radiusNm = radiusNm;
        _stations = stations;
    }

    public string Key => "flights";
    public string Name => "flights";
    public TimeSpan Interval => Poll;

    public async Task<DataSnapshot?> FetchAsync(HttpClient http, CancellationToken ct)
    {
        if (_where() is not { } city) return null;

        // Состояние меняем только после успешного ответа: при ошибке следующий опрос повторит ту же цель.
        if (_onStations && _stations.Count > 0 && _stationPolls < StationPollsBeforeCity)
        {
            var st = _stations[_stationIdx];
            var (list, _) = await QueryAsync(http, st.Lat, st.Lon, ct).ConfigureAwait(false);
            var snap = new FlightSnapshot(st.Name, true, st.Lat, st.Lon, _radiusNm, list, DateTimeOffset.Now, false);
            _stationIdx = (_stationIdx + 1) % _stations.Count;
            _stationPolls++;
            return snap;
        }

        var (cityList, airborne) = await QueryAsync(http, city.Lat, city.Lon, ct).ConfigureAwait(false);
        _onStations = airborne < MinAirborne && _stations.Count > 0;
        _stationPolls = 0;
        return new FlightSnapshot(city.Name.ToUpperInvariant(), false, city.Lat, city.Lon, _radiusNm, cityList, DateTimeOffset.Now, false);
    }

    private async Task<(List<Aircraft> List, int Airborne)> QueryAsync(HttpClient http, double lat, double lon, CancellationToken ct)
    {
        var url = FormattableString.Invariant($"{Endpoint}{lat:0.####}/{lon:0.####}/{(int)Math.Round(_radiusNm)}");
        var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        return Parse(json, lat, lon);
    }

    /// <summary>
    /// Разбор ответа adsb.lol: { "ac": [ ... ] }. Возвращает ближайшие MaxAircraft самолётов к центру и число
    /// самолётов в воздухе (alt_baro — число ≥ 0). Не JSON или без "ac" — JsonException.
    /// </summary>
    public static (List<Aircraft> List, int Airborne) Parse(string json, double centerLat, double centerLon)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("ac", out var ac) || ac.ValueKind != JsonValueKind.Array)
            throw new JsonException("adsb.lol: нет поля ac");

        double cosLat = Math.Cos(centerLat * Math.PI / 180);
        var items = new List<(double Dist, Aircraft A)>();
        int airborne = 0;
        foreach (var e in ac.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            if (!Dbl(e, "lat", out var lat) || !Dbl(e, "lon", out var lon)) continue;

            int alt = -1;
            var ab = Prop(e, "alt_baro");
            if (ab.ValueKind == JsonValueKind.Number)
            {
                double raw = ab.GetDouble();
                alt = (int)Math.Max(0, Math.Round(raw));
                if (raw >= 0) airborne++;
            }   // "ground" и отсутствие поля — на земле (AltFt = -1)

            double dy = (lat - centerLat) * KmPerDegree;
            double dx = (lon - centerLon) * KmPerDegree * cosLat;
            var a = new Aircraft(
                Str(e, "hex"),
                Str(e, "flight").Trim(),
                Str(e, "t"),
                lat, lon, alt,
                (float)(Dbl(e, "gs", out var gs) ? gs : 0),
                (float)(Dbl(e, "track", out var tr) ? tr : 0),
                (float)(Dbl(e, "baro_rate", out var br) ? br : 0));
            items.Add((Math.Sqrt(dx * dx + dy * dy), a));
        }

        items.Sort((x, y) => x.Dist.CompareTo(y.Dist));
        var list = new List<Aircraft>(Math.Min(items.Count, MaxAircraft));
        for (int i = 0; i < items.Count && i < MaxAircraft; i++) list.Add(items[i].A);
        return (list, airborne);
    }

    /// <summary>"ИМЯ:широта:долгота", например "BELGRADE:44.82:20.29". Битые записи — пропускаются с предупреждением.</summary>
    public static IReadOnlyList<Station> ParseStations(IEnumerable<string> specs, Action<string>? warn = null)
    {
        var list = new List<Station>();
        foreach (var spec in specs)
        {
            var p = spec.Split(':');
            if (p.Length == 3
                && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
                && lat is >= -90 and <= 90 && lon is >= -180 and <= 180
                && p[0].Trim().Length > 0)
            {
                list.Add(new Station(p[0].Trim().ToUpperInvariant(), lat, lon));
            }
            else warn?.Invoke($"flights: станция '{spec}' не разобрана (нужно ИМЯ:широта:долгота)");
        }
        return list;
    }

    // --- разбор JSON: толерантно к отсутствующим полям ---

    private static JsonElement Prop(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v : default;

    private static bool Dbl(JsonElement e, string name, out double v)
    {
        var p = Prop(e, name);
        v = 0;
        return p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out v);
    }

    private static string Str(JsonElement e, string name)
    {
        var p = Prop(e, name);
        return p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    }
}
