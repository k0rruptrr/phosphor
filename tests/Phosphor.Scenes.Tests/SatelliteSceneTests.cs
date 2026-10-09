using System.Text;
using Phosphor.Core;

namespace Phosphor.Scenes.Tests;

public sealed class SatelliteSceneTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new SatelliteScene(), "satellite");

    [Fact]
    public void NoDataShowsStubWithoutExceptions()
    {
        var s = SceneContract.Make(() => new SatelliteScene(), 120, 40, 1);
        SceneContract.Run(s, 200);
        var g = SceneContract.Snap(s, 120, 40);
        Assert.True(AnyRowContains(g, "НЕТ СИГНАЛА"), "в заглушке нет подписи «НЕТ СИГНАЛА»");
    }

    [Fact]
    public void TwoCloudsOverCommonBackgroundAreTrackedWithSpeed()
    {
        // Два пятна на общем фоне облачности, за 3 часа каждое сдвинуто на 3° по долготе.
        // Ожидаемая скорость: 3·111·cos(широты) / 3 ч → 104 км/ч (широта 20°) и 109 км/ч (широта −10°).
        var f1 = CloudFrame(T0, (-60, 20), (60, -10));
        var f2 = CloudFrame(T0 + TimeSpan.FromHours(3), (-57, 20), (63, -10));
        var clouds = new SatClouds();
        clouds.Compute(f2, f1, EquirectArea(double.NaN, double.NaN, null));

        Assert.True(clouds.Count >= 2, $"найдено облаков: {clouds.Count}");
        var expected = new[] { 3 * 111.0 * Math.Cos(20 * Math.PI / 180) / 3, 3 * 111.0 * Math.Cos(10 * Math.PI / 180) / 3 };
        foreach (double e in expected)
        {
            bool found = false;
            for (int i = 0; i < clouds.Count; i++)
                if (Math.Abs(clouds.Items[i].SpeedKmh - e) <= e * 0.2) found = true;
            Assert.True(found, $"нет облака со скоростью {e:F0} км/ч ±20%: {string.Join(", ", Enumerable.Range(0, clouds.Count).Select(i => clouds.Items[i].Label))}");
        }
        for (int i = 0; i < clouds.Count; i++) Assert.Equal('→', clouds.Items[i].Arrow);
    }

    [Fact]
    public void BrightSpotAtEdgeIsIgnoredAndCentreSpotFound()
    {
        // Пятно у западного края (окно 33×33 не помещается) не берём; центральное — находим.
        var f1 = CloudFrame(T0, (-175, 0), (0, 0));
        var f2 = CloudFrame(T0 + TimeSpan.FromHours(3), (-175, 0), (3, 0));
        var clouds = new SatClouds();
        clouds.Compute(f2, f1, EquirectArea(double.NaN, double.NaN, null));

        Assert.True(clouds.Count >= 1, "центральное пятно не найдено");
        for (int i = 0; i < clouds.Count; i++)
        {
            Assert.True(clouds.Items[i].U0 * 360 >= 20 && clouds.Items[i].U1 * 360 <= 340,
                $"облако у края: {clouds.Items[i].Label}");
        }
        Assert.Contains(Enumerable.Range(0, clouds.Count), i => Math.Abs(clouds.Items[i].SpeedKmh - 3 * 111.0 * Math.Cos(0) / 3) <= 3 * 111.0 / 3 * 0.2);
    }

    [Fact]
    public void FastCloudIsLabelledWithoutSpeed()
    {
        // 5° за час — около 520 км/ч: выше порога 200 км/ч, число не выводится.
        var f1 = CloudFrame(T0, (-60, 20));
        var f2 = CloudFrame(T0 + TimeSpan.FromHours(1), (-55, 20));
        var clouds = new SatClouds();
        clouds.Compute(f2, f1, EquirectArea(double.NaN, double.NaN, null));

        Assert.True(clouds.Count >= 1, "облако не найдено");
        Assert.Contains("ТРЕК", clouds.Items[0].Label);
        Assert.DoesNotContain("КМ/Ч", clouds.Items[0].Label);
        Assert.True(double.IsNaN(clouds.Items[0].SpeedKmh));
    }

    [Fact]
    public void DarkFeedIsSkippedWhenLightFeedExists()
    {
        var dark = new SatelliteFeed("himawari", "DARK-FEED", DiskArea(), new[] { Gray(T0, 200, 200, 0) }, T0, false);
        var light = new SatelliteFeed("meteosat", "LIGHT-FEED", DiskArea(), new[] { Disk(T0, 200, 200) }, T0, false);
        var s = new SatelliteScene();
        s.OnData("satellite", new SatelliteSetSnapshot(new List<SatelliteFeed> { dark, light }, T0, false));

        var seen = new HashSet<string>();
        for (int seed = 1; seed <= 12; seed++)
        {
            s.Init(120, 40, new Random(seed), new SceneOptions());
            var g = SceneContract.Snap(s, 120, 40);
            if (RowText(g, 1).Contains("LIGHT-FEED")) seen.Add("LIGHT-FEED");
            if (RowText(g, 1).Contains("DARK-FEED")) seen.Add("DARK-FEED");
        }
        Assert.Equal(new[] { "LIGHT-FEED" }, seen.OrderBy(x => x));
    }

    [Fact]
    public void DarkFeedIsShownWhenItIsTheOnlyOne()
    {
        var dark = new SatelliteFeed("himawari", "DARK-FEED", DiskArea(), new[] { Gray(T0, 200, 200, 0) }, T0, false);
        var s = new SatelliteScene();
        s.Init(120, 40, new Random(1), new SceneOptions());
        s.OnData("satellite", new SatelliteSetSnapshot(new List<SatelliteFeed> { dark }, T0, false));
        SceneContract.Run(s, 10);
        Assert.True(AnyRowContains(SceneContract.Snap(s, 120, 40), "DARK-FEED"));
    }

    [Fact]
    public void CityMarkerLandsOnEquirectCell()
    {
        // Холст 120×80 пикселей, кадр 360×180 вписан по ширине: s = 1/3, поля сверху y0 = 10 пикселей.
        // lon −28.5 → x = 151.5/360·360·(1/3) = 50.5 → колонка 50; lat 40 → y = 10 + 50·(1/3) = 26.67 → строка 13.
        const double lat = 40, lon = -28.5;
        var area = new SatelliteArea(SatelliteProjection.Equirect, -180, -90, 180, 90, 0, 1, lat, lon, "BERLIN");
        var frames = new[] { Gray(T0, 360, 180, 80), Gray(T0 + TimeSpan.FromHours(1), 360, 180, 120), Gray(T0 + TimeSpan.FromHours(2), 360, 180, 160) };
        var s = new SatelliteScene();
        s.Init(120, 40, new Random(1), new SceneOptions());
        s.OnData("satellite", new SatelliteSetSnapshot(new[] { new SatelliteFeed("viirs", "VIIRS", area, frames, T0, false) }, T0, false));
        SceneContract.Run(s, 240);
        var g = SceneContract.Snap(s, 120, 40);
        Assert.Equal('┼', g[50, 13].Ch);
    }

    [Fact]
    public void FullDiskIsCenteredWithBlackCorners()
    {
        var area = new SatelliteArea(SatelliteProjection.FullDisk, -180, -90, 180, 90, 0, 1, double.NaN, double.NaN, null);
        var s = new SatelliteScene();
        s.Init(120, 40, new Random(2), new SceneOptions());
        s.OnData("satellite", new SatelliteSetSnapshot(new[] { new SatelliteFeed("himawari", "HIMAWARI", area, new[] { Disk(T0, 200, 200) }, T0, false) }, T0, false));
        SceneContract.Run(s, 240);
        var g = SceneContract.Snap(s, 120, 40);

        Assert.Equal(Rgba.Black, g[0, 0].Bg);
        Assert.Equal(Rgba.Black, g[119, 39].Bg);
        Assert.True(Luma(g[60, 20].Bg) > 50, "центр диска не яркий");
    }

    [Fact]
    public void FeedChangesBetweenInits()
    {
        var eq = EquirectArea(double.NaN, double.NaN, null);
        var disk = new SatelliteArea(SatelliteProjection.FullDisk, -180, -90, 180, 90, 0, 1, double.NaN, double.NaN, null);
        var feeds = new List<SatelliteFeed>
        {
            new("meteosat", "FEED-A", eq, new[] { Gray(T0, 64, 32, 100) }, T0, false),
            new("himawari", "FEED-B", disk, new[] { Disk(T0, 64, 64) }, T0, false),
            new("viirs", "FEED-C", eq, new[] { Gray(T0, 64, 32, 60) }, T0, false),
        };
        var s = new SatelliteScene();
        s.OnData("satellite", new SatelliteSetSnapshot(feeds, T0, false));

        var seen = new HashSet<string>();
        for (int seed = 1; seed <= 10; seed++)
        {
            s.Init(120, 40, new Random(seed), new SceneOptions());
            var g = SceneContract.Snap(s, 120, 40);
            string row = RowText(g, 1);
            foreach (var label in new[] { "FEED-A", "FEED-B", "FEED-C" })
                if (row.Contains(label)) seen.Add(label);
        }
        Assert.True(seen.Count >= 2, $"ленты не меняются между Init: {string.Join(",", seen)}");
    }

    [Fact]
    public void TimelapseChangesPictureOverTime()
    {
        var s = new SatelliteScene();
        s.Init(120, 40, new Random(3), new SceneOptions());
        s.OnData("satellite", new SatelliteSetSnapshot(new[]
        {
            new SatelliteFeed("viirs", "VIIRS", EquirectArea(double.NaN, double.NaN, null), new[]
            {
                Gray(T0, 360, 180, 50), Gray(T0 + TimeSpan.FromHours(1), 360, 180, 150), Gray(T0 + TimeSpan.FromHours(2), 360, 180, 250),
            }, T0, false),
        }, T0, false));
        SceneContract.Run(s, 240);
        var a = SceneContract.Snap(s, 120, 40);
        SceneContract.Run(s, 60);
        var b = SceneContract.Snap(s, 120, 40);
        Assert.NotEqual(a.Cells, b.Cells);
    }

    [Fact]
    public void ReceptionProgressShowsThenFinishes()
    {
        var s = new SatelliteScene();
        s.Init(120, 40, new Random(4), new SceneOptions());
        s.OnData("satellite", new SatelliteSetSnapshot(new[]
        {
            new SatelliteFeed("viirs", "VIIRS", EquirectArea(double.NaN, double.NaN, null), new[] { Gray(T0, 360, 180, 120) }, T0, false),
        }, T0, false));
        SceneContract.Run(s, 30);
        Assert.True(AnyRowContains(SceneContract.Snap(s, 120, 40), "ПРИЁМ"), "во время приёма нет строки прогресса");
        SceneContract.Run(s, 210);
        Assert.False(AnyRowContains(SceneContract.Snap(s, 120, 40), "ПРИЁМ"), "после приёма строка прогресса осталась");
    }

    [Fact]
    public void NightClockGivesAmberTones()
    {
        var s = new SatelliteScene();
        s.Init(120, 40, new Random(5), new SceneOptions());
        s.OnData("satellite", new SatelliteSetSnapshot(new[]
        {
            new SatelliteFeed("viirs", "VIIRS", EquirectArea(double.NaN, double.NaN, null), new[] { Gray(T0, 360, 180, 128) }, T0, false),
        }, T0, false));
        s.OnData("clock", new ClockSnapshot(new DateTime(2026, 10, 9, 23, 0, 0)));
        SceneContract.Run(s, 240);
        var g = SceneContract.Snap(s, 120, 40);

        Assert.All(g.Cells, c => Assert.True(c.Bg.R >= c.Bg.G && c.Bg.G >= c.Bg.B, $"фон не янтарный: {c.Bg}"));
        Assert.Contains(g.Cells, c => c.Bg.R > 80 && c.Bg.B < 10);
    }

    [Fact]
    public void DayClockKeepsNeutralTones()
    {
        var s = new SatelliteScene();
        s.Init(120, 40, new Random(6), new SceneOptions());
        s.OnData("satellite", new SatelliteSetSnapshot(new[]
        {
            new SatelliteFeed("viirs", "VIIRS", EquirectArea(double.NaN, double.NaN, null), new[] { Gray(T0, 360, 180, 128) }, T0, false),
        }, T0, false));
        s.OnData("clock", new ClockSnapshot(new DateTime(2026, 10, 9, 12, 0, 0)));
        SceneContract.Run(s, 240);
        var g = SceneContract.Snap(s, 120, 40);
        Assert.Contains(g.Cells, c => c.Bg.R > 50 && c.Bg.R == c.Bg.G && c.Bg.G == c.Bg.B);
    }

    private static SatelliteArea EquirectArea(double lat, double lon, string? name) =>
        new(SatelliteProjection.Equirect, -180, -90, 180, 90, 0, 1, lat, lon, name);

    private static SatelliteFrame Gray(DateTimeOffset at, int w, int h, byte v)
    {
        var rgb = new byte[w * h * 3];
        Array.Fill(rgb, v);
        return new SatelliteFrame(at, w, h, rgb);
    }

    /// <summary>
    /// Кадр 1°/пиксель: ровный фон облачности (яркость 90) и гауссовы пятна (σ 12°) в заданных точках (lon, lat).
    /// </summary>
    private static SatelliteFrame CloudFrame(DateTimeOffset at, params (double Lon, double Lat)[] blobs)
    {
        const int w = 360, h = 180;
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double plon = -180 + x + 0.5, plat = 90 - y - 0.5;
                double v = 90;
                foreach (var b in blobs)
                {
                    double dl = plon - b.Lon, dt = plat - b.Lat;
                    v += 165 * Math.Exp(-(dl * dl + dt * dt) / (2 * 12.0 * 12.0));
                }
                int i = (y * w + x) * 3;
                rgb[i] = rgb[i + 1] = rgb[i + 2] = (byte)Math.Clamp((int)v, 0, 255);
            }
        return new SatelliteFrame(at, w, h, rgb);
    }

    private static SatelliteArea DiskArea() =>
        new(SatelliteProjection.FullDisk, -180, -90, 180, 90, 0, 1, double.NaN, double.NaN, null);

    /// <summary>Кадр FullDisk: светлый диск радиусом Width/2 по центру, снаружи чёрно.</summary>
    private static SatelliteFrame Disk(DateTimeOffset at, int w, int h)
    {
        var rgb = new byte[w * h * 3];
        double cx = w / 2.0, cy = h / 2.0, r = w / 2.0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                byte v = dx * dx + dy * dy <= r * r ? (byte)200 : (byte)0;
                int i = (y * w + x) * 3;
                rgb[i] = rgb[i + 1] = rgb[i + 2] = v;
            }
        return new SatelliteFrame(at, w, h, rgb);
    }

    private static double Luma(Rgba c) => c.R + c.G + c.B;

    private static string RowText(CharGrid g, int y)
    {
        var sb = new StringBuilder(g.Cols);
        for (int x = 0; x < g.Cols; x++) sb.Append(g[x, y].Ch);
        return sb.ToString();
    }

    private static bool AnyRowContains(CharGrid g, string text)
    {
        for (int y = 0; y < g.Rows; y++)
            if (RowText(g, y).Contains(text)) return true;
        return false;
    }
}
