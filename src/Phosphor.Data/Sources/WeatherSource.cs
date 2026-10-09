using System.Globalization;
using System.Text.Json;
using Phosphor.Core;

namespace Phosphor.Data;

/// <summary>
/// Погода Open-Meteo для одного города: геокодинг (один раз, пока не получился) и прогноз на 7 дней.
/// Ключ API не нужен. Интервал 30 минут — далеко внутри лимитов Open-Meteo.
/// </summary>
public sealed class WeatherSource : IDataSource
{
    private readonly string _city;
    private GeoPlace? _place;

    public WeatherSource(string city)
    {
        _city = city;
    }

    public string Key => "weather";
    public string Name => "weather";
    public TimeSpan Interval => TimeSpan.FromMinutes(30);

    public async Task<DataSnapshot?> FetchAsync(HttpClient http, CancellationToken ct)
    {
        if (_place is null)
            _place = await GeocodeAsync(http, ct).ConfigureAwait(false);
        var place = _place;

        var url = FormattableString.Invariant(
            $"https://api.open-meteo.com/v1/forecast?latitude={place.Lat}&longitude={place.Lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_sum&timezone=auto&forecast_days=7");

        var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var cur = root.GetProperty("current");
        var now = new WeatherNow(
            cur.GetProperty("temperature_2m").GetDouble(),
            cur.GetProperty("wind_speed_10m").GetDouble(),
            cur.GetProperty("wind_direction_10m").GetDouble(),
            cur.GetProperty("weather_code").GetInt32());

        var daily = root.GetProperty("daily");
        var times = daily.GetProperty("time");
        var codes = daily.GetProperty("weather_code");
        var maxs = daily.GetProperty("temperature_2m_max");
        var mins = daily.GetProperty("temperature_2m_min");
        var precs = daily.GetProperty("precipitation_sum");

        var count = times.GetArrayLength();
        var days = new List<WeatherDay>(count);
        for (int i = 0; i < count; i++)
        {
            days.Add(new WeatherDay(
                DateOnly.ParseExact(times[i].GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                mins[i].GetDouble(),
                maxs[i].GetDouble(),
                precs[i].GetDouble(),
                codes[i].GetInt32()));
        }

        return new WeatherSnapshot(place.Name, place.Lat, place.Lon, now, days, DateTimeOffset.Now, false);
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

    private sealed record GeoPlace(string Name, double Lat, double Lon, string? Timezone);
}
