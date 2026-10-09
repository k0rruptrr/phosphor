using System.Diagnostics;
using System.Text.Json;
using Phosphor.Core;
using Phosphor.Scenes;

namespace Phosphor.Scenes.Tests;

public sealed class LifeSceneTests
{
    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new LifeScene(), "life");

    [Fact]
    public void BlinkerHasPeriodTwo()
    {
        var a = Field(5, 5, (2, 1), (2, 2), (2, 3));
        var b = new byte[25];
        LifeScene.Step(a, b, 5, 5);
        Assert.Equal(Field(5, 5, (1, 2), (2, 2), (3, 2)), b);
        var c = new byte[25];
        LifeScene.Step(b, c, 5, 5);
        Assert.Equal(a, c);
    }

    [Fact]
    public void BlockIsStillLife()
    {
        var a = Field(6, 6, (1, 1), (2, 1), (1, 2), (2, 2));
        var b = new byte[36];
        LifeScene.Step(a, b, 6, 6);
        Assert.Equal(a, b);
    }

    [Fact]
    public void GliderMovesByOneOneEveryFourGenerations()
    {
        var glider = new[] { (1, 0), (2, 1), (0, 2), (1, 2), (2, 2) };
        var a = Field(10, 10, glider);
        var shifted = Field(10, 10, glider.Select(p => ((p.Item1 + 1) % 10, (p.Item2 + 1) % 10)).ToArray());

        var cur = a;
        var next = new byte[100];
        for (int i = 0; i < 4; i++)
        {
            LifeScene.Step(cur, next, 10, 10);
            (cur, next) = (next, cur);
        }
        Assert.Equal(shifted, cur);
    }

    [Fact]
    public void TorusWrapsAcrossEdges()
    {
        // Горизонтальный «блинкер», переходящий через правый край: (4,2),(0,2),(1,2) на поле 5×5.
        var a = Field(5, 5, (4, 2), (0, 2), (1, 2));
        var b = new byte[25];
        LifeScene.Step(a, b, 5, 5);
        Assert.Equal(Field(5, 5, (0, 1), (0, 2), (0, 3)), b);
    }

    [Fact]
    public void RestartsWithinTenMinutesOn40x15()
    {
        var s = SceneContract.Make(() => new LifeScene(), 40, 15, 1);
        for (int i = 0; i < 60 * 60 * 10; i++) s.Update(SceneContract.Dt);
        Assert.True(((LifeScene)s).Restarts > 0, "за 10 минут симуляции не было ни одного перезапуска");
    }

    [Fact]
    public void ScreenIsNotEmptyThreeSecondsAfterRestart()
    {
        var life = new LifeScene();
        var s = SceneContract.Make(() => life, 40, 15, 1);

        int before = life.Restarts;
        int limit = 60 * 60 * 10;
        for (int i = 0; i < limit && life.Restarts == before; i++) s.Update(SceneContract.Dt);
        Assert.NotEqual(before, life.Restarts);

        SceneContract.Run(s, 180); // 3 с после смены Restarts
        var g = SceneContract.Snap(s, 40, 15);
        Assert.Contains(g.Cells, c => c.Fg != Rgba.Black);
    }

    [Fact]
    public void UpdateAndDrawAre_Fast_On240x70()
    {
        var s = SceneContract.Make(() => new LifeScene(), 240, 70, 31);
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

    /// <summary>Поле w×h с живыми клетками в указанных координатах.</summary>
    private static byte[] Field(int w, int h, params (int X, int Y)[] alive)
    {
        var f = new byte[w * h];
        foreach (var (x, y) in alive) f[y * w + x] = 1;
        return f;
    }
}
