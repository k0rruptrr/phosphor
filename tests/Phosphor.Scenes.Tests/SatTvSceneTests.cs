using System.Text.Json;
using Phosphor.Core;

namespace Phosphor.Scenes.Tests;

public sealed class SatTvSceneTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private const int Cols = 120, Rows = 40;
    private const float Dt = 1f / 60f;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new SatTvScene(), "sattv");

    [Fact]
    public void NoDataShowsSearchWithoutInventedChannels()
    {
        var s = Start(null);
        SceneContract.Run(s, 300);
        var g = SceneContract.Snap(s, Cols, Rows);
        Assert.True(AnyRowContains(g, "SEARCHING") || AnyRowContains(g, "ПОИСК"), "нет экрана поиска спутника");
        Assert.False(AnyRowContains(g, "PROGRAMME"), "без данных не должно быть таблицы каналов");
    }

    [Fact]
    public void TableShowsFrequencyFromData()
    {
        var s = Start(Set(Sat("astra1n", "Astra 1N", "19.2°E", 12)));
        var g = RunUntil(s, 4000, grid => AnyRowContains(grid, "11214.25"));
        Assert.NotNull(g);
    }

    [Fact]
    public void CreditsScrollUpOneRowPerStep()
    {
        var s = Start(Set(Sat("astra1n", "Astra 1N", "19.2°E", 12)));
        var g0 = RunUntil(s, 4000, grid => RowOf(grid, "PROGRAMME") >= 0);
        Assert.NotNull(g0);
        int y0 = RowOf(g0!, "PROGRAMME");

        // Один шаг титров = scrollSec (0.35 с) — ровно одна строка сетки вверх.
        s.Update(0.35f);
        var g1 = SceneContract.Snap(s, Cols, Rows);
        int y1 = RowOf(g1, "PROGRAMME");
        Assert.Equal(y0 - 1, y1);
    }

    [Fact]
    public void AfterTableNextSatelliteComesUp()
    {
        var s = Start(Set(
            Sat("astra1n", "Astra 1N", "19.2°E", 5),
            Sat("eutelsat", "Eutelsat 9B", "9.0°E", 5)));
        bool firstSeen = false;
        for (int i = 0; i < 3000; i++)
        {
            s.Update(0.1f);
            if (i % 5 != 0) continue;
            var g = SceneContract.Snap(s, Cols, Rows);
            if (AnyRowContains(g, "ASTRA 1N")) firstSeen = true;
            if (firstSeen && AnyRowContains(g, "EUTELSAT 9B")) return;
        }
        Assert.Fail("второй спутник не появился после таблицы первого");
    }

    [Theory]
    [InlineData("blinds")]
    [InlineData("blue")]
    [InlineData("gradient")]
    public void StyleIsDrawnOnBackground(string style)
    {
        var s = Start(Set(Sat("astra1n", "Astra 1N", "19.2°E", 5)), StyleJson(style));
        s.Update(Dt); // эфир начинается: контент ниже экрана, виден только фон
        var g = SceneContract.Snap(s, Cols, Rows);
        Assert.False(AnyRowContains(g, "CH"), "в первом кадре эфира текста быть не должно");
        Assert.NotEqual(default, AvgBg(g));
    }

    [Fact]
    public void ThreeStylesGiveDifferentBackgrounds()
    {
        var avgs = new[] { "blinds", "blue", "gradient" }.Select(st =>
        {
            var s = Start(Set(Sat("astra1n", "Astra 1N", "19.2°E", 5)), StyleJson(st));
            s.Update(Dt);
            return AvgBg(SceneContract.Snap(s, Cols, Rows));
        }).ToArray();

        for (int i = 0; i < avgs.Length; i++)
            for (int j = i + 1; j < avgs.Length; j++)
                Assert.True(Distance(avgs[i], avgs[j]) > 0.05f,
                    $"фоны стилей {i} и {j} почти одинаковые: {avgs[i]} vs {avgs[j]}");
    }

    [Fact]
    public void ScrollSecParamSetsStepLength()
    {
        // За 3 с: при scrollSec=0.35 строк уходит больше, чем при scrollSec=1.0.
        int Shift(string json)
        {
            var s = Start(Set(Sat("astra1n", "Astra 1N", "19.2°E", 12)), json);
            var g0 = RunUntil(s, 4000, grid => RowOf(grid, "PROGRAMME") >= 0);
            Assert.NotNull(g0);
            int y0 = RowOf(g0!, "PROGRAMME");
            for (int i = 0; i < 30; i++) s.Update(0.1f);
            return y0 - RowOf(SceneContract.Snap(s, Cols, Rows), "PROGRAMME");
        }

        int fast = Shift("{\"scrollSec\":0.35}");
        int slow = Shift("{\"scrollSec\":1.0}");
        Assert.True(fast >= 7, $"при 0.35 с за 3 с должно уйти ≥7 строк, ушло {fast}");
        Assert.True(slow <= 3, $"при 1.0 с за 3 с должно уйти ≤3 строк, ушло {slow}");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(200)]
    public void MaxChannelsLimitsChannelsPerEpisode(int max)
    {
        // 30 каналов: в эфир попадает не больше maxChannels; при 200 — все 30.
        var s = Start(Set(Sat("astra1n", "Astra 1N", "19.2°E", 30)), $"{{\"maxChannels\":{max}}}");
        // Считаем один эфир: от появления первой частоты до её исчезновения (следующий эфир — новое окно каналов).
        var seen = new HashSet<string>();
        bool started = false;
        for (int step = 0; step < 400; step++)
        {
            s.Update(0.35f); // один шаг титров = одна строка
            var g = SceneContract.Snap(s, Cols, Rows);
            bool any = false;
            for (int k = 0; k < 30; k++)
            {
                string freq = (11214.25 + k * 14.75).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                if (AnyRowContains(g, freq))
                {
                    any = true;
                    seen.Add(freq);
                }
            }
            if (any) started = true;
            else if (started) break;
        }
        Assert.True(started, "каналы так и не появились");
        Assert.True(seen.Count <= max, $"каналов в эфире {seen.Count}, при maxChannels={max}");
        if (max >= 30) Assert.Equal(30, seen.Count);
    }

    [Fact]
    public void PixelFontCoversSatelliteNameAlphabet()
    {
        foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.°-/")
            Assert.True(PixelFont.Has(c), $"нет глифа '{c}' в PixelFont");
        Assert.True(PixelFont.Has('A') && PixelFont.Bits(' ').All(b => b == 0), "пробел должен быть пустым");
    }

    // ---- помощники ----

    private static SatTvSetSnapshot Set(params SatTvSatellite[] sats) => new(sats, T0, false);

    private static SatTvSatellite Sat(string id, string name, string position, int channels) =>
        new(id, name, position, Channels(channels), T0, false);

    private static IReadOnlyList<SatTvChannel> Channels(int n)
    {
        string[] names = { "SCREENSPORT", "RTL PLUS", "SCANSAT TV3", "EUROSPORT", "LIFESTYLE" };
        var list = new List<SatTvChannel>();
        for (int i = 0; i < n; i++)
            list.Add(new SatTvChannel(names[i % names.Length] + (i >= names.Length ? $" {i}" : ""),
                11214.25 + i * 14.75, i % 2 == 0 ? 'H' : 'V', 27500, "3/4", "DVB-S", "Germany", i % 3 != 0));
        return list;
    }

    private static string StyleJson(string style) => $"{{\"style\":\"{style}\"}}";

    private static SatTvScene Start(SatTvSetSnapshot? set, string? paramsJson = null, int seed = 1)
    {
        var options = paramsJson is null
            ? new SceneOptions()
            : new SceneOptions { Params = JsonDocument.Parse(paramsJson).RootElement };
        var s = new SatTvScene();
        s.Init(Cols, Rows, new Random(seed), options);
        if (set is not null) s.OnData("sattv", set);
        return s;
    }

    /// <summary>Крутит сцену шагами 0.1 с, пока условие не выполнится на отрисованной сетке.</summary>
    private static CharGrid? RunUntil(SatTvScene s, int maxFrames, Func<CharGrid, bool> cond)
    {
        for (int i = 0; i < maxFrames; i++)
        {
            s.Update(Dt);
            var g = SceneContract.Snap(s, Cols, Rows);
            if (cond(g)) return g;
        }
        return null;
    }

    private static string RowText(CharGrid g, int y)
    {
        var chars = new char[g.Cols];
        for (int x = 0; x < g.Cols; x++) chars[x] = g[x, y].Ch;
        return new string(chars);
    }

    private static bool AnyRowContains(CharGrid g, string text)
    {
        for (int y = 0; y < g.Rows; y++)
            if (RowText(g, y).Contains(text, StringComparison.Ordinal)) return true;
        return false;
    }

    private static int RowOf(CharGrid g, string text)
    {
        for (int y = 0; y < g.Rows; y++)
            if (RowText(g, y).Contains(text, StringComparison.Ordinal)) return y;
        return -1;
    }

    /// <summary>Средний цвет фона: у полублоков — среднее двух половин, у остальных клеток — Bg.</summary>
    private static (float R, float G, float B) AvgBg(CharGrid g)
    {
        float r = 0, gg = 0, b = 0;
        foreach (var c in g.Cells)
        {
            if (c.Ch == '▀')
            {
                r += (c.Fg.R + c.Bg.R) / 2f; gg += (c.Fg.G + c.Bg.G) / 2f; b += (c.Fg.B + c.Bg.B) / 2f;
            }
            else
            {
                r += c.Bg.R; gg += c.Bg.G; b += c.Bg.B;
            }
        }
        float n = g.Cells.Length * 255f;
        return (r / n, gg / n, b / n);
    }

    private static float Distance((float R, float G, float B) a, (float R, float G, float B) b) =>
        MathF.Sqrt((a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B));
}
