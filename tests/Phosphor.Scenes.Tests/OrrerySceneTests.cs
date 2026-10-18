using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class OrrerySceneTests
{
    private readonly ITestOutputHelper _out;

    public OrrerySceneTests(ITestOutputHelper output) => _out = output;

    private static readonly Rgba EarthColor = Rgba.Hex(0x4DA6FF);
    private const double CellAspect = 1.8;

    private static SceneOptions Opts(object pars) => new() { Params = JsonSerializer.SerializeToElement(pars) };

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new OrreryScene(), "orrery");

    [Fact]
    public void NoMusicIsAlive()
    {
        var s = SceneContract.Make(() => new OrreryScene(), 120, 40, 81);
        SceneContract.Run(s, 60);
        var a = SceneContract.Snap(s, 120, 40);
        SceneContract.Run(s, 180);                       // 3 с без музыки
        var b = SceneContract.Snap(s, 120, 40);
        int lit = a.Cells.Count(c => c.Ch != ' ' || (c.Bg.R | c.Bg.G | c.Bg.B) > 24);
        double share = ChangedShare(a, b);
        _out.WriteLine($"без музыки за 3 с изменилось {share:P1} ячеек, освещённых {lit}");
        Assert.True(share > 0.02, $"за 3 с изменилось лишь {share:P1} ячеек");
    }

    [Fact]
    public void UpdateAndDrawAre_Fast_On192x60()
    {
        var s = SceneContract.Make(() => new OrreryScene(), 192, 60, 31);
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
    public void EarthAngleFollowsRealPeriod_Exact()
    {
        // Модель: 30 дней/с, Земля делает оборот за 365 дней. Точная проверка по внутреннему углу.
        var s = (OrreryScene)SceneContract.Make(() => new OrreryScene(), 192, 60, 12, Opts(new { daysPerSec = 30f, zoom = "outer" }));
        SceneContract.Run(s, 60);
        double a0 = s.EarthAngleDeg;
        SceneContract.Run(s, 600);                       // 10 с
        double delta = Wrap180(s.EarthAngleDeg - a0);
        double expected = Wrap180(360.0 * 10 * 30 / 365);
        _out.WriteLine($"угол Земли: изменение {delta:F3}°, ожидалось {expected:F3}°");
        Assert.True(Math.Abs(Wrap180(delta - expected)) < 0.05, $"угол Земли {delta:F3}°, ожидалось {expected:F3}°");
    }

    [Fact]
    public void EarthOnScreenMovesOnItsRealPeriod()
    {
        // Смысл сцены: на экране Земля крутится вокруг Солнца со своим периодом. Угол берём по ячейкам, zoom=inner.
        const int cols = 192, rows = 60, seconds = 10;
        var s = SceneContract.Make(() => new OrreryScene(), cols, rows, 11, Opts(new { daysPerSec = 30f, zoom = "inner", labels = false }));
        SceneContract.Run(s, 120);
        var a = FindEarth(SceneContract.Snap(s, cols, rows));
        SceneContract.Run(s, seconds * 60);
        var b = FindEarth(SceneContract.Snap(s, cols, rows));
        Assert.True(a.HasValue && b.HasValue, "Земля (•, синяя) не найдена на экране ровно одной ячейкой");
        double ang0 = AngleDeg(cols, rows, a!.Value), ang1 = AngleDeg(cols, rows, b!.Value);
        double delta = Wrap180(ang1 - ang0);
        double expected = Wrap180(360.0 * seconds * 30 / 365);
        double err = Wrap180(delta - expected);
        _out.WriteLine($"на экране Земля за {seconds} с повернулась на {delta:F1}°, ожидалось {expected:F1}°, ошибка {err:F1}°");
        Assert.True(Math.Abs(err) <= 6.0, $"на экране Земля повернулась на {delta:F1}°, ожидалось {expected:F1}°");
    }

    [Fact]
    public void OrbitsAreRoundWithCellAspect()
    {
        // Самая внешняя орбита (Нептун) — по её пунктиру: горизонтальный радиус ≈ вертикальный × CellAspect.
        const int cols = 192, rows = 60;
        var s = SceneContract.Make(() => new OrreryScene(), cols, rows, 21, Opts(new { zoom = "outer", labels = false }));
        SceneContract.Run(s, 120);
        var g = SceneContract.Snap(s, cols, rows);
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < cols; x++)
        {
            var c = g[x, y];
            if (!GlyphSet.IsSextant(c.Ch) || c.Ch == (char)0xE000) continue;
            if (c.Fg != OrreryScene.OrbitColor && c.Fg != OrreryScene.OrbitHi) continue;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        double hr = (maxX - minX + 1) / 2.0, vr = (maxY - minY + 1) / 2.0;
        double ratio = hr / (vr * CellAspect);
        _out.WriteLine($"орбита: горизонтальный радиус {hr:F1} ячеек, вертикальный {vr:F1} рядов, отношение {ratio:F3}");
        Assert.True(hr > 30, $"внешняя орбита слишком мала: {hr:F1}");
        Assert.True(Math.Abs(ratio - 1) < 0.05, $"орбита не круглая: отношение {ratio:F3}");
        Assert.True(minX >= 0 && maxX < cols && minY >= 0 && maxY < rows, "орбита вышла за экран");
    }

    [Fact]
    public void PlanetNamesAreReadable()
    {
        const int cols = 192, rows = 60;
        var s = SceneContract.Make(() => new OrreryScene(), cols, rows, 41, Opts(new { labels = true, zoom = "outer" }));
        SceneContract.Run(s, 120);
        var g = SceneContract.Snap(s, cols, rows);
        var sb = new StringBuilder();
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++) sb.Append(g[x, y].Ch);
            sb.Append('\n');
        }
        string text = sb.ToString();
        string[] names = { "MERCURY", "VENUS", "EARTH", "MARS", "JUPITER", "SATURN", "URANUS", "NEPTUNE" };
        int found = names.Count(n => text.Contains(n));
        _out.WriteLine($"видно подписей планет: {found} из {names.Length}");
        Assert.True(found >= 6, $"видно лишь {found} подписей из 8");
    }

    [Fact]
    public void BeatFlashBrightensSunHalo()
    {
        const int cols = 160, rows = 50;
        var s = SceneContract.Make(() => new OrreryScene(), cols, rows, 51);
        SceneContract.Run(s, 120);
        var audio = (IAudioReactive)s;

        // Яркость ореола в радиусе 5 ячеек от Солнца — после удара и через 0.5 с.
        audio.OnAudio(new AudioFrame(Active: true, Level: 0f, Bass: 0f, Mid: 0f, High: 0f, Beat: true, BeatPulse: 1f, Bpm: 120f));
        s.Update(SceneContract.Dt);
        double afterBeat = HaloLuma(SceneContract.Snap(s, cols, rows));
        audio.OnAudio(new AudioFrame(Active: true, Level: 0f, Bass: 0f, Mid: 0f, High: 0f, Beat: false, BeatPulse: 0f, Bpm: 120f));
        SceneContract.Run(s, 30);
        double later = HaloLuma(SceneContract.Snap(s, cols, rows));
        _out.WriteLine($"ореол сразу после удара {afterBeat:F0}, через 0.5 с {later:F0}");
        Assert.True(afterBeat >= 1.2 * later, $"вспышка не видна: {afterBeat:F0} против {later:F0}");
    }

    [Fact]
    public void DaysPerSecMinAndMaxDiffer()
    {
        var a = Render(Opts(new { daysPerSec = 1f, zoom = "outer" }));
        var b = Render(Opts(new { daysPerSec = 365f, zoom = "outer" }));
        double share = ChangedShare(a, b);
        _out.WriteLine($"daysPerSec 1 и 365 отличаются в {share:P1} ячеек");
        Assert.True(share > 0.01, $"Min и Max daysPerSec дают почти одинаковый кадр: {share:P2}");
    }

    [Fact]
    public void LabelsMinAndMaxDiffer()
    {
        var a = Render(Opts(new { labels = false, zoom = "outer" }));
        var b = Render(Opts(new { labels = true, zoom = "outer" }));
        double share = ChangedShare(a, b);
        _out.WriteLine($"labels 0 и 1 отличаются в {share:P1} ячеек");
        Assert.True(share > 0.005, $"Min и Max labels дают почти одинаковый кадр: {share:P2}");
    }

    [Fact]
    public void ZoomMinAndMaxDiffer()
    {
        var a = Render(Opts(new { zoom = "auto" }));
        var b = Render(Opts(new { zoom = "outer" }));
        double share = ChangedShare(a, b);
        _out.WriteLine($"zoom auto и outer отличаются в {share:P1} ячеек");
        Assert.True(share > 0.01, $"Min и Max zoom дают почти одинаковый кадр: {share:P2}");
    }

    private static CharGrid Render(SceneOptions o)
    {
        var s = SceneContract.Make(() => new OrreryScene(), 120, 40, 61, o);
        SceneContract.Run(s, 120);
        return SceneContract.Snap(s, 120, 40);
    }

    /// <summary>Ячейка Земли: синий «•» ровно один на экране; иначе null.</summary>
    private static (int x, int y)? FindEarth(CharGrid g)
    {
        int found = 0, fx = 0, fy = 0;
        for (int y = 0; y < g.Rows; y++)
        for (int x = 0; x < g.Cols; x++)
        {
            var c = g[x, y];
            if (c.Ch == '●' && c.Fg == EarthColor) { found++; fx = x; fy = y; }
        }
        return found == 1 ? (fx, fy) : null;
    }

    /// <summary>Угол ячейки относительно центра схемы (Солнца), градусы; y переведён в ширины ячейки через CellAspect.</summary>
    private static double AngleDeg(int cols, int rows, (int x, int y) p)
    {
        double ox = cols / 2 + 0.5, oy = rows / 2 + 0.5;
        return Math.Atan2(((p.y + 0.5) - oy) * CellAspect, (p.x + 0.5) - ox) * 180.0 / Math.PI;
    }

    private static double Wrap180(double d)
    {
        d %= 360.0;
        if (d > 180.0) d -= 360.0;
        if (d < -180.0) d += 360.0;
        return d;
    }

    /// <summary>Средняя яркость фона в радиусе 5 ячеек от центра (Солнце), суммой.</summary>
    private static double HaloLuma(CharGrid g)
    {
        double ox = g.Cols / 2 + 0.5, oy = g.Rows / 2 + 0.5;
        double sum = 0;
        for (int y = 0; y < g.Rows; y++)
        for (int x = 0; x < g.Cols; x++)
        {
            double dx = x + 0.5 - ox, dy = (y + 0.5 - oy) * CellAspect;
            if (dx * dx + dy * dy > 25) continue;
            var bg = g[x, y].Bg;
            sum += bg.R * 0.299 + bg.G * 0.587 + bg.B * 0.114;
        }
        return sum;
    }

    private static double ChangedShare(CharGrid a, CharGrid b)
    {
        int n = 0;
        for (int i = 0; i < a.Cells.Length; i++)
            if (a.Cells[i].Ch != b.Cells[i].Ch || a.Cells[i].Fg != b.Cells[i].Fg || a.Cells[i].Bg != b.Cells[i].Bg) n++;
        return n / (double)a.Cells.Length;
    }
}
