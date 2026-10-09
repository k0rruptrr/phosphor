using System.Text;
using System.Text.RegularExpressions;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class WeatherBoardSceneTests
{
    private readonly ITestOutputHelper _out;

    public WeatherBoardSceneTests(ITestOutputHelper output) => _out = output;

    private static readonly Rgba Cold = Rgba.Hex(0xA8D8FF);
    private static readonly ClockSnapshot Clock = new(new DateTime(2026, 10, 9, 4, 20, 0));
    // Строка дня: «ПТ 09» без точки за числом (в шапке стоит дата «ПТ 09.10»).
    private static readonly Regex DayRow = new(@"(ПН|ВТ|СР|ЧТ|ПТ|СБ|ВС) \d\d(?![\d.])", RegexOptions.Compiled);

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new WeatherBoardScene(), "weatherboard");

    [Fact]
    public void ShowsSyntheticForecast()
    {
        var s = Make(41);
        Feed(s, Moscow(7.4));
        SceneContract.Run(s, 300); // 5 с
        var g = SceneContract.Snap(s, 120, 36);
        string text = Text(g, 0, g.Rows);

        Assert.Contains("МОСКВА", text);
        Assert.Contains("ПАСМУРНО", text);
        Assert.Contains("+7°", text);
        Assert.Contains("ПТ", text);
        Assert.Contains("+12°", text);   // день 1 (макс.)
        Assert.Contains("ГРОЗА", text);  // день 6
        Assert.Contains("ЧТ 15", text);  // день 7
        int dayRows = text.Split('\n').Count(l => DayRow.IsMatch(l));
        Assert.True(dayRows >= 7, $"строк дней: {dayRows}");

        // Анимация закончилась: через секунду табло (кроме тикера) не меняется.
        string before = Text(g, 0, g.Rows - 3);
        SceneContract.Run(s, 60);
        string after = Text(SceneContract.Snap(s, 120, 36), 0, g.Rows - 3);
        Assert.Equal(before, after);

        _out.WriteLine(SceneContract.Dump(g, "weatherboard"));
    }

    [Fact]
    public void ValuesFlipInFlightWithin300Ms()
    {
        var early = Make(41);
        Feed(early, Moscow(7.4));
        SceneContract.Run(early, 18); // 0.3 с
        var eg = SceneContract.Snap(early, 120, 36);

        var late = Make(41);
        Feed(late, Moscow(7.4));
        SceneContract.Run(late, 300);
        var lg = SceneContract.Snap(late, 120, 36);

        // Сравниваем только строки табло (без тикера): на 0.3 с часть пластинок ещё в полёте.
        int diff = 0;
        for (int y = 0; y < lg.Rows - 3; y++)
            for (int x = 0; x < lg.Cols; x++)
                if (eg[x, y].Ch != lg[x, y].Ch) diff++;
        Assert.True(diff >= 5, $"через 0.3 с почти все пластинки уже на целевых символах (расхождений: {diff})");
    }

    [Fact]
    public void NegativeTemperatureIsColdBlue()
    {
        var s = Make(42);
        Feed(s, Moscow(-3.2));
        SceneContract.Run(s, 300);
        var g = SceneContract.Snap(s, 120, 36);

        var fg = FirstFg(g, "-3°");
        Assert.NotNull(fg);
        Assert.Equal(Cold, fg.Value);
    }

    [Fact]
    public void FromCacheShowsKeshInTicker()
    {
        var cached = Make(43);
        Feed(cached, Moscow(7.4, fromCache: true));
        SceneContract.Run(cached, 60);
        Assert.Contains("КЕШ", Text(SceneContract.Snap(cached, 120, 36), 0, 36));

        var fresh = Make(43);
        Feed(fresh, Moscow(7.4, fromCache: false));
        SceneContract.Run(fresh, 60);
        Assert.DoesNotContain("КЕШ", Text(SceneContract.Snap(fresh, 120, 36), 0, 36));
    }

    [Fact]
    public void DumpAfterFiveSeconds()
    {
        var s = Make(44);
        Feed(s, Moscow(7.4));
        SceneContract.Run(s, 300);
        var g = SceneContract.Snap(s, 120, 36);
        _out.WriteLine(SceneContract.Dump(g, "weatherboard-layout"));
    }

    [Fact]
    public void NoData_ShowsWaitingInTicker()
    {
        var s = Make(45);
        ((IDataScene)s).OnData("clock", Clock);

        string text = Over10s(s);
        Assert.Contains("ОЖИДАНИЕ ДАННЫХ", text);
        Assert.DoesNotContain("НЕТ СВЯЗИ", text);
    }

    [Fact]
    public void StatusError_ShowsNoLinkAndReason()
    {
        var s = Make(46);
        var d = (IDataScene)s;
        d.OnData("clock", Clock);
        d.OnData("weather.status", new SourceStatusSnapshot("BLOCKED", "WSAEACCES 10013",
            new DateTimeOffset(new DateTime(2026, 10, 9, 4, 18, 0, DateTimeKind.Local))));

        string text = Over10s(s);
        Assert.Contains("НЕТ СВЯЗИ · OPEN-METEO", text);
        Assert.Contains("СЕТЬ ЗАБЛОКИРОВАНА (FIREWALL?)", text);
        Assert.DoesNotContain("ОЖИДАНИЕ ДАННЫХ", text);
    }

    [Fact]
    public void StaleCache_HidesPastDays_AndShowsAge()
    {
        var s = Make(47);
        var d = (IDataScene)s;
        d.OnData("clock", Clock);
        d.OnData("weather", Stale());
        SceneContract.Run(s, 300);

        var g = SceneContract.Snap(s, 120, 36);
        string table = Text(g, 0, 36);
        Assert.DoesNotContain("ЧТ 08", table);   // вчерашний день из старого кеша не показываем
        Assert.Contains("ПТ 09", table);
        _out.WriteLine(SceneContract.Dump(g, "weatherboard-stale"));
        int dayRows = table.Split('\n').Count(l => DayRow.IsMatch(l));
        Assert.Equal(6, dayRows);                // 09..14, хвост таблицы пустой

        Assert.Contains("КЕШ · 5 МИН НАЗАД", Over10s(s));
    }

    [Fact]
    public void DuringWave_NoPlateShowsSpaceForNonSpaceTarget()
    {
        var s = Make(48);
        Feed(s, Moscow(7.4));
        SceneContract.Run(s, 300);
        var settled = SceneContract.Snap(s, 120, 36);
        const int tickerY = 36 - 1 - 2;   // строка тикера бежит — её не сравниваем

        int violations = 0, changed = 0;
        for (int f = 0; f < 6000; f++)    // 100 с: первая волна приходит в 45–90 с
        {
            s.Update(SceneContract.Dt);
            var g = SceneContract.Snap(s, 120, 36);
            for (int y = 0; y < 36; y++)
            {
                if (y == tickerY) continue;
                for (int x = 0; x < 120; x++)
                {
                    char target = settled[x, y].Ch;
                    if (g[x, y].Ch != target) changed++;
                    if (target != ' ' && g[x, y].Ch == ' ') violations++;
                }
            }
        }

        Assert.True(changed > 0, "волна не прошла — проверка пустая");
        Assert.Equal(0, violations);
    }

    /// <summary>Текст табло за ~10 с: тикер бежит по кругу, поэтому склеиваем кадры.</summary>
    private static string Over10s(IScene s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 40; i++)
        {
            SceneContract.Run(s, 15);
            sb.Append(Text(SceneContract.Snap(s, 120, 36), 0, 36));
        }
        return sb.ToString();
    }

    /// <summary>Кеш вчерашнего дня: 7 дней, начиная с 08.10, снимок взят в 04:15 локального времени (за 5 минут до часов сцены).</summary>
    private static WeatherSnapshot Stale()
    {
        var start = new DateOnly(2026, 10, 8);
        var days = new List<WeatherDay>();
        for (int i = 0; i < 7; i++) days.Add(new WeatherDay(start.AddDays(i), 3, 10, 0, 3));
        return new WeatherSnapshot("Москва", 55.75, 37.62, new WeatherNow(7.4, 5.4, 45, 3), days,
            new DateTimeOffset(new DateTime(2026, 10, 9, 4, 15, 0, DateTimeKind.Local)), FromCache: true);
    }

    private static IScene Make(int seed) =>
        SceneContract.Make(() => new WeatherBoardScene(), 120, 36, seed);

    private static void Feed(IScene s, WeatherSnapshot w)
    {
        var d = (IDataScene)s;
        d.OnData("clock", Clock);
        d.OnData("weather", w);
    }

    private static WeatherSnapshot Moscow(double temp, bool fromCache = false)
    {
        var start = new DateOnly(2026, 10, 9);
        // (мин, макс, осадки, код) на 7 дней
        var raw = new (double Min, double Max, double Precip, int Code)[]
        {
            (3.2, 12.4, 0.0, 3),
            (1.1, 8.0, 2.5, 61),
            (-2.6, 3.1, 0.0, 71),
            (-0.4, 5.7, 0.2, 0),
            (4.0, 10.9, 1.1, 2),
            (6.3, 14.2, 0.0, 95),
            (2.2, 7.7, 4.4, 45),
        };
        var days = new List<WeatherDay>();
        for (int i = 0; i < raw.Length; i++)
            days.Add(new WeatherDay(start.AddDays(i), raw[i].Min, raw[i].Max, raw[i].Precip, raw[i].Code));

        return new WeatherSnapshot(
            City: "Москва", Lat: 55.75, Lon: 37.62,
            Now: new WeatherNow(TempC: temp, WindKmh: 5.4, WindDirDeg: 45, Code: 3),
            Days: days,
            FetchedAt: new DateTimeOffset(2026, 10, 9, 4, 15, 0, TimeSpan.Zero),
            FromCache: fromCache);
    }

    private static string Text(CharGrid g, int fromY, int toY)
    {
        var sb = new StringBuilder();
        for (int y = fromY; y < toY; y++)
        {
            for (int x = 0; x < g.Cols; x++) sb.Append(g[x, y].Ch);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Цвет символа (первого) вхождения needle в сетке; null если не найдено.</summary>
    private static Rgba? FirstFg(CharGrid g, string needle)
    {
        for (int y = 0; y < g.Rows; y++)
        {
            var row = new StringBuilder();
            for (int x = 0; x < g.Cols; x++) row.Append(g[x, y].Ch);
            int i = row.ToString().IndexOf(needle, StringComparison.Ordinal);
            if (i >= 0) return g[i + 1, y].Fg;
        }
        return null;
    }
}
