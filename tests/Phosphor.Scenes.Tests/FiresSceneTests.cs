using Phosphor.Core;

namespace Phosphor.Scenes.Tests;

public sealed class FiresSceneTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private const int Cols = 120, Rows = 40;
    private const double CLat = 55.5, CLon = 37.5, RadKm = 250;
    private const char Core = '●';
    private static readonly double KmPerLon = 111.0 * Math.Cos(CLat * Math.PI / 180);

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new FiresScene(), "fires");

    [Fact]
    public void NoDataShowsNoFirmsLabelAndPlainGridWithoutCentre()
    {
        var s = Start(null);
        SceneContract.Run(s, 60);
        var g = SceneContract.Snap(s, Cols, Rows);
        Assert.True(AnyRowContains(g, "NO FIRMS DATA"), "нет надписи NO FIRMS DATA");
        Assert.Null(FindFirst(g, '┼'));           // без данных центра нет
        Assert.False(AnyRowContains(g, "°"), "без данных не должно быть градусных подписей");
        Assert.DoesNotContain(g.Cells, c => c.Ch == Core);
    }

    [Fact]
    public void EmptySquareSaysNoFiresDetectedAndDrawsRings()
    {
        var s = Start(Snap());
        SceneContract.Run(s, 60);
        var g = SceneContract.Snap(s, Cols, Rows);
        Assert.True(AnyRowContains(g, "ОЧАГОВ НЕ ОБНАРУЖЕНО"), "нет надписи «очагов не обнаружено»");
        Assert.DoesNotContain(g.Cells, c => c.Ch == Core);
        Assert.NotEmpty(Sextants(g));              // кольца пунктиром секстантами
        Assert.True(AnyRowContains(g, "100 КМ"), "нет подписи кольца 100 км");
    }

    [Fact]
    public void FireOutsideSquareIsNotDrawn()
    {
        // 300 км на восток при радиусе 250 км — за пределами квадрата.
        var s = Start(Snap(Fire(CLat, CLon + 300 / KmPerLon, 80f)));
        SceneContract.Run(s, 30);
        var g = SceneContract.Snap(s, Cols, Rows);
        Assert.DoesNotContain(g.Cells, c => c.Ch == Core);
        Assert.True(AnyRowContains(g, "ОЧАГОВ НЕ ОБНАРУЖЕНО"), "за квадратом очаг не должен считаться");
    }

    [Fact]
    public void FireAtOffsetAppearsInExpectedCell()
    {
        // 50 км на восток: клеток на км = (side/2)/RadKm; рамка задаёт side.
        var s = Start(Snap(Fire(CLat, CLon + 50 / KmPerLon, 40f)));
        SceneContract.Run(s, 30);
        var g = SceneContract.Snap(s, Cols, Rows);

        var (cx, cy) = FindFirst(g, '┼')!.Value;
        var (tlx, _) = FindFirst(g, '┌')!.Value;
        var (trx, _) = FindFirst(g, '┐')!.Value;
        int side = trx - tlx - 1;

        var cores = Cores(g);
        Assert.Single(cores);
        double expX = cx + 50.0 / RadKm * (side / 2.0);
        Assert.True(Math.Abs(cores[0].x - expX) <= 1.0, $"очаг в колонке {cores[0].x}, ожидали около {expX:F1}");
        Assert.Equal(cy, cores[0].y);
        Assert.True(cores[0].x > cx, "очаг на востоке должен быть правее центра");
    }

    [Fact]
    public void FireToNorthIsAboveCentre()
    {
        // 100 км на север: полувысота квадрата (в строках) = RadKm; рамка задаёт высоту.
        var s = Start(Snap(Fire(CLat + 100 / 111.0, CLon, 40f)));
        SceneContract.Run(s, 30);
        var g = SceneContract.Snap(s, Cols, Rows);
        var (cx, cy) = FindFirst(g, '┼')!.Value;
        var (_, tly) = FindFirst(g, '┌')!.Value;
        var (_, bry) = FindLast(g, '┘')!.Value;
        int sideR = bry - tly - 1;

        var cores = Cores(g);
        Assert.Single(cores);
        double expY = cy - 100.0 / RadKm * (sideR / 2.0);
        Assert.True(Math.Abs(cores[0].y - expY) <= 1.0, $"очаг в строке {cores[0].y}, ожидали около {expY:F1}");
        Assert.True(cores[0].y < cy, "очаг на севере должен быть выше центра");
        Assert.Equal(cx, cores[0].x);
    }

    [Theory]
    [InlineData(1.8f)]
    [InlineData(2.4f)]
    public void MapStaysSquareInPixelsForAnyCellAspect(float aspect)
    {
        // Квадрат в пикселях: side колонок шириной 1 и sideR строк высотой aspect → sideR·aspect ≈ side.
        var s = Start(Snap(Fire(CLat, CLon + 20 / KmPerLon, 40f)), aspect: aspect);
        SceneContract.Run(s, 30);
        var g = SceneContract.Snap(s, Cols, Rows);
        var (tlx, tly) = FindFirst(g, '┌')!.Value;
        var (trx, _) = FindFirst(g, '┐')!.Value;
        var (brx, bry) = FindLast(g, '┘')!.Value;
        int side = trx - tlx - 1;
        int sideR = bry - tly - 1;
        Assert.Equal(brx - tlx - 1, side);
        Assert.True(Math.Abs(sideR * aspect - side) <= aspect, $"A={aspect}: {side} колонок × {sideR} строк не квадрат");
    }

    [Fact]
    public void FireCellIsAtLeastTwoAndHalfTimesBrighterThanRingCell()
    {
        // Средняя яркость клетки с очагом ≥ 2.5× средней яркости клетки кольца (пунктир секстантом, ~0.3).
        var s = Start(Snap(Fire(CLat, CLon + 20 / KmPerLon, 10f), Fire(CLat - 30 / 111.0, CLon, 60f)));
        SceneContract.Run(s, 30);
        var g = SceneContract.Snap(s, Cols, Rows);

        var fire = Cores(g).Select(p => Lum(g[p.x, p.y].Fg)).ToArray();
        Assert.NotEmpty(fire);
        // Кольца — секстанты ярче сетки (сетка ~0.18): порог — четверть яркости белого.
        int ringThreshold = Lum(new Rgba(255, 255, 255)) / 4;
        var ring = Sextants(g).Where(p => Lum(g[p.x, p.y].Fg) >= ringThreshold).Select(p => Lum(g[p.x, p.y].Fg)).ToArray();
        Assert.NotEmpty(ring);

        double fireAvg = fire.Average(), ringAvg = ring.Average();
        Assert.True(fireAvg >= 2.5 * ringAvg, $"очаг {fireAvg:F0} не в 2.5 раза ярче кольца {ringAvg:F0}");
    }

    private static int Lum(Rgba c) => c.R + c.G + c.B;

    // ---------- helpers ----------

    private static FireSnapshot Snap(params FirePoint[] pts) =>
        new("МОСКВА", CLat, CLon, RadKm, pts, T0, false);

    private static FirePoint Fire(double lat, double lon, float frp) =>
        new(lat, lon, frp, 330f, "nominal", T0.AddHours(-2), false);

    private static FiresScene Start(FireSnapshot? snap, int seed = 1, float aspect = 1.8f)
    {
        var s = new FiresScene();
        s.Init(Cols, Rows, new Random(seed), new SceneOptions { CellAspect = aspect });
        if (snap is not null) s.OnData("fires", snap);
        return s;
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

    private static (int x, int y)? FindFirst(CharGrid g, char ch)
    {
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Cols; x++)
                if (g[x, y].Ch == ch) return (x, y);
        return null;
    }

    private static (int x, int y)? FindLast(CharGrid g, char ch)
    {
        for (int y = g.Rows - 1; y >= 0; y--)
            for (int x = g.Cols - 1; x >= 0; x--)
                if (g[x, y].Ch == ch) return (x, y);
        return null;
    }

    private static List<(int x, int y)> Cores(CharGrid g)
    {
        var list = new List<(int x, int y)>();
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Cols; x++)
                if (g[x, y].Ch == Core) list.Add((x, y));
        return list;
    }

    private static List<(int x, int y)> Sextants(CharGrid g)
    {
        var list = new List<(int x, int y)>();
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Cols; x++)
                if (GlyphSet.IsSextant(g[x, y].Ch) && g[x, y].Ch != (char)0xE000) list.Add((x, y));
        return list;
    }
}
