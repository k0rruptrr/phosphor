using System.Diagnostics;
using Phosphor.Core;

namespace Phosphor.Scenes.Tests;

public sealed class PipesSceneTests
{
    private const string PipeGlyphs = "═║╔╗╚╝╬■";

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new PipesScene(), "pipes");

    [Fact]
    public void ConnectTableBothWays()
    {
        var cases = new (PipesScene.Side a, PipesScene.Side b, char ch)[]
        {
            (PipesScene.Side.W, PipesScene.Side.E, '═'),
            (PipesScene.Side.N, PipesScene.Side.S, '║'),
            (PipesScene.Side.E, PipesScene.Side.S, '╔'),
            (PipesScene.Side.W, PipesScene.Side.S, '╗'),
            (PipesScene.Side.E, PipesScene.Side.N, '╚'),
            (PipesScene.Side.W, PipesScene.Side.N, '╝'),
        };
        foreach (var (a, b, ch) in cases)
        {
            Assert.Equal(ch, PipesScene.Connect(a, b));
            Assert.Equal(ch, PipesScene.Connect(b, a));
        }
    }

    [Fact]
    public void OnlyPipeGlyphsAfterFiveSeconds()
    {
        var s = SceneContract.Make(() => new PipesScene(), 120, 40, 41);
        SceneContract.Run(s, 300);
        var g = SceneContract.Snap(s, 120, 40);
        var bad = g.Cells.Select(c => c.Ch)
            .Where(c => c != ' ' && !PipeGlyphs.Contains(c))
            .Distinct().ToArray();
        Assert.True(bad.Length == 0, $"Лишние символы: {string.Join(" ", bad)}");
    }

    [Fact]
    public void HorizontalAndVerticalSegmentsAreConnected()
    {
        const int cols = 120, rows = 40;
        var s = SceneContract.Make(() => new PipesScene(), cols, rows, 42);
        SceneContract.Run(s, 300);
        var g = SceneContract.Snap(s, cols, rows);

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                char ch = g[x, y].Ch;
                if (ch == '═')
                {
                    bool left = x == 0 || g[x - 1, y].Ch != ' ';
                    bool right = x == cols - 1 || g[x + 1, y].Ch != ' ';
                    Assert.True(left || right, $"'═' в ({x},{y}) не стыкуется ни слева, ни справа");
                }
                else if (ch == '║')
                {
                    bool up = y == 0 || g[x, y - 1].Ch != ' ';
                    bool down = y == rows - 1 || g[x, y + 1].Ch != ' ';
                    Assert.True(up || down, $"'║' в ({x},{y}) не стыкуется ни сверху, ни снизу");
                }
            }
        }
    }

    [Fact]
    public void RoundClearsAfterFillingUp()
    {
        const int cols = 60, rows = 20;
        var s = SceneContract.Make(() => new PipesScene(), cols, rows, 43);
        var g = new CharGrid(cols, rows);
        bool wasHigh = false, cleared = false;
        const int frames = 5 * 60 * 60; // 5 минут при 60 кадрах в секунду
        for (int i = 0; i < frames && !cleared; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
            int used = g.Cells.Count(c => c.Ch != ' ');
            double occ = used / (double)g.Cells.Length;
            if (occ > 0.40) wasHigh = true;
            if (wasHigh && occ < 0.05) cleared = true;
        }
        Assert.True(cleared, "за 5 минут занятость не упала ниже 5% после подъёма выше 40%");
    }

    [Fact]
    public void UpdateAndDrawAre_Fast_On240x70()
    {
        var s = SceneContract.Make(() => new PipesScene(), 240, 70, 44);
        var g = new CharGrid(240, 70);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 600; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 600, $"600 кадров заняли {sw.ElapsedMilliseconds} мс");
    }
}
