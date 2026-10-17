using System.Globalization;
using System.Text.RegularExpressions;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Data.Tests;

public sealed class WeatherSourceTests
{
    // Коды WMO из спецификации Open-Meteo
    private static readonly int[] ValidWmo =
    {
        0, 1, 2, 3, 45, 48, 51, 53, 55, 56, 57, 61, 63, 65, 66, 67,
        71, 73, 75, 77, 80, 81, 82, 85, 86, 95, 96, 99,
    };

    private static FakeHttp MoscowHttp() => new FakeHttp()
        .OnFile("geocoding-api.open-meteo.com", "weather-geo-moscow.json")
        .OnFile("api.open-meteo.com/v1/forecast", "weather-forecast-moscow.json");

    [Fact]
    public async Task Moscow_ParsesSnapshot()
    {
        var fake = MoscowHttp();
        var snap = Assert.IsType<WeatherSnapshot>(
            await new WeatherSource("Москва").FetchAsync(fake.Client(), CancellationToken.None));

        Assert.Contains("Москва", snap.City);
        Assert.InRange(snap.Lat, 55.70, 55.80);
        Assert.InRange(snap.Lon, 37.55, 37.70);
        Assert.False(snap.FromCache);

        Assert.Equal(7, snap.Days.Count);
        for (int i = 1; i < snap.Days.Count; i++)
            Assert.Equal(snap.Days[i - 1].Date.AddDays(1), snap.Days[i].Date);

        foreach (var d in snap.Days)
        {
            Assert.True(d.MinC <= d.MaxC, $"{d.Date}: min {d.MinC} > max {d.MaxC}");
            Assert.Contains(d.Code, ValidWmo);
        }

        Assert.Contains(snap.Now.Code, ValidWmo);
        Assert.InRange(snap.Now.TempC, -60, 60);

        // Значения из сохранённой фикстуры
        Assert.Equal(7.4, snap.Now.TempC);
        Assert.Equal(DateOnly.Parse("2026-10-09", CultureInfo.InvariantCulture), snap.Days[0].Date);
        Assert.Equal(15.9, snap.Days[0].MaxC);
    }

    [Fact]
    public async Task SecondFetch_DoesNotGeocodeAgain()
    {
        var fake = MoscowHttp();
        var src = new WeatherSource("Москва");
        using var http = fake.Client();

        await src.FetchAsync(http, CancellationToken.None);
        await src.FetchAsync(http, CancellationToken.None);

        Assert.Equal(1, fake.Requests.Count(r => r.Contains("geocoding-api")));
        Assert.Equal(2, fake.Requests.Count(r => r.Contains("api.open-meteo.com/v1/forecast")));
    }

