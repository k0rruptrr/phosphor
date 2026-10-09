using System.Text;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class DvdSceneTests
{
    private readonly ITestOutputHelper _out;

    public DvdSceneTests(ITestOutputHelper output) => _out = output;

    private const int TenMinFrames = 10 * 60 * 60;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new DvdScene(), "dvd");

    [Fact]
    public void LogoStaysFullyOnScreen()
    {
        var s = new DvdScene();
        s.Init(80, 25, new Random(41), new SceneOptions());
        var g = new CharGrid(80, 25);
        for (int i = 0; i < TenMinFrames; i++)
        {
            s.Update(SceneContract.Dt);
            if (i % 10 != 0) continue;
            s.Draw(g);
            Assert.True(s.X >= 0 && s.X + s.LogoWidth <= 80, $"кадр {i}: X={s.X}, ширина {s.LogoWidth}");
            Assert.True(s.Y >= 0 && s.Y + s.LogoHeight <= 25, $"кадр {i}: Y={s.Y}, высота {s.LogoHeight}");
        }
        Assert.True(s.Bounces > 0, "за 10 минут ни одного отскока");
    }

    [Fact]
    public void BouncesGrowAndLogoChangesColorOnBounce()
    {
        var s = new DvdScene();
        s.Init(80, 25, new Random(42), new SceneOptions());
        var g = new CharGrid(80, 25);
        s.Draw(g);
        Rgba before = g[s.X, s.Y].Fg;

        int b0 = s.Bounces;
        for (int i = 0; i < 60 * 60 && s.Bounces == b0; i++) s.Update(SceneContract.Dt);
        Assert.True(s.Bounces > b0, "первый отскок не случился за минуту");

        s.Draw(g);
        Assert.NotEqual(before, g[s.X, s.Y].Fg);

        int b1 = s.Bounces;
        SceneContract.Run(s, 9 * 60 * 60);
        Assert.True(s.Bounces > b1, "счётчик отскоков не растёт");
    }

    [Fact]
    public void AmberPaletteGivesShadesOfAmber()
    {
        var o = new SceneOptions { Palette = "amber" };
        var s = new DvdScene();
        s.Init(80, 25, new Random(43), o);
        var g = new CharGrid(80, 25);
        int b0 = s.Bounces;
        for (int i = 0; i < 60 * 60 && s.Bounces == b0; i++) s.Update(SceneContract.Dt);
        Assert.True(s.Bounces > b0, "первый отскок не случился за минуту");
        s.Draw(g);
        Rgba c = g[s.X, s.Y].Fg;
        Assert.Equal(0, c.B);
        Assert.True(c.R > c.G && c.G > 0, $"цвет {c} не оттенок янтаря");
    }

    [Fact]
    public void CornersHappenOnSeeds1to5()
    {
        int total = 0;
        for (int seed = 1; seed <= 5; seed++)
        {
            var s = new DvdScene();
            s.Init(120, 40, new Random(seed), new SceneOptions());
            SceneContract.Run(s, TenMinFrames);
            _out.WriteLine($"seed {seed}: углов за 10 мин = {s.Corners}");
            Assert.True(s.Corners >= 1, $"seed {seed}: ни одного угла за 10 минут");
            total += s.Corners;
        }
        _out.WriteLine($"среднее время между углами на 120x40: {(5 * 600.0) / total:F0} с");
    }

    /// <summary>Первый угол на 192x60 для seed 1..20: сцена стоит на кадре угла.</summary>
    private static DvdScene? FirstCorner(out int seedUsed, out int frame)
    {
        DvdScene? found = null;
        seedUsed = 0;
        frame = 0;
        for (int seed = 1; seed <= 20 && found == null; seed++)
        {
            var s = new DvdScene();
            s.Init(192, 60, new Random(seed), new SceneOptions());
            for (int i = 1; i <= TenMinFrames; i++)
            {
                s.Update(SceneContract.Dt);
                if (s.Corners >= 1)
                {
                    found = s;
                    seedUsed = seed;
                    frame = i;
                    break;
                }
            }
        }
        return found;
    }

    [Fact]
    public void PartyShowsCornerText()
    {
        var party = FirstCorner(out int seed, out int frame);
        Assert.NotNull(party);
        _out.WriteLine($"первый угол на 192x60: seed {seed}, кадр {frame}");

        SceneContract.Run(party!, 30);
        var g = new CharGrid(192, 60);
        party!.Draw(g);
        string screen = ToText(g);
        Assert.Contains("CORNER", screen);
        Assert.Contains("CORNER #1", screen);
    }

    [Fact]
    public void PartyShowsBigBlockTextInCenter()
    {
        var s = FirstCorner(out _, out _);
        Assert.NotNull(s);
        var party = s!;
        var g = new CharGrid(192, 60);
        // Надпись мигает: проверяем, что за первые 4 с праздника в центральной трети есть '█'.
        bool seen = false;
        for (int i = 0; i < 40 && !seen; i++)
        {
            SceneContract.Run(party, 6);
            party.Draw(g);
            for (int y = g.Rows / 3; y < 2 * g.Rows / 3 && !seen; y++)
                for (int x = g.Cols / 3; x < 2 * g.Cols / 3 && !seen; x++)
                    if (g[x, y].Ch == '█') seen = true;
        }
        Assert.True(seen, "во время праздника в центре нет большой надписи из '█'");
    }

    [Fact]
    public void TinyGridShowsDvdFragment()
    {
        var s = new DvdScene();
        s.Init(10, 3, new Random(51), new SceneOptions());
        var g = new CharGrid(10, 3);
        bool seen = false;
        for (int i = 0; i < 600; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
            if (g.Cells.Any(c => "[DVD]".Contains(c.Ch))) seen = true;
        }
        Assert.True(seen, "на сетке 10x3 не нарисовалось ни одного символа [DVD]");
    }

    private static string ToText(CharGrid g)
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
