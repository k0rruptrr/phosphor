using System.Globalization;
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
