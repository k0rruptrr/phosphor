using System.Text.Json;
using Phosphor.Core;

namespace Phosphor.Scenes.Tests;

public sealed class RadarSceneTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private const int Cols = 240, Rows = 80;
    private const double CenterLat = 44.82, CenterLon = 20.29;   // Белград

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new RadarScene(), "radar");

    [Fact]
    public void ListensToFlights()
    {
        Assert.Equal(new[] { "flights" }, new RadarScene().DataKeys);
    }

    [Fact]
    public void NoDataShowsNoAdsbAndBeamKeepsSpinning()
    {
        var s = Start(null);
        int f = 0;
        var g0 = At(s, ref f, 1.0);
        var g1 = At(s, ref f, 3.0);
        Assert.True(AnyRowContains(g0, "NO ADS-B DATA"), "без данных нет надписи NO ADS-B DATA");
        Assert.False(g0.Cells.SequenceEqual(g1.Cells), "без данных радар должен крутиться");
    }

    [Fact]
    public void EmptyListShowsNoTargets()
    {
        var s = Start(Flights());
        int f = 0;
        var g = At(s, ref f, 1.0);
        Assert.True(AnyRowContains(g, "ЦЕЛЕЙ НЕ ОБНАРУЖЕНО"));
        Assert.Empty(Find(g, '•'));
    }

    [Fact]
    public void MarkLightsUpAtBeamPassAndDimsByHalfTurn()
    {
        // Самолёт на 50 nm к востоку (пеленг 90°), стоит. Луч проходит 90° на 1.5 с оборота (период 6 с).
        var s = Start(Flights(Ac("DLH1", "A320", 44.82, LonAt(50), 35000)));
        int f = 0;
        var g = At(s, ref f, 1.55);
        var marks = Find(g, '•');
        Assert.Single(marks);
        var (mx, my) = marks[0];
        var (cx, cy) = Find(g, '┼')[0];
        Assert.True(mx > cx && my == cy, "отметка должна стоять справа от центра на горизонтали");
        double lumPass = Lum(g[mx, my].Fg);

        var gHalf = At(s, ref f, 4.55);       // пол-оборота спустя
        double lumHalf = Lum(gHalf[mx, my].Fg);
        Assert.True(lumPass > 0, "сразу после прохода луча отметка не видна");
        Assert.True(lumHalf < lumPass * 0.5, $"через пол-оборота отметка не потускнела: {lumHalf} против {lumPass}");
    }

    [Theory]
    [InlineData(1.8f)]
    [InlineData(2.4f)]
    public void CircleIsRoundInPixelsForCellAspect(float aspect)
    {
        // Круг — круг в пикселях: отметки на краю шкалы (100 nm) к востоку и к северу.
        // Горизонтальный радиус в клетках (dx) × ширина клетки = вертикальный (dy) × высота = dy × aspect × ширина.
        var o = new SceneOptions { CellAspect = aspect };
        var east = Start(Flights(Ac("E1", "A320", 44.82, LonAt(100), 35000)), o);
        int fe = 0;
        var ge = At(east, ref fe, 1.55);
        var (ex, _) = Find(ge, '•')[0];
        var (cx, cy) = Find(ge, '┼')[0];

        var north = Start(Flights(Ac("N1", "A320", LatAt(100), CenterLon, 35000)), o);
        int fn = 0;
        var gn = At(north, ref fn, 6.05);      // луч прошёл 0° (север) на 6.0 с
        var nm = Find(gn, '•');
        Assert.Single(nm);
        var (_, ny) = nm[0];

        int dxCols = ex - cx, dyRows = cy - ny;
        Assert.True(Math.Abs(dxCols - aspect * dyRows) <= 1.0,
            $"A={aspect}: {dxCols} столбцов по горизонтали против {dyRows} строк по вертикали (ожидалось {aspect * dyRows:F1} столбцов)");
    }

    [Fact]
    public void TrailFadesBehindBeamIntoBackground()
    {
        // θ = 10°: клетка в 10° за лучом (север) светится послесвечением; клетка при 90° (далеко позади) — фон.
        var s = Start(null);
        int f = 0;
        var g = At(s, ref f, 10.0 / 360.0 * 6.0);
        var (cx, cy) = Find(g, '┼')[0];
        var behind = g[cx, cy - 5].Bg;
        Assert.True(behind.G > 40, $"в 10° за лучом послесвечения нет: G={behind.G}");
        var far = g[cx + 10, cy - 1].Bg;
        Assert.Equal(Rgba.Hex(0x03100A), far);
    }

    [Fact]
    public void BeamAndEdgeAreDrawnWithSextants()
    {
        var s = Start(null);
        int f = 0;
        var g = At(s, ref f, 1.5);            // луч на 90° — на восточном радиусе
        Assert.True(g.Cells.Count(c => GlyphSet.IsSextant(c.Ch)) > 30, "край окружности и луч должны идти секстантами");
        var (cx, cy) = Find(g, '┼')[0];
        int beamHits = 0;
        for (int x = cx + 1; x < cx + 30; x++) if (GlyphSet.IsSextant(g[x, cy].Ch) || GlyphSet.IsSextant(g[x, cy - 1].Ch)) beamHits++;
        Assert.True(beamHits > 0, "луч на 90° не виден на восточном радиусе");
    }

    [Fact]
    public void LabelsNeverOverlapEachOther()
    {
        // Пять самолётов в одной точке: четыре формуляра займут СВ, ЮВ, ЮЗ, СЗ целиком; пятый — не влезает.
        var same = new[]
        {
            Ac("AAA1", "A320", 44.82, LonAt(50), 35000, 480f, 90f),
            Ac("BBB2", "A320", 44.82, LonAt(50), 35000, 480f, 90f),
            Ac("CCC3", "A320", 44.82, LonAt(50), 35000, 480f, 90f),
            Ac("DDD4", "A320", 44.82, LonAt(50), 35000, 480f, 90f),
            Ac("EEE5", "A320", 44.82, LonAt(50), 35000, 480f, 90f),
        };
        var s = Start(Flights(same));
        int f = 0;
        var g = At(s, ref f, 1.55);
        // «480KT» есть только в формулярах (в табличке скорости нет): ровно четыре полных формуляра.
        Assert.Equal(4, CountOf(g, "480KT"));
        // Позывные четырёх формуляров — рядом с табличкой (по одному в табличке + один в формуляре).
        foreach (var call in new[] { "AAA1", "BBB2", "CCC3", "DDD4" })
            Assert.Equal(2, CountOf(g, call));
        // Пятый борт: формуляр не влез никуда — позывной только в табличке.
        Assert.Equal(1, CountOf(g, "EEE5"));
    }

    private static int CountOf(CharGrid g, string text)
    {
        int n = 0;
        for (int y = 0; y < g.Rows; y++)
        {
            string row = RowText(g, y);
            for (int i = row.IndexOf(text, StringComparison.Ordinal); i >= 0; i = row.IndexOf(text, i + 1, StringComparison.Ordinal)) n++;
        }
        return n;
    }

    [Fact]
    public void ExtrapolatesByCourseAndSpeed()
    {
        // 480 kt = 8 nm/мин. За 30 с — 4 nm по курсу (восток).
        var s = Start(Flights(Ac("SPD1", "B738", 44.82, LonAt(50), 35000, 480f, 90f)));
        int f = 0;
        var g1 = At(s, ref f, 1.55);
        var (c1, _) = Find(g1, '•')[0];
        var (cx, _) = Find(g1, '┼')[0];
        var g2 = At(s, ref f, 31.55);
        var m2 = Find(g2, '•');
        Assert.Single(m2);
        int c2 = m2[0].x;

        double expected = (c1 - cx) * 4.0 / 50.0;   // 4 nm из 50
        Assert.True(c2 > c1, "самолёт не сдвинулся по курсу");
        Assert.True(Math.Abs((c2 - c1) - expected) <= 1.0,
            $"за 30 с сдвиг {c2 - c1} столбцов, ожидалось ≈{expected:F1}");
    }

    [Fact]
    public void GroundAircraftIsNotShown()
    {
        var s = Start(Flights(Ac("GND1", "A320", 44.82, LonAt(50), -1)));
        int f = 0;
        var g = At(s, ref f, 1.55);
        Assert.Empty(Find(g, '•'));
        Assert.False(AnyRowContains(g, "GND1"));
    }

    [Fact]
    public void TableShowsNearestAirborneFirstAndSkipsGround()
    {
        var s = Start(Flights(
            Ac("SU1111", "B738", LatAt(80), CenterLon, 30000, 450f, 0f),
            Ac("BA222", "A320", 44.82, LonAt(10), 35000, 450f, 270f),
            Ac("GND9", "A320", LatAt(5), CenterLon, -1)));
        int f = 0;
        var g = At(s, ref f, 1.0);
        int rowBa = RowOf(g, "BA222", Rows - 9);
        int rowSu = RowOf(g, "SU1111", Rows - 9);
        Assert.True(rowBa >= 0 && rowSu >= 0, "оба борта в воздухе должны быть в табличке");
        Assert.True(rowBa < rowSu, "ближайший борт должен быть первым");
        Assert.True(AnyRowContains(g, "FL350"));
        Assert.False(AnyRowContains(g, "GND9"), "самолёт на земле в табличке быть не должен");
    }

    [Fact]
    public void RemoteStationShowsBlinkingCoverageWarning()
    {
        var s = Start(FlightsEx(new Aircraft[0], remote: true, station: "Belgrade-2"));
        int f = 0;
        bool seenOn = false, seenOff = false;
        for (int i = 0; i < 120; i++)
        {
            var g = At(s, ref f, (i + 1) / 60.0);
            if (AnyRowContains(g, "ДАЛЬНЯЯ СТАНЦИЯ"))
            {
                seenOn = true;
                Assert.True(AnyRowContains(g, "BELGRADE-2"), "вместе с предупреждением нужно имя станции");
            }
            else seenOff = true;
        }
        Assert.True(seenOn && seenOff, "предупреждение о покрытии должно мигать");
    }

    [Fact]
    public void LocalStationHasNoCoverageWarning()
    {
        var s = Start(Flights(Ac("BA1", "A320", 44.82, LonAt(30), 35000)));
        int f = 0;
        var g = At(s, ref f, 1.0);
        Assert.False(AnyRowContains(g, "ДАЛЬНЯЯ"));
    }

    [Fact]
    public void LabelsParamTogglesFormulary()
    {
        // Формуляр содержит скорость «480KT»; в табличке её нет.
        var snap = Flights(Ac("SPD2", "B738", 44.82, LonAt(50), 35000, 480f, 90f));
        int f = 0;
        var on = At(Start(snap), ref f, 1.55);
        Assert.True(AnyRowContains(on, "480KT"), "формуляр должен быть включён по умолчанию");

        int f2 = 0;
        var off = At(Start(snap, Opts(new { labels = false })), ref f2, 1.55);
        Assert.False(AnyRowContains(off, "480KT"), "labels=false — формуляра быть не должно");
        Assert.Single(Find(off, '•'));
    }

    [Fact]
    public void RangeParamSetsRingScale()
    {
        // Кольца каждые 25 nm: при 50 nm — только 25 и 50; при auto по данным (100 nm) — и 75.
        var snap = FlightsEx(new Aircraft[0], radiusNm: 100);
        int f = 0;
        var fixed50 = At(Start(snap, Opts(new { rangeNm = "50" })), ref f, 0.5);
        Assert.True(AnyRowContains(fixed50, "25") && AnyRowContains(fixed50, "50"));
        Assert.False(AnyRowContains(fixed50, "75"), "при 50 nm кольца 75 быть не должно");

        int f2 = 0;
        var auto = At(Start(snap, Opts(new { rangeNm = "auto" })), ref f2, 0.5);
        Assert.True(AnyRowContains(auto, "75"), "при auto кольцо 75 нужно от радиуса данных (100 nm)");
    }

    // ---------- помощники ----------

    private static RadarScene Start(FlightSnapshot? snap, SceneOptions? o = null)
    {
        var s = (RadarScene)SceneContract.Make(() => new RadarScene(), Cols, Rows, 5, o);
        if (snap is not null) s.OnData("flights", snap);
        return s;
    }

    /// <summary>Довести сцену до времени sec (кадры по 1/60 с) и снять сетку.</summary>
    private static CharGrid At(RadarScene s, ref int frames, double sec)
    {
        int target = (int)Math.Round(sec * 60);
        for (; frames < target; frames++) s.Update(SceneContract.Dt);
        return SceneContract.Snap(s, Cols, Rows);
    }

    private static FlightSnapshot Flights(params Aircraft[] list) => FlightsEx(list);

    private static FlightSnapshot FlightsEx(Aircraft[] list, bool remote = false,
        string station = "Belgrade", double radiusNm = 100)
        => new(station, remote, CenterLat, CenterLon, radiusNm, list, T0, false);

    private static Aircraft Ac(string call, string typ, double lat, double lon, int altFt, float kt = 0f, float track = 0f)
        => new("3C6444", call, typ, lat, lon, altFt, kt, track, 0f);

    private static double LonAt(double nmEast) => CenterLon + nmEast / (60.0 * Math.Cos(CenterLat * Math.PI / 180.0));
    private static double LatAt(double nmNorth) => CenterLat + nmNorth / 60.0;

    private static SceneOptions Opts(object o) => new() { Params = JsonSerializer.SerializeToElement(o) };

    private static List<(int x, int y)> Find(CharGrid g, char ch)
    {
        var r = new List<(int, int)>();
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Cols; x++)
                if (g[x, y].Ch == ch) r.Add((x, y));
        return r;
    }

    private static string RowText(CharGrid g, int y)
    {
        var chars = new char[g.Cols];
        for (int x = 0; x < g.Cols; x++) chars[x] = g[x, y].Ch;
        return new string(chars);
    }

    private static bool AnyRowContains(CharGrid g, string text) => RowOf(g, text, 0) >= 0;

    private static int RowOf(CharGrid g, string text, int fromRow)
    {
        for (int y = Math.Max(0, fromRow); y < g.Rows; y++)
            if (RowText(g, y).Contains(text, StringComparison.Ordinal)) return y;
        return -1;
    }

    private static double Lum(Rgba c) => c.R + c.G + c.B;
}
