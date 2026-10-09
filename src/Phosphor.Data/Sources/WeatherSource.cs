using System.Globalization;
using System.Text.Json;
using Phosphor.Core;

namespace Phosphor.Data;

/// <summary>
/// Погода для одного города. Основной провайдер — Open-Meteo, запасной — MET Norway (если Open-Meteo недоступен по сети).
/// Координаты: заданы в конфиге (lat, lon) — геокодер не вызывается; иначе геокодер Open-Meteo (один раз, пока не получился),
/// а если он недоступен — координаты из кеша погоды. Ключ API не нужен. Интервал 30 минут.
/// Пропущенные в ответе значения — NaN (сцена рисует «--»), а не падение всего запроса.
/// </summary>
public sealed class WeatherSource : IDataSource, IRestorable
{
    private const string OpenMeteoName = "open-meteo";
    private const string MetNoName = "met.no";
    private const string MetNoAgent = "PHOSPHOR-screensaver/1.0 (ascii screensaver; personal use)";

    private readonly string _city;
    private GeoPlace? _place;   // координаты: из конфига или геокодера
    private GeoPlace? _cached;  // координаты из кеша погоды — запасной вариант при недоступном геокодере

    /// <param name="lat">Широта из конфига. Вместе с lon (оба заданы) — геокодер не вызывается.</param>
    public WeatherSource(string city, double? lat = null, double? lon = null)
    {
        _city = city;
        if (lat is double la && lon is double lo) _place = new GeoPlace(city, la, lo, null);
    }

    public string Key => "weather";
    public string Name => "weather";
    public TimeSpan Interval => TimeSpan.FromMinutes(30);

    /// <summary>Координаты из кеша погоды. Старый кеш без поля Query принимаем; кеш другого города — нет.</summary>
    public void Restore(DataSnapshot cached)
    {
        if (cached is not WeatherSnapshot w) return;
        if (w.Query is not null && w.Query != _city) return;
        if (!double.IsFinite(w.Lat) || !double.IsFinite(w.Lon)) return;
        _cached = new GeoPlace(w.City, w.Lat, w.Lon, null);
    }

