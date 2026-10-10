using System.Diagnostics;
using System.Text.Json;
using Phosphor.Core;
using Phosphor.Scenes;

namespace Phosphor.Scenes.Tests;

public sealed class DroneSceneTests
{
    private const int Cols = 96, Rows = 30;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new DroneScene(), "drone");

    [Fact]
    public void FlightKeepsFourMetersFromWallsAndNeverEntersThem()
    {
        var s = (DroneScene)SceneContract.Make(() => new DroneScene(), Cols, Rows, 21);
        float minWall = float.MaxValue;
        for (int i = 0; i < 120 * 60; i++)
        {
            s.Update(SceneContract.Dt);
            if (s.InsideAt(s.CameraX, s.CameraY, s.CameraZ))
                Assert.Fail($"кадр {i}: камера внутри дома ({s.CameraX:F1}, {s.CameraY:F1}, {s.CameraZ:F1})");
            minWall = MathF.Min(minWall, s.WallDistance(s.CameraX, s.CameraY, s.CameraZ));
        }
        Assert.Equal(0, s.PullUpCount);
        Assert.True(minWall >= 4f - 1e-3f, $"минимальный зазор до стены {minWall:F2} м, нужно не меньше 4");
    }

    [Fact]
    public void DroneGoesOut100To400MetersAndReturns()
    {
        var s = (DroneScene)SceneContract.Make(() => new DroneScene(), 16, 8, 5);
        float maxDist = 0f;
        int maxAt = -1;
        bool returned = false;
        for (int i = 0; i < 300 * 60; i++)
        {
            s.Update(SceneContract.Dt);
            float d = s.PilotDistance;
            if (d > maxDist) { maxDist = d; maxAt = i; }
            if (maxAt >= 0 && i > maxAt && d < 60f) returned = true;
        }
        Assert.True(maxDist >= 100f, $"дальше всего дрон уходил на {maxDist:F0} м, нужно не меньше 100");
        Assert.True(maxDist <= 420f, $"дрон ушёл на {maxDist:F0} м, больше 400 м");
        Assert.True(returned, "после дальнего района дрон не вернулся к пилоту (ближе 60 м)");
    }

    [Fact]
    public void SignalBehindHouseIsWorseThanDirectAtSameDistance()
    {
        var s = (DroneScene)SceneContract.Make(() => new DroneScene(), Cols, Rows, 9);
        const float radius = 20f, height = 6f;
        float? direct = null, blocked = null;
        float dDirect = 0f, dBlocked = 0f;
        for (int deg = 0; deg < 360; deg += 2)
        {
            float a = deg * MathF.PI / 180f;
            float x = 0.5f + radius * MathF.Cos(a), y = 0.5f + radius * MathF.Sin(a);
            if (s.InsideAt(x, y, height)) continue;
            bool isBlocked = s.LineBlockedFromPilot(x, y, height);
            if (isBlocked && blocked == null) { blocked = s.SignalAt(x, y, height); dBlocked = deg; }
            if (!isBlocked && direct == null) { direct = s.SignalAt(x, y, height); dDirect = deg; }
        }
        Assert.True(direct.HasValue, "не найдено точки с прямой видимостью на дистанции 20");
        Assert.True(blocked.HasValue, "не найдено точки за домом на дистанции 20");
        Assert.True(blocked < direct, $"сигнал за домом {blocked} не ниже прямой видимости {direct} (углы {dBlocked} и {dDirect})");

        // Доля снежных клеток выше за домом: дрон ставится в обе точки, сигнал сходится, кадры сравниваются.
        var sDirect = (DroneScene)SceneContract.Make(() => new DroneScene(), Cols, Rows, 9);
        var sBlocked = (DroneScene)SceneContract.Make(() => new DroneScene(), Cols, Rows, 9);
        float ad = dDirect * MathF.PI / 180f, ab = dBlocked * MathF.PI / 180f;
        int snowDirect = CountSnow(sDirect, 0.5f + radius * MathF.Cos(ad), 0.5f + radius * MathF.Sin(ad), height);
        int snowBlocked = CountSnow(sBlocked, 0.5f + radius * MathF.Cos(ab), 0.5f + radius * MathF.Sin(ab), height);
        Assert.True(snowBlocked > snowDirect, $"снега за домом {snowBlocked}, при прямой видимости {snowDirect}");
    }

    [Fact]
    public void OsdDrawsBigGlyphsOnlyWhenOn()
    {
        var on = SceneContract.Make(() => new DroneScene(), Cols, Rows, 3);
        SceneContract.Run(on, 60);
        Assert.True(CountOsdGlyphs(SceneContract.Snap(on, Cols, Rows)) > 40, "крупный OSD не нарисован");

        var o = new SceneOptions { Params = JsonDocument.Parse("{\"osd\": false}").RootElement };
        var off = SceneContract.Make(() => new DroneScene(), Cols, Rows, 3, o);
        SceneContract.Run(off, 60);
        Assert.Equal(0, CountOsdGlyphs(SceneContract.Snap(off, Cols, Rows)));
    }

    [Fact]
    public void BankIsVisibleDuringOrbit()
    {
        var s = (DroneScene)SceneContract.Make(() => new DroneScene(), Cols, Rows, 14);
        int guard = 0;
        while (s.ManeuverName != "orbit" && guard++ < 600 * 60) s.Update(SceneContract.Dt);
        Assert.Equal("orbit", s.ManeuverName);
        float sum = 0f;
        for (int i = 0; i < 30; i++)
        {
            s.Update(SceneContract.Dt);
            sum += MathF.Abs(s.Roll);
        }
        Assert.True(sum / 30f > 0.2f, $"средний крен на облёте {sum / 30f:F2} рад, нужно больше 0.2");
    }

    [Fact]
    public void RendersUnder6msPerFrameOn213x60()
    {
        var s = SceneContract.Make(() => new DroneScene(), 213, 60, 31);
        var g = new CharGrid(213, 60);
        for (int i = 0; i < 60; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
        }
        const int frames = 300;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
        }
        sw.Stop();
        double ms = sw.Elapsed.TotalMilliseconds / frames;
        Assert.True(ms <= 6.0, $"в среднем {ms:F2} мс на кадр (Update+Draw), нужно не больше 6");
    }

    private static int CountSnow(DroneScene s, float x, float y, float z)
    {
        s.DebugPlaceDrone(x, y, z);
        var g = new CharGrid(Cols, Rows);
        int snow = 0;
        for (int f = 0; f < 150; f++) s.Update(SceneContract.Dt);
        for (int f = 0; f < 20; f++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
            snow += g.Cells.Count(c => c.Ch == '░' || c.Ch == '▒');
        }
        return snow;
    }

    private static int CountOsdGlyphs(CharGrid g) => g.Cells.Count(c => GlyphSet.IsSextant(c.Ch));
}
