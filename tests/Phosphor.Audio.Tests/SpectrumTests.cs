using Phosphor.Audio;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Audio.Tests;

/// <summary>Спектр по 32 лог-полосам (AudioFrame.Bands): 30 Гц–16 кГц, dB → 0..1, спад 12 дБ/с.</summary>
public class SpectrumTests
{
    private const int Bands = AudioFrame.BandCount;
    private const double LoHz = 30.0;
    private const double HiHz = 16000.0;
    private const int Chunk = 480;
    private readonly ITestOutputHelper _out;

    public SpectrumTests(ITestOutputHelper output) => _out = output;

    /// <summary>Индекс полосы, содержащей частоту (по тем же геометрическим границам, что в анализаторе).</summary>
    private static int BandOf(double hz)
    {
        double ratio = Math.Pow(HiHz / LoHz, 1.0 / Bands);
        return (int)Math.Floor(Math.Log(hz / LoHz) / Math.Log(ratio));
    }

    private static float[] Sine(double hz, double seconds, float amp)
    {
        int n = (int)(seconds * Synth.SampleRate);
        var s = new float[n];
        for (int i = 0; i < n; i++)
            s[i] = amp * (float)Math.Sin(2 * Math.PI * hz * i / Synth.SampleRate);
        return s;
    }

    private static void Feed(BeatAnalyzer a, float[] signal)
    {
        for (int i = 0; i < signal.Length; i += Chunk)
        {
            int n = Math.Min(Chunk, signal.Length - i);
            a.Push(signal.AsSpan(i, n));
        }
    }

    private static int ArgMax(float[] v)
    {
        int best = 0;
        for (int i = 1; i < v.Length; i++)
            if (v[i] > v[best]) best = i;
        return best;
    }

    [Fact]
    public void Sine100Hz_PeakIsInBandOf100Hz()
    {
        var a = new BeatAnalyzer(Synth.SampleRate, new AudioConfig());
        Feed(a, Sine(100, 1.0, 0.5f));
        a.Read(0.01f);
        int peak = ArgMax(a.Bands), target = BandOf(100);
        _out.WriteLine($"100 Гц: полоса {target}, максимум в полосе {peak}, значения {a.Bands[target]:F3} / {a.Bands[peak]:F3}");
        Assert.InRange(peak, target - 1, target + 1);
        Assert.True(a.Bands[target] >= a.Bands[peak] - 0.1f, $"полоса 100 Гц {a.Bands[target]:F3} заметно ниже максимума {a.Bands[peak]:F3}");
    }

    [Fact]
    public void Sine5kHz_PeakIsInBandOf5kHz()
    {
        var a = new BeatAnalyzer(Synth.SampleRate, new AudioConfig());
        Feed(a, Sine(5000, 1.0, 0.5f));
        a.Read(0.01f);
        int peak = ArgMax(a.Bands), target = BandOf(5000);
        _out.WriteLine($"5 кГц: полоса {target}, максимум в полосе {peak}");
        Assert.InRange(peak, target - 1, target + 1);
        Assert.True(a.Bands[target] >= a.Bands[peak] - 0.1f);
    }

    [Fact]
    public void WhiteNoise_HasNoZeroBands()
    {
        var a = new BeatAnalyzer(Synth.SampleRate, new AudioConfig());
        Feed(a, Synth.WhiteNoise(3.0, 0.3f));
        a.Read(0.01f);
        float min = a.Bands.Min();
        _out.WriteLine($"Белый шум 0.3: минимум полосы {min:F3}, максимум {a.Bands.Max():F3}");
        Assert.True(min > 0.2f, $"полоса почти пустая: {min:F3}");
    }

    [Fact]
    public void BandValuesAreWithinZeroOne()
    {
        var a = new BeatAnalyzer(Synth.SampleRate, new AudioConfig());
        Feed(a, Sine(1000, 0.5, 1.0f));
        a.Read(0.01f);
        Assert.All(a.Bands, v => Assert.InRange(v, 0f, 1f));
    }

    [Fact]
    public void AfterSineStops_BandsDecayAboutTwelveDbPerSecond()
    {
        var a = new BeatAnalyzer(Synth.SampleRate, new AudioConfig());
        Feed(a, Sine(1000, 1.0, 0.5f));
        // Окно FFT (1024 сэмпла) должно полностью стать тихим, прежде чем мерим спад.
        Feed(a, new float[Synth.SampleRate / 10]);
        int b = BandOf(1000);
        float before = a.Bands[b];
        Assert.True(before > 0.7f, $"перед спадом полоса слишком тихая: {before:F3}");

        // 0.5 с тишины = 6 дБ при 12 дБ/с.
        Feed(a, new float[Synth.SampleRate / 2]);
        float after = a.Bands[b];
        double dropDb = (before - after) * 60.0;
        _out.WriteLine($"Спад полосы 1 кГц за 0.5 с: {dropDb:F2} дБ (ожидание 6 ±30%)");
        Assert.InRange(dropDb, 6.0 * 0.7, 6.0 * 1.3);
    }

    [Fact]
    public void BandsArrayIsSameObjectBetweenFrames()
    {
        var a = new BeatAnalyzer(Synth.SampleRate, new AudioConfig());
        var first = a.Bands;
        Feed(a, Sine(440, 0.2, 0.3f));
        var mid = a.Bands;
        a.Read(0.01f);
        Feed(a, Sine(880, 0.2, 0.3f));
        Assert.Same(first, a.Bands);
        Assert.Same(first, mid);
        Assert.Equal(Bands, a.Bands.Length);
    }
}