    public async Task<DataSnapshot?> FetchAsync(HttpClient http, CancellationToken ct)
    {
        var place = await ResolveAsync(http, ct).ConfigureAwait(false);

        Exception? first = null;
        try
        {
            return await FetchOpenMeteoAsync(http, place, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsNetworkError(e, ct))
        {
            first = e;
        }

        try
        {
            return await FetchMetNoAsync(http, place, ct).ConfigureAwait(false);
        }
        catch (Exception e2) when (IsNetworkError(e2, ct))
        {
            // Оба провайдера недоступны — причина в статусе та же, что у основного.
            throw first!;
        }
    }

    private async Task<GeoPlace> ResolveAsync(HttpClient http, CancellationToken ct)
    {
        if (_place is not null) return _place;
        try
        {
            _place = await GeocodeAsync(http, ct).ConfigureAwait(false);
            return _place;
        }
        catch (Exception e) when (_cached is not null && IsNetworkError(e, ct))
        {
            return _cached;
        }
    }

    private static bool IsNetworkError(Exception e, CancellationToken ct) =>
        e is HttpRequestException or IOException or System.Net.Sockets.SocketException or TimeoutException
        || (e is OperationCanceledException && !ct.IsCancellationRequested);

    private async Task<DataSnapshot> FetchOpenMeteoAsync(HttpClient http, GeoPlace place, CancellationToken ct)
    {
        var url = FormattableString.Invariant(
            $"https://api.open-meteo.com/v1/forecast?latitude={place.Lat}&longitude={place.Lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_sum&timezone=auto&forecast_days=7");

        var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var cur = Prop(root, "current");
        var now = new WeatherNow(
            Num(Prop(cur, "temperature_2m")),
            Num(Prop(cur, "wind_speed_10m")),
            Num(Prop(cur, "wind_direction_10m")),
            Code(Prop(cur, "weather_code")));

        var daily = Prop(root, "daily");
        var times = Prop(daily, "time");
        var codes = Prop(daily, "weather_code");
        var maxs = Prop(daily, "temperature_2m_max");
        var mins = Prop(daily, "temperature_2m_min");
        var precs = Prop(daily, "precipitation_sum");

        var count = times.ValueKind == JsonValueKind.Array ? times.GetArrayLength() : 0;
        var days = new List<WeatherDay>(count);
        for (int i = 0; i < count; i++)
        {
            var t = times[i];
            if (t.ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(t.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;
            days.Add(new WeatherDay(date, NumAt(mins, i), NumAt(maxs, i), NumAt(precs, i), CodeAt(codes, i)));
        }

        if (double.IsNaN(now.TempC) && days.Count == 0)
            throw new System.Text.Json.JsonException("пустой ответ Open-Meteo");

        return new WeatherSnapshot(place.Name, place.Lat, place.Lon, now, days, DateTimeOffset.Now, false, OpenMeteoName, _city);
    }

    /// <summary>Запасной провайдер: MET Norway locationforecast/compact, свёрнут в 7 локальных дней.</summary>
    private async Task<DataSnapshot> FetchMetNoAsync(HttpClient http, GeoPlace place, CancellationToken ct)
    {
        var url = FormattableString.Invariant($"https://api.met.no/weatherapi/locationforecast/2.0/compact?lat={place.Lat:0.0000}&lon={place.Lon:0.0000}");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", MetNoAgent);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var series = Prop(Prop(doc.RootElement, "properties"), "timeseries");
        if (series.ValueKind != JsonValueKind.Array || series.GetArrayLength() == 0)
            throw new System.Text.Json.JsonException("met.no: пустой timeseries");

        var first = series[0];
        var inst0 = Details(first, "instant");
        var now = new WeatherNow(
            Num(Prop(inst0, "air_temperature")),
            Num(Prop(inst0, "wind_speed")) * 3.6,   // м/с → км/ч
            Num(Prop(inst0, "wind_from_direction")),
            MetCode(Symbol(first, "next_1_hours") ?? Symbol(first, "next_6_hours")));

        var today = DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);
        var acc = new Dictionary<DateOnly, DayAcc>();
        var order = new List<DateOnly>();
        foreach (var e in series.EnumerateArray())
        {
            var timeProp = Prop(e, "time");
            if (timeProp.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(timeProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var utc))
                continue;
            var local = utc.ToLocalTime();
            var date = DateOnly.FromDateTime(local.DateTime);
            if (date < today) continue;
            if (!acc.TryGetValue(date, out var a))
            {
                if (order.Count >= 7) continue;
                a = new DayAcc();
                acc[date] = a;
                order.Add(date);
            }

            a.Fold(Num(Prop(Details(e, "instant"), "air_temperature")));
            var n6 = Details(e, "next_6_hours");
            a.Fold(Num(Prop(n6, "air_temperature_min")));
            a.Fold(Num(Prop(n6, "air_temperature_max")));

            // Осадки: почасовой прогноз, где он есть; дальше — шестичасовой (блоки не перекрываются).
            var n1 = Details(e, "next_1_hours");
            var p = Prop(n1, "precipitation_amount");
            if (p.ValueKind != JsonValueKind.Number) p = Prop(n6, "precipitation_amount");
            if (p.ValueKind == JsonValueKind.Number) a.AddPrecip(p.GetDouble());

            var sym = Symbol(e, "next_1_hours") ?? Symbol(e, "next_6_hours");
            int dist = Math.Abs(local.Hour - 12);
            if (sym is not null && dist < a.CodeDist) { a.CodeDist = dist; a.Symbol = sym; }
        }

        var days = new List<WeatherDay>(order.Count);
        foreach (var d in order)
        {
            var a = acc[d];
            days.Add(new WeatherDay(d, a.MinOrNaN(), a.MaxOrNaN(), a.Precip, MetCode(a.Symbol)));
        }

        return new WeatherSnapshot(place.Name, place.Lat, place.Lon, now, days, DateTimeOffset.Now, false, MetNoName, _city);
    }

    private async Task<GeoPlace> GeocodeAsync(HttpClient http, CancellationToken ct)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(_city)}&count=1&language=ru";
        var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        // Пустой результат Open-Meteo приходит без ключа "results" вовсе — учитываем оба случая.
        if (!doc.RootElement.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
            throw new InvalidOperationException($"город не найден: {_city}");

        var r = results[0];
        return new GeoPlace(
            r.GetProperty("name").GetString()!,
            r.GetProperty("latitude").GetDouble(),
            r.GetProperty("longitude").GetDouble(),
            r.TryGetProperty("timezone", out var tz) ? tz.GetString() : null);
    }

    // --- разбор JSON: всё толерантно к отсутствующим и null значениям ---

    private static JsonElement Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    private static double Num(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : double.NaN;

    private static int Code(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i) ? i : -1;

    private static double NumAt(JsonElement arr, int i) =>
        arr.ValueKind == JsonValueKind.Array && i < arr.GetArrayLength() ? Num(arr[i]) : double.NaN;

    private static int CodeAt(JsonElement arr, int i) =>
        arr.ValueKind == JsonValueKind.Array && i < arr.GetArrayLength() ? Code(arr[i]) : -1;

    // --- met.no ---

    private static JsonElement Details(JsonElement entry, string block) => Prop(Prop(Prop(entry, "data"), block), "details");

    private static string? Symbol(JsonElement entry, string block)
    {
        var s = Prop(Prop(Prop(Prop(entry, "data"), block), "summary"), "symbol_code");
        return s.ValueKind == JsonValueKind.String ? s.GetString() : null;
    }

    /// <summary>Символ met.no (например "partlycloudy_day", "lightrainshowers_night") → код WMO; -1 — неизвестен.</summary>
    private static int MetCode(string? symbol)
    {
        if (symbol is null) return -1;
        int us = symbol.IndexOf('_');
        var s = us >= 0 ? symbol[..us] : symbol;
        if (s.Contains("thunder", StringComparison.Ordinal)) return 95;
        return s switch
        {
            "clearsky" => 0,
            "fair" => 1,
            "partlycloudy" => 2,
            "cloudy" => 3,
            "fog" => 45,
            "lightrain" => 61,
            "rain" => 63,
            "heavyrain" => 65,
            "lightsleet" or "sleet" or "heavysleet" or "sleetshowers" or "lightsleetshowers" or "heavysleetshowers" => 66,
            "lightrainshowers" => 80,
            "rainshowers" => 81,
            "heavyrainshowers" => 82,
            "lightsnow" => 71,
            "snow" => 73,
            "heavysnow" => 75,
            "lightsnowshowers" or "snowshowers" => 85,
            "heavysnowshowers" => 86,
            _ => -1,
        };
    }

    /// <summary>Сворачивание часовых/шестичасовых точек в один день met.no.</summary>
    private sealed class DayAcc
    {
        private double _min = double.PositiveInfinity, _max = double.NegativeInfinity;
        public double Precip = double.NaN;
        public int CodeDist = int.MaxValue;
        public string? Symbol;

        public void Fold(double t)
        {
            if (double.IsNaN(t)) return;
            if (t < _min) _min = t;
            if (t > _max) _max = t;
        }

        public void AddPrecip(double mm) => Precip = double.IsNaN(Precip) ? mm : Precip + mm;

        public double MinOrNaN() => double.IsFinite(_min) ? _min : double.NaN;

        public double MaxOrNaN() => double.IsFinite(_max) ? _max : double.NaN;
    }

    private sealed record GeoPlace(string Name, double Lat, double Lon, string? Timezone);
}
