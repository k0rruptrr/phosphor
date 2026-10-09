using Phosphor.Audio;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Audio.Tests;

/// <summary>Замер распределения ratio онсет-детектора. Только печатает, ничего не проверяет.</summary>
[Trait("Category", "Diagnostics")]
public class OnsetDiagnostics
{
    private const int Step = 256;
    private readonly ITestOutputHelper _out;

    public OnsetDiagnostics(ITestOutputHelper output) => _out = output;

    /// <summary>Подаёт сигнал и возвращает трассу ratio (по шагу 256 сэмплов).</summary>
    private static List<double> Trace(float[] signal)
    {
        var analyzer = new BeatAnalyzer(Synth.SampleRate, new AudioConfig());
        var trace = new List<double>();
        analyzer.OnsetRatio += trace.Add;
        for (int i = 0; i < signal.Length; i += Harness.ChunkSamples)
        {
            int n = Math.Min(Harness.ChunkSamples, signal.Length - i);
            analyzer.Push(signal.AsSpan(i, n));
        }
        return trace;
    }

    private static double Percentile(List<double> values, double p)
    {
        var s = values.OrderBy(v => v).ToArray();
        int idx = Math.Clamp((int)Math.Ceiling(p * s.Length) - 1, 0, s.Length - 1);
        return s[idx];
    }

    /// <summary>Максимум ratio на шагах, попадающих в окно [t, t+0.05 с] для каждого кика (t ≥ 2 с).</summary>
    private static List<double> KickPeaks(List<double> trace, double totalSec, double bpm)
    {
        var peaks = new List<double>();
        double interval = 60.0 / bpm;
        for (double t = 2.0; t < totalSec - 0.5; t += interval)
        {
            double max = double.NegativeInfinity;
            for (int i = 0; i < trace.Count; i++)
            {
                double tEnd = (i + 1) * (double)Step / Synth.SampleRate;
                if (tEnd >= t && tEnd <= t + 0.05) max = Math.Max(max, trace[i]);
            }
            peaks.Add(max);
        }
        return peaks;
    }

    private void Report(string name, List<double> trace, double? skipSec = null)
    {
        var t = skipSec is null ? trace : trace.Skip((int)(skipSec.Value * Synth.SampleRate / Step)).ToList();
        _out.WriteLine($"{name}: p50={Percentile(t, 0.5):F3} p99={Percentile(t, 0.99):F3} p99.9={Percentile(t, 0.999):F3} max={t.Max():F3} (шагов {t.Count})");
    }

    [Fact]
    public void MeasureOnsetRatioDistributions()
    {
        // Белый шум 0.3, 20 с.
        var noise = Trace(Synth.WhiteNoise(20, 0.3f));
        Report("белый шум 0.3, 20 с (весь)", noise);
        Report("белый шум 0.3, 20 с (после 2 с)", noise, skipSec: 2.0);

        // Кик 120 BPM на фоне 0.02 (без хэтов).
        var kickQuiet = Trace(Synth.Rhythm(30, new[] { (0.0, 120.0) }, noise: 0.02f, hats: false));
        var kpQuiet = KickPeaks(kickQuiet, 30, 120);
        _out.WriteLine($"кик 120 BPM, фон 0.02: пики ratio min={kpQuiet.Min():F3} median={Percentile(kpQuiet, 0.5):F3} (киков {kpQuiet.Count})");

        // Кик 120 BPM на шуме 0.3.
        var kickNoise = Trace(Synth.Rhythm(30, new[] { (0.0, 120.0) }, noise: 0.3f, hats: false));
        var kpNoise = KickPeaks(kickNoise, 30, 120);
        _out.WriteLine($"шум 0.3 + кик 120 BPM: пики ratio min={kpNoise.Min():F3} median={Percentile(kpNoise, 0.5):F3} (киков {kpNoise.Count})");
        Report("шум 0.3 + кик (весь трек)", kickNoise, skipSec: 2.0);

        // Рампа шума 0 → 0.5 за 10 с.
        var ramp = Trace(Synth.RampNoise(10, 0.5f));
        _out.WriteLine($"рампа 0→0.5 за 10 с (весь): max={ramp.Max():F3} p99.9={Percentile(ramp, 0.999):F3}");
        var rampAfter = ramp.Skip((int)(2.0 * Synth.SampleRate / Step)).ToList();
        _out.WriteLine($"рампа 0→0.5 за 10 с (после 2 с): max={rampAfter.Max():F3} p99.9={Percentile(rampAfter, 0.999):F3}");
    }
}
