using Phosphor.Core;

namespace Phosphor.Scenes.Tests;

public sealed class BoingSceneTests
{
    private const int Cols = 120, Rows = 40;
    private static readonly Rgba Purple = Rgba.Hex(0xAA00AA);
    private static readonly Rgba Shadow = Rgba.Hex(0x555555);

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new BoingScene(), "boing");

    [Fact]
    public void BallTouchesFloorAtLeastTwiceIn5Seconds()
    {
        var s = Make(11);
        SceneContract.Run(s, 300);
        Assert.True(s.FloorContacts >= 2, $"за 5 с касаний пола: {s.FloorContacts}");
    }

    [Fact]
    public void BallHasRedPixels()
    {
        var s = Make(12);
        SceneContract.Run(s, 60);
        var g = SceneContract.Snap(s, Cols, Rows);
        Assert.Contains(g.Cells, c => IsRed(c.Fg) || IsRed(c.Bg));
    }

    [Fact]
    public void GridIsPurple()
    {
        var s = Make(14);
        SceneContract.Run(s, 60);
        var g = SceneContract.Snap(s, Cols, Rows);
        Assert.Contains(g.Cells, c => c.Fg == Purple || c.Bg == Purple);
    }

    [Fact]
    public void FloorContactsFollowMusicBeatsAt120Bpm()
    {
        const double dt = 1.0 / 60, beat = 0.5;
        var s = Make(13);
        var contacts = new List<double>();
        int last = s.FloorContacts;
        for (int i = 0; i < 8 * 60; i++)
        {
            double t0 = i * dt, t1 = (i + 1) * dt;
            bool isBeat = Math.Floor(t1 / beat) > Math.Floor(t0 / beat);
            s.OnAudio(new AudioFrame(true, 0.6f, 0.8f, 0.5f, 0.3f, isBeat, isBeat ? 1f : 0f, 120f));
            s.Update((float)dt);
            if (s.FloorContacts != last)
            {
                contacts.Add(t1);
                last = s.FloorContacts;
            }
        }

        var late = contacts.Where(t => t >= 4.0).ToArray();
        Assert.True(late.Length >= 2, $"после 4 с касаний: {late.Length}");
        foreach (var t in late)
        {
            double nearest = Math.Round(t / beat) * beat;
            Assert.True(Math.Abs(t - nearest) < 0.08, $"касание {t:F3} с далеко от удара {nearest:F3}");
        }
    }

    [Fact]
    public void FloorIsMostlyGreyLinesNotPurpleFill()
    {
        var s = Make(15);
        SceneContract.Run(s, 300);
        var g = SceneContract.Snap(s, Cols, Rows);
        int h = Rows * 2, floor = (int)(h * 0.8f);
        int purple = 0, total = 0;
        for (int py = floor; py < h; py++)
            for (int px = 0; px < Cols; px++)
            {
                total++;
                if (PixelAt(g, px, py) == Purple) purple++;
            }
        double share = purple / (double)total;
        Assert.True(share < 0.35, $"в зоне пола фиолетовых {share:P0}");
    }

    [Fact]
    public void ShadowIsDarkGreyDiscRightOfBall()
    {
        var s = Make(16);
        var g = new CharGrid(Cols, Rows);
        bool found = false;
        for (int i = 0; i < 600 && !found; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
            int px = (int)MathF.Round(s.BallX + 1.2f * s.BallRadius);
            int py = (int)MathF.Round(s.BallY);
            if ((uint)px >= Cols || (uint)py >= Rows * 2) continue;
            found = PixelAt(g, px, py) == Shadow;
        }
        Assert.True(found, "справа от шара на его высоте нет тёмно-серой тени");
    }

    private static Rgba PixelAt(CharGrid g, int px, int py)
    {
        var c = g[px, py / 2];
        return py % 2 == 0 ? c.Fg : c.Bg;
    }

    private static BoingScene Make(int seed)
    {
        var s = new BoingScene();
        s.Init(Cols, Rows, new Random(seed), new SceneOptions());
        return s;
    }

    private static bool IsRed(Rgba c) => c.R > 150 && c.G < 60 && c.B < 60;
}
