using System.Diagnostics;
using System.Text.Json;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class MetroSceneTests
{
    private const int StillFrames = 115;   // 2 с при 60 кадрах/с, с запасом на округление
    private const int SchemeRight = 145;   // правая граница схемы на 192 колонках (панель с колонки 148)
    private const int QueueDist = 12;      // поезд стоит в очереди, если впереди такой же поезд не дальше, субпикселей

    private readonly ITestOutputHelper _out;

    public MetroSceneTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new MetroScene(), "metro");

    [Fact]
    public void NoMusicIsAlive()
    {
        var s = SceneContract.Make(() => new MetroScene(), 120, 40, 81);
        SceneContract.Run(s, 60);
        var a = SceneContract.Snap(s, 120, 40);
        SceneContract.Run(s, 180);                       // 3 с без музыки
        var b = SceneContract.Snap(s, 120, 40);
        double share = ChangedShare(a, b);
        _out.WriteLine($"без музыки за 3 с изменилось {share:P1} ячеек");
        Assert.True(share > 0.01, $"за 3 с изменилось лишь {share:P1} ячеек");
    }

    [Fact]
    public void UpdateAndDrawAre_Fast_On192x60()
    {
        var s = SceneContract.Make(() => new MetroScene(), 192, 60, 31);
        var g = new CharGrid(192, 60);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 600; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
        }
        sw.Stop();
        _out.WriteLine($"600 кадров Update+Draw 192x60: {sw.Elapsed.TotalMilliseconds:F1} мс");
        Assert.True(sw.ElapsedMilliseconds < 1500, $"600 кадров заняли {sw.ElapsedMilliseconds} мс");
    }

    [Fact]
    public void StationsAreInsideTheSchematic_On192x60()
    {
        var s = SceneContract.Make(() => new MetroScene(), 192, 60, 11);
        SceneContract.Run(s, 30);
        var g = SceneContract.Snap(s, 192, 60);
        int stations = 0;
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Cols; x++)
            {
                char ch = g[x, y].Ch;
                if (ch != '●' && ch != '○') continue;
                stations++;
                Assert.True(x >= 1 && x <= SchemeRight, $"станция вне схемы: x={x}");
                Assert.True(y >= 2 && y <= 57, $"станция вне схемы: y={y}");
            }
        _out.WriteLine($"станций на экране: {stations}");
        Assert.True(stations >= 8, $"станций всего {stations}");
    }

    [Fact]
    public void LabelsDoNotCoverTheScheme()
    {
        var on = Render(192, 60, "{\"labels\":true}", 13);
        var off = Render(192, 60, "{\"labels\":false}", 13);
        int letters = 0, overwritten = 0;
        for (int i = 0; i < on.Cells.Length; i++)
        {
            char a = off.Cells[i].Ch, b = on.Cells[i].Ch;
            if (b >= 'A' && b <= 'Z' && a == ' ') letters++;
            if (a != ' ' && a != b) overwritten++;
        }
        _out.WriteLine($"подписей добавлено: {letters}, схема затёрта в {overwritten} ячейках");
        Assert.True(letters > 20, $"подписи почти не появились: {letters}");
        Assert.Equal(0, overwritten);
    }

    [Fact]
    public void CenterlinesAreContinuous()
    {
        foreach (int seed in new[] { 1, 2, 3, 4, 5 })
        {
            var s = new MetroScene();
            s.Init(192, 60, new Random(seed), new SceneOptions());
            _out.WriteLine($"seed {seed}: линий {s.LineCountNow}, пересадок {s.TransfersNow}, разрывов {s.CenterlineGaps()}");
            Assert.Equal(0, s.CenterlineGaps());
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void TransfersStayBetweenTwoAndFour(int seed)
    {
        var s = new MetroScene();
        s.Init(192, 60, new Random(seed), new SceneOptions());
        _out.WriteLine($"seed {seed}: линий {s.LineCountNow}, пересадок {s.TransfersNow}");
        Assert.Equal(4, s.LineCountNow);
        Assert.InRange(s.TransfersNow, 2, 4);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void HubsAreSeparatedAndCarryTwoLinesEach(int seed)
    {
        var s = new MetroScene();
        s.Init(192, 60, new Random(seed), new SceneOptions());
        var hubs = s.HubsNow();
        _out.WriteLine($"seed {seed}: узлов {hubs.Length}, стray-перекрёстков {s.StrayClustersNow}, третьих линий {s.ThirdLineViolations()}");
        Assert.InRange(hubs.Length, 2, 4);
        foreach (var h in hubs) Assert.Equal(2, h.Lines);
        for (int a = 0; a < hubs.Length; a++)
            for (int b = a + 1; b < hubs.Length; b++)
            {
                double d = Math.Sqrt(Math.Pow(hubs[a].X - hubs[b].X, 2) + Math.Pow(hubs[a].Y - hubs[b].Y, 2));
                Assert.True(d >= 10, $"узлы {a} и {b} на расстоянии {d:F1} колонок");
            }
        Assert.Equal(0, s.ThirdLineViolations());
        Assert.InRange(s.StrayClustersNow, 0, 2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void TwoLinesNeverShareACorridor(int seed)
    {
        var s = new MetroScene();
        s.Init(192, 60, new Random(seed), new SceneOptions());
        int shared = s.SharedPixelsOutsideHubs();
        _out.WriteLine($"seed {seed}: субпикселей общих вне узлов {shared} (учтено генератором {s.StrayPixelsNow})");
        Assert.InRange(shared, 0, 16);
        Assert.InRange(s.StrayPixelsNow, 0, 16);
        Assert.InRange(s.StrayClustersNow, 0, 2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void StationNamesAreUnique(int seed)
    {
        var s = new MetroScene();
        s.Init(192, 60, new Random(seed), new SceneOptions());
        var names = s.StationNamesNow();
        _out.WriteLine($"seed {seed}: станций {names.Length}, уникальных имён {names.Distinct().Count()}");
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LabelsOnOneRowHaveGapsBetweenThem(int seed)
    {
        var s = new MetroScene();
        s.Init(192, 60, new Random(seed), new SceneOptions());
        var words = s.LabelWordsNow();
        foreach (var rowGroup in words.GroupBy(w => w.Y))
        {
            var sorted = rowGroup.OrderBy(w => w.X).ToArray();
            for (int i = 1; i < sorted.Length; i++)
            {
                int gap = sorted[i].X - (sorted[i - 1].X + sorted[i - 1].Len);
                Assert.True(gap >= 2, $"подписи «{sorted[i - 1].X}..» и «{sorted[i].X}» в строке {rowGroup.Key} разделены {gap} ячейками");
            }
        }
        _out.WriteLine($"seed {seed}: подписей {words.Length}");
    }

    [Fact]
    public void TickerStaysBetweenTheRoundedCorners()
    {
        var s = SceneContract.Make(() => new MetroScene(), 192, 60, 41);
        SceneContract.Run(s, 600);
        var g = SceneContract.Snap(s, 192, 60);
        // Бегущая строка — в колонках 3..(ширина−3) последней строки; левые 3 и правые 2 колонки пусты.
        for (int x = 0; x < 3; x++) Assert.Equal(' ', g[x, 59].Ch);
        for (int x = 190; x < 192; x++) Assert.Equal(' ', g[x, 59].Ch);
    }

    [Fact]
    public void TrainsStopOnStationsOrInQueue()
    {
        var s = new MetroScene();
        s.Init(192, 60, new Random(101), new SceneOptions());
        var last = new Dictionary<int, double>();
        var still = new Dictionary<int, int>();
        int waitsAtStation = 0, violations = 0;
        for (int f = 0; f < 40 * 60; f++)
        {
            s.Update(SceneContract.Dt);
            var info = s.TrainInfo();
            foreach (var t in info)
            {
                if (last.TryGetValue(t.Key, out double pu) && pu == t.U) still[t.Key] = still.GetValueOrDefault(t.Key) + 1;
                else still[t.Key] = 0;
                last[t.Key] = t.U;
                if (still[t.Key] < StillFrames) continue;
                if (t.AtStation) { waitsAtStation++; continue; }
                if (!InQueue(info, t)) violations++;
            }
        }
        _out.WriteLine($"кадров стоянки на станции: {waitsAtStation}, неподвижных без станции и очереди: {violations}");
        Assert.True(waitsAtStation > 0, "ни один поезд не стоял на станции 2 с");
        Assert.Equal(0, violations);
    }

    [Fact]
    public void TrainsOfOneDirectionNeverShareATrack()
    {
        var s = new MetroScene();
        s.Init(192, 60, new Random(102), new SceneOptions());
        int bad = 0;
        for (int f = 0; f < 40 * 60; f++)
        {
            s.Update(SceneContract.Dt);
            if (f % 6 == 0) bad += s.SharedTrainCells();
        }
        _out.WriteLine($"совпадений поездов одной колеи за 40 с: {bad}");
        Assert.Equal(0, bad);
    }

    [Fact]
    public void ParamsCanBeSetAndSceneStillDraws()
    {
        var g = Render(120, 40, "{\"lines\":5,\"trains\":2,\"labels\":false}", 21);
        Assert.Contains(g.Cells, c => c.Ch != ' ' && (c.Fg.R | c.Fg.G | c.Fg.B) != 0);
    }

    [Fact]
    public void LinesParamChangesPicture() =>
        AssertParamDiffers("{\"lines\":3}", "{\"lines\":6}", "lines");

    [Fact]
    public void TrainsParamChangesPicture() =>
        AssertParamDiffers("{\"trains\":1}", "{\"trains\":5}", "trains");

    [Fact]
    public void LabelsParamChangesPicture() =>
        AssertParamDiffers("{\"labels\":false}", "{\"labels\":true}", "labels");

    /// <summary>Поезд в очереди: впереди такой же (тот же направление) поезд на расстоянии до QueueDist.</summary>
    private static bool InQueue((int Key, int Line, bool Forward, bool AtStation, double U, int M, float V)[] info, (int Key, int Line, bool Forward, bool AtStation, double U, int M, float V) t)
    {
        foreach (var o in info)
        {
            if (o.Key == t.Key || o.Line != t.Line || o.Forward != t.Forward) continue;
            double d = ((o.U - t.U) % t.M + t.M) % t.M;
            if (d > 0 && d <= QueueDist) return true;
        }
        return false;
    }

    private void AssertParamDiffers(string minJson, string maxJson, string name)
    {
        var a = Render(120, 40, minJson, 111);
        var b = Render(120, 40, maxJson, 111);
        int diff = 0;
        for (int i = 0; i < a.Cells.Length; i++)
            if (a.Cells[i].Ch != b.Cells[i].Ch || a.Cells[i].Fg != b.Cells[i].Fg) diff++;
        _out.WriteLine($"{name}: отличий ячеек {diff}");
        Assert.True(diff > 20, $"параметр {name} почти не меняет картинку: {diff} ячеек");
    }

    private static CharGrid Render(int cols, int rows, string json, int seed)
    {
        var s = new MetroScene();
        s.Init(cols, rows, new Random(seed), new SceneOptions { Params = JsonDocument.Parse(json).RootElement });
        SceneContract.Run(s, 120);
        return SceneContract.Snap(s, cols, rows);
    }

    private static double ChangedShare(CharGrid a, CharGrid b)
    {
        int n = 0;
        for (int i = 0; i < a.Cells.Length; i++)
            if (a.Cells[i].Ch != b.Cells[i].Ch || a.Cells[i].Fg != b.Cells[i].Fg || a.Cells[i].Bg != b.Cells[i].Bg) n++;
        return n / (double)a.Cells.Length;
    }
}