    [Fact]
    public async Task EmptyGeocoding_ThrowsCityNotFound()
    {
        var fake = new FakeHttp().OnFile("geocoding-api.open-meteo.com", "weather-geo-empty.json");
        var src = new WeatherSource("zzzzqqqq");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => src.FetchAsync(fake.Client(), CancellationToken.None));
        Assert.Contains("город не найден", ex.Message);
    }

    [Fact]
    public async Task ForecastUrl_UsesDotDecimalSeparator_EvenInRussianCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            var fake = MoscowHttp();
            await new WeatherSource("Москва").FetchAsync(fake.Client(), CancellationToken.None);

            var forecast = fake.Requests.Single(r => r.Contains("api.open-meteo.com/v1/forecast"));
            Assert.Contains("latitude=55.75204&longitude=37.61781", forecast);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static WeatherSnapshot CachedMoscow(double lat, double lon, string? query = "Москва") =>
        new("Москва", lat, lon, new WeatherNow(1, 1, 1, 0), Array.Empty<WeatherDay>(), DateTimeOffset.Now, true,
            "open-meteo", query);

    [Fact]
    public async Task ConfigCoords_SkipGeocoder()
    {
        var fake = new FakeHttp().OnFile("api.open-meteo.com/v1/forecast", "weather-forecast-moscow.json");
        var snap = Assert.IsType<WeatherSnapshot>(
            await new WeatherSource("Москва", 55.75204, 37.61781).FetchAsync(fake.Client(), CancellationToken.None));

        Assert.DoesNotContain(fake.Requests, r => r.Contains("geocoding-api"));
        Assert.Contains("latitude=55.75204&longitude=37.61781", fake.Requests.Single());
        Assert.Equal("open-meteo", snap.Source);
        Assert.Equal("Москва", snap.Query);
    }

    [Fact]
    public async Task GeocoderDown_UsesCachedCoords()
    {
        // Геокодер не отвечает (404 в фейке), прогноз доступен: координаты берутся из кеша погоды.
        var fake = new FakeHttp().OnFile("api.open-meteo.com/v1/forecast", "weather-forecast-moscow.json");
        var src = new WeatherSource("Москва");
        src.Restore(CachedMoscow(55.75, 37.62));

        var snap = Assert.IsType<WeatherSnapshot>(await src.FetchAsync(fake.Client(), CancellationToken.None));

        Assert.Contains("latitude=55.75&longitude=37.62", fake.Requests.Single(r => r.Contains("api.open-meteo.com/v1/forecast")));
        Assert.Equal(55.75, snap.Lat);
        Assert.Equal(7, snap.Days.Count);
    }

    [Fact]
    public async Task GeocoderDown_NoCache_Throws()
    {
        await Assert.ThrowsAsync<HttpRequestException>(
            () => new WeatherSource("Москва").FetchAsync(new FakeHttp().Client(), CancellationToken.None));
    }

    [Fact]
    public async Task CacheOfOtherCity_NotUsedForCoords()
    {
        var src = new WeatherSource("Берлин");
        src.Restore(CachedMoscow(55.75, 37.62));   // Query кеша — «Москва»

        await Assert.ThrowsAsync<HttpRequestException>(
            () => src.FetchAsync(new FakeHttp().Client(), CancellationToken.None));
    }

    [Fact]
    public async Task OpenMeteoDown_FallsBackToMetNo()
    {
        // Все даты фикстуры (с 2026-10-09) сдвигаем так, чтобы первый день стал сегодняшним: источник отбрасывает прошедшие дни.
        var shift = DateTime.UtcNow.Date - new DateTime(2026, 10, 9);
        var body = Regex.Replace(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "weather-metno-moscow.json")),
            @"\d{4}-\d{2}-\d{2}(?=T)",
            m => DateTime.ParseExact(m.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture).Add(shift)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var fake = new FakeHttp()
            .OnFile("geocoding-api.open-meteo.com", "weather-geo-moscow.json")
            .On("api.met.no/weatherapi/locationforecast/2.0/compact", body);

        var snap = Assert.IsType<WeatherSnapshot>(
            await new WeatherSource("Москва").FetchAsync(fake.Client(), CancellationToken.None));

        Assert.Equal("met.no", snap.Source);
        Assert.Equal("Москва", snap.Query);
        Assert.Equal(7, snap.Days.Count);
        // Первая точка фикстуры: 15.4 °C, облачно (cloudy → WMO 3), ветер 3.3 м/с = 11.88 км/ч
        Assert.Equal(15.4, snap.Now.TempC);
        Assert.Equal(3, snap.Now.Code);
        Assert.InRange(snap.Now.WindKmh, 11.8, 12.0);
        foreach (var d in snap.Days)
        {
            Assert.Contains(d.Code, ValidWmo.Concat(new[] { -1 }));
            if (!double.IsNaN(d.MinC) && !double.IsNaN(d.MaxC)) Assert.True(d.MinC <= d.MaxC, $"{d.Date}");
        }
        Assert.Contains(fake.Requests, r => r.Contains("api.met.no"));
    }

    [Fact]
    public async Task BothDown_ThrowsOpenMeteoError()
    {
        var fake = new FakeHttp().OnFile("geocoding-api.open-meteo.com", "weather-geo-moscow.json");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => new WeatherSource("Москва").FetchAsync(fake.Client(), CancellationToken.None));
        Assert.Contains(fake.Requests, r => r.Contains("api.met.no"));
    }

    [Fact]
    public async Task NullsInOpenMeteo_DoNotBreakFetch()
    {
        const string json = """
        {"current":{"temperature_2m":null,"weather_code":3,"wind_speed_10m":5.4,"wind_direction_10m":45},
         "daily":{"time":["2026-10-09","2026-10-10"],"weather_code":[3,null],
                  "temperature_2m_max":[12.4,null],"temperature_2m_min":[3.2,1.0],"precipitation_sum":[null,0.5]}}
        """;
        var fake = new FakeHttp().On("api.open-meteo.com/v1/forecast", json);

        var snap = Assert.IsType<WeatherSnapshot>(
            await new WeatherSource("Москва", 55.75, 37.62).FetchAsync(fake.Client(), CancellationToken.None));

        Assert.True(double.IsNaN(snap.Now.TempC));
        Assert.Equal(3, snap.Now.Code);
        Assert.Equal(2, snap.Days.Count);
        Assert.Equal(12.4, snap.Days[0].MaxC);
        Assert.True(double.IsNaN(snap.Days[0].PrecipMm));
        Assert.Equal(1.0, snap.Days[1].MinC);
        Assert.True(double.IsNaN(snap.Days[1].MaxC));
        Assert.Equal(-1, snap.Days[1].Code);
    }

    [Fact]
    public async Task NullsInMetNo_DoNotBreakFetch()
    {
        var t = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        const string template = """
        {"properties":{"timeseries":[
          {"time":"TIME","data":{"instant":{"details":{"air_temperature":null,"wind_speed":3.0,"wind_from_direction":null}},
            "next_1_hours":{"summary":{"symbol_code":"cloudy"},"details":{"precipitation_amount":null}}}}]}}
        """;
        string json = template.Replace("TIME", t, StringComparison.Ordinal);
        var fake = new FakeHttp()
            .OnFile("geocoding-api.open-meteo.com", "weather-geo-moscow.json")
            .On("api.met.no/weatherapi/locationforecast/2.0/compact", json);

        // Open-Meteo недоступен (404), поэтому разбирается met.no
        var snap = Assert.IsType<WeatherSnapshot>(
            await new WeatherSource("Москва").FetchAsync(fake.Client(), CancellationToken.None));

        Assert.Equal("met.no", snap.Source);
        Assert.True(double.IsNaN(snap.Now.TempC));
        Assert.True(double.IsNaN(snap.Now.WindDirDeg));
        Assert.InRange(snap.Now.WindKmh, 10.7, 10.9);   // 3.0 м/с
        Assert.Equal(3, snap.Now.Code);
        Assert.Single(snap.Days);
        Assert.True(double.IsNaN(snap.Days[0].MaxC));
        Assert.True(double.IsNaN(snap.Days[0].PrecipMm));
    }
}

[Trait("Category", "Live")]
public sealed class WeatherSourceLiveTests
{
    private readonly ITestOutputHelper _out;

    public WeatherSourceLiveTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("Москва")]
    [InlineData("Berlin")]
    public async Task Live_FetchesRealForecast(string city)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var snap = Assert.IsType<WeatherSnapshot>(
            await new WeatherSource(city).FetchAsync(http, CancellationToken.None));

        _out.WriteLine($"Город: {snap.City} ({snap.Lat:F2}, {snap.Lon:F2})");
        _out.WriteLine($"Сейчас: {snap.Now.TempC:F1} °C, ветер {snap.Now.WindKmh:F1} км/ч, WMO {snap.Now.Code}");
        foreach (var d in snap.Days)
            _out.WriteLine($"  {d.Date:dd.MM ddd}  {d.MinC,5:F1} .. {d.MaxC,5:F1} °C  осадки {d.PrecipMm,5:F1} мм  WMO {d.Code}");

        Assert.Equal(7, snap.Days.Count);
    }
}
