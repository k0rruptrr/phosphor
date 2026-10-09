using System.Text;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class OrganismSceneTests
{
    private const double Mb = 1048576.0;
    private readonly ITestOutputHelper _out;

    public OrganismSceneTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new OrganismScene(), "organism");

    [Fact]
    public void CellCountEqualsCoreCount()
    {
        foreach (var n in new[] { 1, 4, 12, 16, 17, 32, 64 })
        {
            var s = Fresh(120, 40, 11);
            Feed(s, "system", Sys(Cores(n, 0.5f)));
            Assert.Equal(n, ((OrganismScene)s).CoreCellCount);
        }
    }

    [Fact]
    public void FullyLoadedCoreIsBrighterAndBiggerThanIdleCore()
    {
        // Два ядра: c0 = 100%, c1 = 0%. c0 стоит наверху кольца, c1 — внизу.
        var s = Fresh(120, 40, 12);
        Feed(s, "system", Sys(new float[] { 100f, 0f }));
        SceneContract.Run(s, 240);
        var g = SceneContract.Snap(s, 120, 40);
        _out.WriteLine(SceneContract.Dump(g, "organism-two-cores"));

        var (x0, y0) = ((OrganismScene)s).CoreCellGridPos(0);
        var (x1, y1) = ((OrganismScene)s).CoreCellGridPos(1);
        Assert.True(x0 >= 0 && y0 >= 0 && x1 >= 0 && y1 >= 0, "центры клеток не найдены");
        double topLuma = WindowLuma(g, x0, y0, 5, 3);
        double botLuma = WindowLuma(g, x1, y1, 5, 3);
        _out.WriteLine($"окно c0 (100%): {topLuma:F0}, окно c1 (0%): {botLuma:F0}");
        Assert.True(topLuma > botLuma, $"ядро 100% ({topLuma:F0}) не ярче ядра 0% ({botLuma:F0})");
    }

    [Fact]
    public void HostClockAndMemoryAreOnScreen()
    {
        var s = Fresh(120, 40, 13);
        Feed(s, "system", Sys(Cores(8, 0.3f)));
        Feed(s, "clock", new ClockSnapshot(new DateTime(2026, 10, 9, 16, 42, 7)));
        SceneContract.Run(s, 30);
        var g = SceneContract.Snap(s, 120, 40);
        var path = SceneContract.Dump(g, "organism-data");
        _out.WriteLine(path);
        var text = GridText(g);
        Assert.Contains("K0RRY-PC", text);
        Assert.Contains("АПТАЙМ 3Д 04:12", text);
        Assert.Contains("16:42:07", text);
        Assert.Contains("11.2/16 ГБ", text);
        Assert.Contains("GPU 34%", text);
    }

    [Fact]
    public void NoSystemDataMeansSleepWithoutExceptions()
    {
        var s = Fresh(120, 40, 14);
        SceneContract.Run(s, 120);
        var g = SceneContract.Snap(s, 120, 40);
        _out.WriteLine(SceneContract.Dump(g, "organism-sleep"));
        Assert.Contains("ДАТЧИКИ НЕДОСТУПНЫ", GridText(g));
        Assert.Equal(0, ((OrganismScene)s).CoreCellCount);
    }

    [Fact]
    public void SixtyFourCoresKeepLayout()
    {
        var s = Fresh(160, 45, 15);
        Feed(s, "system", Sys(Cores(64, 0.6f)));
        Feed(s, "phosphor", Phos(128f, false));
        SceneContract.Run(s, 300);
        var g = SceneContract.Snap(s, 160, 45);
        _out.WriteLine(SceneContract.Dump(g, "organism-64"));
        Assert.Equal(64, ((OrganismScene)s).CoreCellCount);
        Assert.Contains("♥ 128 BPM", GridText(g));
        Assert.Contains("ПАМЯТЬ", GridText(g));
    }

    [Fact]
    public void BeatChangesFrame()
    {
        var a = Fresh(120, 40, 16);
        var b = Fresh(120, 40, 16);
        Feed(a, "system", Sys(Cores(8, 0.4f)));
        Feed(b, "system", Sys(Cores(8, 0.4f)));
        SceneContract.Run(a, 120);
        SceneContract.Run(b, 120);
        // Удар только у A: в кадре бит должен изменить картинку относительно B без бита.
        ((IAudioReactive)a).OnAudio(new AudioFrame(true, 0.6f, 0.5f, 0.4f, 0.3f, true, 1f, 120f));
        ((IAudioReactive)b).OnAudio(AudioFrame.Silent);
        a.Update(SceneContract.Dt);
        b.Update(SceneContract.Dt);
        var ga = SceneContract.Snap(a, 120, 40);
        var gb = SceneContract.Snap(b, 120, 40);
        Assert.NotEqual(ga.Cells, gb.Cells);
    }

    [Fact]
    public void NightIsAmberAndDayIsGreen()
    {
        var day = Fresh(120, 40, 17);
        Feed(day, "system", Sys(Cores(8, 0.7f)));
        Feed(day, "phosphor", Phos(0f, false));
        SceneContract.Run(day, 300);
        var gd = SceneContract.Snap(day, 120, 40);

        var night = Fresh(120, 40, 17);
        Feed(night, "system", Sys(Cores(8, 0.7f)));
        Feed(night, "phosphor", Phos(0f, true));
        SceneContract.Run(night, 300);
        var gn = SceneContract.Snap(night, 120, 40);

        int dayGreen = gd.Cells.Count(c => c.Fg.G > c.Fg.R + 40 && c.Fg.G > c.Fg.B);
        int nightGreen = gn.Cells.Count(c => c.Fg.G > c.Fg.R + 10 && c.Fg.G > c.Fg.B);
        int nightAmber = gn.Cells.Count(c => c.Fg.R > 150 && c.Fg.R > c.Fg.G && c.Fg.G > c.Fg.B);
        Assert.True(dayGreen > 0, "днём нет зелёных тонов");
        Assert.Equal(0, nightGreen);
        Assert.True(nightAmber > 0, "ночью нет янтарных тонов");
    }

    [Fact]
    public void NoAllocationsInUpdateAndDraw()
    {
        var s = Fresh(120, 40, 18);
        Feed(s, "system", Sys(Cores(16, 0.5f)));
        Feed(s, "phosphor", Phos(90f, false));
        Feed(s, "clock", new ClockSnapshot(new DateTime(2026, 10, 9, 16, 42, 7)));
        var g = new CharGrid(120, 40);
        SceneContract.Run(s, 60);
        s.Draw(g);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        _out.WriteLine($"аллокации за 100 кадров: {after - before} байт");
        Assert.True(after - before < 1024, $"аллокации в Update/Draw: {after - before} байт");
    }

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(64)]
    public void CellTendrilsAreContinuousAndTouchCoreAndCell(int cores)
    {
        var s = Fresh(160, 50, 19);
        var loads = new float[cores];
        for (int i = 0; i < cores; i++) loads[i] = (i % 5) * 25f;   // разные нагрузки — разные радиусы клеток
        Feed(s, "system", Sys(loads));
        SceneContract.Run(s, 240);
        var scene = (OrganismScene)s;
        var g = SceneContract.Snap(s, 160, 50);   // ОТРИСОВАННАЯ сетка, не геометрия
        for (int k = 0; k < cores; k++)
        {
            var t = scene.CellTendril(k);
            Assert.NotNull(t);
            var pts = t!.Points;
            // каждый пиксель отростка вне клетки должен быть виден на экране (не чёрный)
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                if (Dist(p.X, p.Y, t.CellX, t.CellY) <= t.CellR + 0.5f) continue;
                int gx = t.BodyX + p.X, gy = t.BodyY + p.Y / 2;
                if (p.Y < 0 || !g.InBounds(gx, gy)) continue;
                var cell = g[gx, gy];
                var px = p.Y % 2 == 0 ? cell.Fg : cell.Bg;
                Assert.True(px.R + px.G + px.B > 30,
                    $"ядро {k}: отросток не нарисован в точке {i} (холст {p.X},{p.Y}), пиксель чёрный");
            }
            // соседние точки растра — соседи (разрывов нет)
            for (int i = 1; i < pts.Count; i++)
            {
                int d = Math.Max(Math.Abs(pts[i].X - pts[i - 1].X), Math.Abs(pts[i].Y - pts[i - 1].Y));
                Assert.True(d <= 1, $"ядро {k}: разрыв отростка в точке {i}: шаг {d}");
            }
            // начало внутри ядра, конец внутри клетки (с 1 пикселем допуска на округление)
            var a = pts[0];
            var b = pts[^1];
            Assert.True(Dist(a.X, a.Y, t.CoreX, t.CoreY) <= t.CoreR * 1.12f + 1f, $"ядро {k}: начало отростка вне ядра");
            Assert.True(Dist(b.X, b.Y, t.CellX, t.CellY) <= t.CellR + 1f, $"ядро {k}: конец отростка вне клетки");
        }
    }

    private static float Dist(float ax, float ay, float bx, float by)
    {
        float dx = ax - bx, dy = ay - by;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // ---------- helpers ----------

    private static IScene Fresh(int cols, int rows, int seed) =>
        SceneContract.Make(() => new OrganismScene(), cols, rows, seed);

    private static void Feed(IScene s, string key, DataSnapshot d) => ((IDataScene)s).OnData(key, d);

    private static float[] Cores(int n, float load)
    {
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = 100f * load * (0.5f + 0.5f * MathF.Sin(i * 1.3f) + 0.5f);
        for (int i = 0; i < n; i++) a[i] = Math.Clamp(a[i], 0f, 100f);
        return a;
    }

    private static SystemSnapshot Sys(float[] cores)
    {
        float cpu = cores.Length == 0 ? 0f : cores.Average();
        return new SystemSnapshot("K0RRY-PC", "WINDOWS 11", new TimeSpan(3, 4, 12, 0), cpu, cores,
            11.2, 16.0, 34f, "RTX 2070", 4.1 * Mb, 0.3 * Mb, 2.0 * Mb, 0.5 * Mb, 300, DateTimeOffset.Now);
    }

    private static PhosphorSnapshot Phos(float bpm, bool night) => new(60f,
        new[] { "plasma", "teletext", "mandelbrot" }, new[] { "amber", "crt", "pal" },
        bpm, 0.62f, 0.5f, night, TimeSpan.FromMinutes(5), 1000, DateTimeOffset.Now);

    /// <summary>Яркость прямоугольника вокруг центра (cx, cy) в ячейках: ±rx по колонкам, ±ry по строкам.</summary>
    private static double WindowLuma(CharGrid g, int cx, int cy, int rx, int ry)
    {
        double sum = 0;
        for (int y = Math.Max(0, cy - ry); y <= Math.Min(g.Rows - 1, cy + ry); y++)
            for (int x = Math.Max(0, cx - rx); x <= Math.Min(g.Cols - 1, cx + rx); x++)
            {
                var c = g[x, y];
                sum += c.Fg.R + c.Fg.G + c.Fg.B + c.Bg.R + c.Bg.G + c.Bg.B;
            }
        return sum;
    }

    private static string GridText(CharGrid g)
    {
        var sb = new StringBuilder();
        for (int y = 0; y < g.Rows; y++)
        {
            for (int x = 0; x < g.Cols; x++) sb.Append(g[x, y].Ch);
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
