using System.Diagnostics;
using Phosphor.Core;
using Phosphor.Scenes;

namespace Phosphor.Scenes.Tests;

public sealed class VoxelSceneTests
{
    private const int Cols = 96, Rows = 30;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new VoxelScene(), "voxel");

    [Fact]
    public void SkyAboveAndTerrainBelowAfterTwoSeconds()
    {
        var s = SceneContract.Make(() => new VoxelScene(), Cols, Rows, 11);
        SceneContract.Run(s, 120);
        var g = SceneContract.Snap(s, Cols, Rows);

        int skyRows = (int)(Rows * 0.2f);
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Cols; x++)
            {
                var c = g[x, y];
                // HUD (строка 2) и перекрестие в центре — текст поверх кадра, остальные ячейки — полублоки.
                bool overlay = y == 2 || (x == Cols / 2 && y == Rows / 2);
                if (!overlay && c.Ch != '▀') Assert.Fail($"ячейка ({x},{y}) = '{c.Ch}', ожидался '▀'");
            }
        }

        for (int y = 0; y < skyRows; y++)
        {
            if (y == 2) continue; // строка HUD
            for (int x = 0; x < Cols; x++)
            {
                var c = g[x, y];
                if (IsGreenish(c.Fg) || IsGreenish(c.Bg))
                    Assert.Fail($"в небе (строка {y}) зелёный цвет в ячейке ({x},{y}): Fg={c.Fg} Bg={c.Bg}");
            }
        }

        int terrain = 0;
        for (int y = (int)(Rows * 0.7f); y < Rows; y++)
        {
            for (int x = 0; x < Cols; x++)
            {
                var c = g[x, y];
                if (IsGreenish(c.Fg) || IsGreenish(c.Bg) || IsBrownish(c.Fg) || IsBrownish(c.Bg)) terrain++;
            }
        }
        Assert.True(terrain > 0, "в нижних 30% строк нет ни зелёных, ни коричневых цветов рельефа");
    }

    [Fact]
    public void CameraNeverSinksIntoTerrain()
    {
        var s = (VoxelScene)SceneContract.Make(() => new VoxelScene(), Cols, Rows, 21);
        for (int i = 0; i < 120 * 60; i++)
        {
            s.Update(SceneContract.Dt);
            if (!(s.CameraHeight > s.GroundUnderCamera))
                Assert.Fail($"кадр {i}: CameraHeight={s.CameraHeight} <= GroundUnderCamera={s.GroundUnderCamera}");
        }
    }

    [Fact]
    public void FramesChangeOverTime()
    {
        var s = SceneContract.Make(() => new VoxelScene(), Cols, Rows, 5);
        SceneContract.Run(s, 60);
        var a = SceneContract.Snap(s, Cols, Rows);
        SceneContract.Run(s, 60);
        var b = SceneContract.Snap(s, Cols, Rows);
        Assert.False(a.Cells.SequenceEqual(b.Cells), "через 1 с кадр не изменился");
    }

    [Fact]
    public void Renders600FramesOn192x60Fast()
    {
        var s = SceneContract.Make(() => new VoxelScene(), 192, 60, 31);
        var g = new CharGrid(192, 60);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 600; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1200, $"600 кадров заняли {sw.ElapsedMilliseconds} мс");
    }

    [Fact]
    public void WaterIsOftenInFrame()
    {
        var s = SceneContract.Make(() => new VoxelScene(), Cols, Rows, 41);
        var g = new CharGrid(Cols, Rows);
        int frames = 120 * 60, good = 0;
        for (int i = 0; i < frames; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
            int water = 0;
            foreach (var c in g.Cells)
            {
                if (IsWater(c.Fg)) water++;
                if (IsWater(c.Bg)) water++;
            }
            if (water * 100 >= 3 * 2 * g.Cells.Length) good++;
        }
        double share = good / (double)frames;
        Assert.True(share >= 0.2, $"кадров с водой (≥3% пикселей) {share:P0}, нужно не меньше 20%");
    }

    private static bool IsWater(Rgba c) => c.B >= 70 && c.B > c.R * 2 && c.G > c.R;

    private static bool IsGreenish(Rgba c) => c.G > c.R && c.G > c.B;

    private static bool IsBrownish(Rgba c) => c.R > c.G && c.G > c.B && c.R - c.B > 25;
}
