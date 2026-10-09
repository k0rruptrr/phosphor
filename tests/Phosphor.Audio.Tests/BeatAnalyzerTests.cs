using Phosphor.Audio;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Audio.Tests;

public class BeatAnalyzerTests
{
    private static readonly (double From, double Bpm)[] Bpm120 = { (0, 120) };
    private readonly ITestOutputHelper _out;

    public BeatAnalyzerTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Rhythm120Bpm_30s_HasExpectedBeatsAndTempo()
    {
        var frames = Harness.Run(Synth.Rhythm(30, Bpm120));
        int beats = Harness.CountBeats(frames);
        _out.WriteLine($"120 BPM, 30 с: битов = {beats} (диапазон 55–63), Bpm в конце = {frames[^1].F.Bpm:F1}");
        Assert.InRange(beats, 55, 63);
        Assert.InRange(frames[^1].F.Bpm, 115f, 125f);
    }

    [Fact]
    public void QuietRhythm_x005_StillFindsBeats()
    {
        var frames = Harness.Run(Synth.Rhythm(30, Bpm120, gain: 0.05f));
        int beats = Harness.CountBeats(frames);
        _out.WriteLine($"Тихий ритм ×0.05, 30 с: битов = {beats} (нужно ≥ 50)");
        Assert.True(beats >= 50, $"битов {beats}");
    }

    [Fact]
    public void WhiteNoiseOnly_FindsFewBeats()
    {
        var frames = Harness.Run(Synth.WhiteNoise(20, 0.3f));
        int beats = Harness.CountBeats(frames);
        _out.WriteLine($"Только белый шум 0.3, 20 с: битов = {beats} (нужно < 5)");
        Assert.True(beats < 5, $"битов {beats}");
    }

    [Fact]
    public void NoiseWithKick_FindsRhythm()
    {
        var frames = Harness.Run(Synth.Rhythm(30, Bpm120, noise: 0.3f, hats: false));
        int beats = Harness.CountBeats(frames);
        _out.WriteLine($"Шум 0.3 + кик 120 BPM, 30 с: битов = {beats} (диапазон 55–63)");
        Assert.InRange(beats, 55, 63);
    }

    [Fact]
    public void GradualNoiseRamp_0To05_NoOnsets()
    {
        var frames = Harness.Run(Synth.RampNoise(10, 0.5f));
        int beats = Harness.CountBeats(frames, fromSec: 0);
        _out.WriteLine($"Нарастание шума 0 → 0.5 за 10 с: битов = {beats} (нужно < 3)");
        Assert.True(beats < 3, $"битов {beats}");
    }

    [Fact]
    public void Silence_5s_IsInactive()
    {
        var frames = Harness.Run(Synth.Zeros(5));
        _out.WriteLine($"Тишина 5 с: последний Active = {frames[^1].F.Active}");
        Assert.False(frames[^1].F.Active);
        Assert.Equal(AudioFrame.Silent, frames[^1].F);
    }

    [Fact]
    public void NoPush_400Reads_IsInactive()
    {
        var analyzer = new BeatAnalyzer(Synth.SampleRate, new AudioConfig());
        AudioFrame last = default;
        for (int i = 0; i < 400; i++) last = analyzer.Read(0.01f);
        _out.WriteLine($"Только Read(0.01) 400 раз: Active = {last.Active}");
        Assert.False(last.Active);
    }

    [Fact]
    public void TempoChange_90To140_BpmFollows()
    {
        var tempo = new[] { (0.0, 90.0), (20.0, 140.0) };
        var frames = Harness.Run(Synth.Rhythm(40, tempo));
        var at30 = frames.First(x => x.T >= 30.0).F;
        _out.WriteLine($"Смена 90→140 на 20 с, 40 с всего: битов = {Harness.CountBeats(frames)}, Bpm при t≈30 с = {at30.Bpm:F1} (диапазон 133–147)");
        Assert.InRange(at30.Bpm, 133f, 147f);
    }

    [Fact]
    public void BeatPulse_BoundedAndDecays()
    {
        var config = new AudioConfig();
        var frames = Harness.Run(Synth.Rhythm(30, new[] { (0.0, 90.0) }));
        double lastBeatT = double.NegativeInfinity;
        int checkedDecay = 0;
        foreach (var (t, f) in frames)
        {
            Assert.True(f.BeatPulse <= config.MaxPulse + 1e-6f, $"пульс {f.BeatPulse} > MaxPulse");
            if (f.Beat)
            {
                lastBeatT = t;
                continue;
            }
            double since = t - lastBeatT;
            if (since >= 0.5 && since < 0.6)
            {
                Assert.True(f.BeatPulse < 0.05f, $"пульс {f.BeatPulse} через {since:F2} с");
                checkedDecay++;
            }
        }
        _out.WriteLine($"BeatPulse, 90 BPM, 30 с: битов = {Harness.CountBeats(frames, 0)}, проверок спада = {checkedDecay}");
        Assert.True(checkedDecay > 0);
    }

    [Fact]
    public void BeatFlag_IsLatchedToSingleRead()
    {
        var frames = Harness.Run(Synth.Rhythm(30, Bpm120));
        int beatReads = frames.Count(x => x.F.Beat);
        _out.WriteLine($"Beat=true в Read: {beatReads}");
        Assert.True(beatReads > 0);
        for (int i = 1; i < frames.Count; i++)
            Assert.False(frames[i].F.Beat && frames[i - 1].F.Beat);
        double prev = double.NegativeInfinity;
        foreach (var (t, f) in frames)
        {
            if (!f.Beat) continue;
            Assert.True(t - prev >= 0.25 - 1e-9, $"интервал {t - prev:F3} с");
            prev = t;
        }
    }
}
