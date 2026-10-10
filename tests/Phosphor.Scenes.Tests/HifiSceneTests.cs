using System.Text;
using System.Text.Json;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class HifiSceneTests
{
    private const int Cols = 120;
    private const int Rows = 40;
    private const float Dt = 1f / 60f;

    // Геометрия спектра: столбики от x = 6 до правой рамки (w − 2), c0(b) = 6 + b·span/32, span = w − 7.

    private readonly ITestOutputHelper _out;

    public HifiSceneTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new HifiScene(), "hifi");

    [Fact]
    public void SpectrumBarHeightFollowsBandValue()
    {
        var s = Make("{\"face\":\"spectrum\"}");
        var bands = new float[AudioFrame.BandCount];
        for (int b = 0; b < bands.Length; b++) bands[b] = b < 16 ? 0.25f : 0.75f;
        Step(s, 60, bands);
        var g = SceneContract.Snap(s, Cols, Rows);

        int low = LitRows(g, BarX(2));
        int high = LitRows(g, BarX(20));
        _out.WriteLine($"Столбик 0.25: {low} сегментов, столбик 0.75: {high} сегментов");
        Assert.True(high >= low * 2, $"высота не пропорциональна: {low} и {high}");
        Assert.True(high - low >= 10, $"разница слишком мала: {low} и {high}");
    }

    [Fact]
    public void PeakHoldsThenFalls()
    {
        var s = Make("{\"face\":\"spectrum\",\"peakHold\":1.0}");
        var bands = new float[AudioFrame.BandCount];
        int x = BarX(10);

        bands[10] = 0.9f;
        Step(s, 6, bands);
        bands[10] = 0.1f;
        Step(s, 30, bands); // 0.5 с после удара — пик ещё держится (peakHold = 1 с)
        int held = PeakRow(SceneContract.Snap(s, Cols, Rows), x);

        Step(s, 180, bands); // ещё 3 с — пик опустился
        int fallen = PeakRow(SceneContract.Snap(s, Cols, Rows), x);
        _out.WriteLine($"Пик: строка {held} через 0.5 с, строка {fallen} через 3.5 с");

        Assert.True(held >= 0 && held <= 9, $"пик не держится над столбиком: строка {held}");
        Assert.True(fallen > held + 10, $"пик не падает: {held} → {fallen}");
    }

    [Fact]
    public void VuNeedleMovesWithInertia()
    {
        var s = Make("{\"face\":\"vu\"}");
        var hs = (HifiScene)s;
        var bands = new float[AudioFrame.BandCount];
        Assert.InRange(hs.VuLeftDb, -20.01f, -19.99f);

        Step(s, 1, bands, level: 1f);
        float after1 = hs.VuLeftDb;
        _out.WriteLine($"Стрелка после 1 кадра: {after1:F2} VU (цель +3)");
        Assert.True(after1 > -20f && after1 < -15f, $"стрелка скакнула: {after1}");

        Step(s, 60, bands, level: 1f);
        float after1s = hs.VuLeftDb;
        _out.WriteLine($"Стрелка через 1 с: {after1s:F2} VU");
        Assert.True(after1s > 0f, $"стрелка слишком медленная: {after1s}");
    }

    [Theory]
    [InlineData("spectrum", "SPECTRUM ANALYZER")]
    [InlineData("vu", "VU METERS")]
    [InlineData("player", "MEDIA PLAYER")]
    public void FaceParamShowsRequestedFace(string face, string title)
    {
        var s = Make($"{{\"face\":\"{face}\"}}");
        Step(s, 5);
        var g = SceneContract.Snap(s, Cols, Rows);
        Assert.Contains(title, RowText(g, 0));
    }

    [Fact]
    public void AutoCyclesToNextFaceAfterFaceSec()
    {
        var s = Make("{\"faceSec\":20}");
        Step(s, 20 * 60 + 60); // 21 с: переход 0.6 с уже прошёл
        var g = SceneContract.Snap(s, Cols, Rows);
        Assert.Contains("VU METERS", RowText(g, 0));
    }

    [Fact]
    public void WithoutSoundShowsStoppedAndZeroTime()
    {
        var s = Make("{\"face\":\"player\"}");
        Step(s, 60);
        var g = SceneContract.Snap(s, Cols, Rows);
        string all = AllText(g);
        Assert.Contains("STOPPED", all);
        Assert.Contains("00:00", all);
        Assert.False(((HifiScene)s).DemoMode);
    }

    [Fact]
    public void PlayerShowsBpmWhileSounding()
    {
        var s = Make("{\"face\":\"player\"}");
        var bands = new float[AudioFrame.BandCount];
        Step(s, 2, bands, bpm: 128f);
        string all = AllText(SceneContract.Snap(s, Cols, Rows));
        Assert.Contains("128 BPM", all);
        Assert.Contains("PLAYING", all);
    }

    [Fact]
    public void DemoStartsAfterTenSecondsOfSilence()
    {
        var s = Make("{\"face\":\"player\"}");
        var hs = (HifiScene)s;
        Step(s, 9 * 60);
        Assert.False(hs.DemoMode);
        Step(s, 2 * 60);
        Assert.True(hs.DemoMode);
        Assert.Contains("DEMO", AllText(SceneContract.Snap(s, Cols, Rows)));
    }

    /// <summary>Снимок не падает на узких и маленьких сетках во всех лицах.</summary>
    [Theory]
    [InlineData(10, 5)]
    [InlineData(30, 12)]
    [InlineData(200, 80)]
    public void SmallAndLargeGridsDoNotCrash(int cols, int rows)
    {
        foreach (var face in new[] { "spectrum", "vu", "player" })
        {
            var s = Make($"{{\"face\":\"{face}\"}}", cols, rows);
            Step(s, 30, new float[AudioFrame.BandCount], level: 0.5f);
            _ = SceneContract.Snap(s, cols, rows);
        }
    }

    // ---------- помощники ----------

    /// <summary>Левый столбец полосы b при ширине сетки 120 (см. DrawSpectrum: c0 = x0 + b·span/32).</summary>
    private static int BarX(int b) => 6 + b * (Cols - 7) / AudioFrame.BandCount;

    private static SceneOptions Opts(string json) => new() { Params = JsonDocument.Parse(json).RootElement };

    private static IScene Make(string json, int cols = Cols, int rows = Rows) =>
        SceneContract.Make(() => new HifiScene(), cols, rows, 7, Opts(json));

    /// <summary>Кадр звука: OnAudio, затем Update. bands = null — без спектра.</summary>
    private static void Step(IScene s, int frames, float[]? bands = null, float level = 0f, float bpm = 0f)
    {
        var reactive = (IAudioReactive)s;
        for (int i = 0; i < frames; i++)
        {
            if (bands is not null)
            {
                var f = new AudioFrame(true, level, level, level * 0.6f, level * 0.4f, false, 0f, bpm) { Bands = bands };
                reactive.OnAudio(f);
            }
            s.Update(Dt);
        }
    }

    private static string RowText(CharGrid g, int y)
    {
        var sb = new StringBuilder(g.Cols);
        for (int x = 0; x < g.Cols; x++) sb.Append(g[x, y].Ch);
        return sb.ToString();
    }

    private static string AllText(CharGrid g)
    {
        var sb = new StringBuilder(g.Cols * g.Rows);
        for (int y = 0; y < g.Rows; y++) sb.Append(RowText(g, y)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Число светящихся сегментов в столбце (яркие ячейки блока, не тёмная подложка).</summary>
    private static int LitRows(CharGrid g, int x)
    {
        int n = 0;
        for (int y = 0; y < g.Rows; y++)
        {
            var c = g[x, y];
            if (c.Ch == '▄' && c.Fg.R + c.Fg.G + c.Fg.B > 200) n++;
        }
        return n;
    }

    /// <summary>Верхняя строка с белой меткой пика в столбце; −1 — нет.</summary>
    private static int PeakRow(CharGrid g, int x)
    {
        for (int y = 0; y < g.Rows; y++)
        {
            var c = g[x, y];
            if (c.Ch == '▄' && c.Fg.R >= 250 && c.Fg.G >= 245 && c.Fg.B >= 230) return y;
        }
        return -1;
    }
}
