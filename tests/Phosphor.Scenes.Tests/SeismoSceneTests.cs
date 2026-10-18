using System.Diagnostics;
using System.Text.Json;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class SeismoSceneTests
{
    private readonly ITestOutputHelper _out;

    public SeismoSceneTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new SeismoScene(), "seismo");

    [Fact]
    public void NoMusicIsAlive()
    {
        var s = SceneContract.Make(() => new SeismoScene(), 120, 40, 81);
        SceneContract.Run(s, 60);
        var a = SceneContract.Snap(s, 120, 40);
        SceneContract.Run(s, 180);                       // 3 с без музыки
        var b = SceneContract.Snap(s, 120, 40);
        double share = ChangedShare(a, b);
        _out.WriteLine($"без музыки за 3 с изменилось {share:P0} ячеек");
        Assert.True(share > 0.05, $"за 3 с изменилось лишь {share:P1} ячеек");
    }

    [Fact]
    public void Fast_On192x60()
    {
        var s = SceneContract.Make(() => new SeismoScene(), 192, 60, 31);
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
    public void QuakeSWaveDominatesBackgroundOnNearStation()
    {
        // M4.5, ближняя станция (канал 0, D=120 км): пик S должен быть не меньше 4 пиков фона.
        var s = (SeismoScene)SceneContract.Make(() => new SeismoScene(), 160, 40, 101, Opts("{\"quakeRate\":0,\"channels\":5}"));
        SceneContract.Run(s, 120);
        s.ForceQuake(4.5f, 120f);
        double t0 = s.TimeAt(s.SubColumns - 1);
        SceneContract.Run(s, 720);                       // 12 с после начала
        double sArr = s.SArrival(0);
        double bg = PeakAbs(s, 0, double.MinValue, t0 - 0.2);   // весь фон до события
        double sPeak = PeakAbs(s, 0, sArr, sArr + 6.0);  // S и начало хвоста
        double ratio = sPeak / bg;
        _out.WriteLine($"M4.5, D120, канал 0: фон {bg:F3}, пик S {sPeak:F3}, отношение {ratio:F1}");
        Assert.True(ratio >= 4.0, $"пик S лишь в {ratio:F1} раза выше пика фона");
    }

    [Fact]
    public void StrongQuakeHitsTheBandEdgeOnNearStation()
    {
        // M5, D60, канал 0: пик S и хвоста у края полосы (≥0.9 полуполосы).
        var s = (SeismoScene)SceneContract.Make(() => new SeismoScene(), 160, 40, 104, Opts("{\"quakeRate\":0,\"channels\":5}"));
        SceneContract.Run(s, 120);
        s.ForceQuake(5.0f, 60f);
        SceneContract.Run(s, 720);
        double sArr = s.SArrival(0);
        double peak = PeakAbs(s, 0, sArr, sArr + 8.0);
        _out.WriteLine($"M5, D60, канал 0: пик S и хвоста {peak:F2} полуполосы");
        Assert.True(peak >= 0.9, $"пик {peak:F2} полуполосы, у края не дошло");
    }

    [Fact]
    public void BackgroundNoiseIsSmallAndNotPeriodic()
    {
        var s = (SeismoScene)SceneContract.Make(() => new SeismoScene(), 120, 40, 105, Opts("{\"quakeRate\":0,\"channels\":5}"));
        SceneContract.Run(s, 600);                       // 10 с
        double peak = PeakAbs(s, 0, double.MinValue, double.MaxValue);
        // 5 каналов на 38 строк: полоса канала 0 — 7 строк, полуполоса 3.5 строки.
        double rows = peak * 3.5;
        _out.WriteLine($"фон канала 0 за 10 с: пик {peak:F3} полуполосы ≈ {rows:F2} строки");
        Assert.True(rows <= 0.5 && rows >= 0.2, $"пик фона {rows:F2} строки, нужно 0.2–0.5");

        // Не периодический: соседние полупериоды 1-го синуса не повторяются — проверяем, что пик не строго кратен периоду.
        double a = PeakAbs(s, 0, s.TimeAt(0), s.TimeAt(0) + 4.0);
        double b = PeakAbs(s, 0, s.TimeAt(0) + 4.0, s.TimeAt(0) + 8.0);
        _out.WriteLine($"пик фона в двух 4-секундных окнах: {a:F3} и {b:F3}");
        Assert.NotEqual(Math.Round(a, 3), Math.Round(b, 3));
    }

    [Fact]
    public void NearStationPWaveIsTwoTimesBackground()
    {
        var s = (SeismoScene)SceneContract.Make(() => new SeismoScene(), 160, 40, 102, Opts("{\"quakeRate\":0,\"channels\":5}"));
        SceneContract.Run(s, 120);
        double bg = PeakAbs(s, 0, s.TimeAt(0), s.TimeAt(s.SubColumns - 1));
        s.ForceQuake(5.0f, 200f);
        double t0 = s.TimeAt(s.SubColumns - 1);
        SceneContract.Run(s, 720);
        double near = s.PArrival(0);
        double far = s.PArrival(s.Channels - 1);
        _out.WriteLine($"P на канале 0: +{near - t0:F2} с, на дальнем канале {s.Channels - 1}: +{far - t0:F2} с");
        Assert.True(far - near >= 1.5, $"дальний канал отстаёт лишь на {far - near:F2} с");
        double pPeak = PeakAbs(s, 0, near, near + 0.8);
        double ratio = pPeak / bg;
        _out.WriteLine($"P на канале 0: пик {pPeak:F3}, фон {bg:F3}, отношение {ratio:F1}");
        Assert.True(ratio >= 2.0, $"P в {ratio:F1} раза выше фона, нужно 2–3");
    }

    [Fact]
    public void ChannelCountSetsLabelCount()
    {
        foreach (int n in new[] { 2, 8 })
        {
            var s = SceneContract.Make(() => new SeismoScene(), 120, 40, 103, Opts($"{{\"channels\":{n}}}"));
            SceneContract.Run(s, 30);
            var g = SceneContract.Snap(s, 120, 40);
            int labels = 0;
            for (int y = 0; y < g.Rows; y++)
            {
                var row = new char[12];
                for (int x = 0; x < row.Length; x++) row[x] = g[x, y].Ch;
                if (new string(row).Contains("BH")) labels++;
            }
            _out.WriteLine($"channels={n}: подписей каналов {labels}");
            Assert.Equal(n, labels);
        }
    }

    [Fact]
    public void JoltIsLockedToTheBeat()
    {
        var s = SceneContract.Make(() => new SeismoScene(), 160, 40, 111, Opts("{\"quakeRate\":0}"));
        var audio = (IAudioReactive)s;
        var seis = (SeismoScene)s;
        SceneContract.Run(s, 60);
        const int beatEvery = 30;                        // 0.5 с при 60 кадрах/с
        double early = 0, late = 0;
        int n = 0;
        for (int f = 0; f < 10 * beatEvery; f++)
        {
            int phase = f % beatEvery;
            float sinceMs = phase * 1000f / 60f;
            float pulse = MathF.Exp(-sinceMs / 150f);
            audio.OnAudio(new AudioFrame(Active: true, Level: 0.5f, Bass: 0.8f, Mid: 0f, High: 0f,
                Beat: phase == 0, BeatPulse: pulse, Bpm: 120f));
            s.Update(SceneContract.Dt);
            if (phase == 0) { early += seis.Jolt; n++; }
            else if (phase == 25) late += seis.Jolt;   // ~0.42 с после удара
        }
        early /= n;
        late /= n;
        _out.WriteLine($"толчок сразу после удара {early:F2}, через ~0.4 с {late:F2}");
        Assert.True(early >= 2.0 * late, $"толчок не привязан к удару: {early:F2} против {late:F2}");

        // Слабый бас не даёт толчка.
        var quiet = (SeismoScene)SceneContract.Make(() => new SeismoScene(), 160, 40, 112, Opts("{\"quakeRate\":0}"));
        ((IAudioReactive)quiet).OnAudio(new AudioFrame(Active: true, Level: 0.2f, Bass: 0.2f, Mid: 0f, High: 0f,
            Beat: true, BeatPulse: 1f, Bpm: 120f));
        Assert.Equal(0f, quiet.Jolt);
    }

    [Fact]
    public void EveryParamMinAndMaxChangeThePicture()
    {
        Assert.True(Diff(Frame120("{\"channels\":2}"), Frame120("{\"channels\":8}")) > 0, "channels: Min и Max одинаковы");
        Assert.True(Diff(Frame120("{\"quakeRate\":0}"), Frame120("{\"quakeRate\":1}")) > 0, "quakeRate: Min и Max одинаковы");
        Assert.True(Diff(Frame120("{\"gain\":0.3}"), Frame120("{\"gain\":3}")) > 0, "gain: Min и Max одинаковы");
    }

    private static CharGrid Frame120(string json)
    {
        var s = SceneContract.Make(() => new SeismoScene(), 120, 40, 9, Opts(json));
        SceneContract.Run(s, 120);
        return SceneContract.Snap(s, 120, 40);
    }

    private static SceneOptions Opts(string json) => new() { Params = JsonDocument.Parse(json).RootElement };

    private static int Diff(CharGrid a, CharGrid b)
    {
        int n = 0;
        for (int i = 0; i < a.Cells.Length; i++)
            if (a.Cells[i].Ch != b.Cells[i].Ch || a.Cells[i].Fg != b.Cells[i].Fg || a.Cells[i].Bg != b.Cells[i].Bg) n++;
        return n;
    }

    /// <summary>Максимум |сырого значения| трассы канала на отрезке времени [from, to] (доли полуполосы).</summary>
    private static double PeakAbs(SeismoScene s, int ch, double from, double to)
    {
        double peak = 0;
        for (int x = 0; x < s.SubColumns; x++)
        {
            double t = s.TimeAt(x);
            if (t < from || t > to) continue;
            peak = Math.Max(peak, Math.Abs(s.TraceAt(ch, x)));
        }
        return peak;
    }

    private static double ChangedShare(CharGrid a, CharGrid b)
    {
        int n = 0;
        for (int i = 0; i < a.Cells.Length; i++)
            if (a.Cells[i].Ch != b.Cells[i].Ch || a.Cells[i].Fg != b.Cells[i].Fg || a.Cells[i].Bg != b.Cells[i].Bg) n++;
        return n / (double)a.Cells.Length;
    }
}
