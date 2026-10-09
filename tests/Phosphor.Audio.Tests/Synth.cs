using Phosphor.Audio;
using Phosphor.Core;

namespace Phosphor.Audio.Tests;

/// <summary>Синтетический сигнал: кик (синус 55 Гц), хэты (шум), фон (шум). Без устройств.</summary>
internal static class Synth
{
    public const int SampleRate = 48000;
    private const int Seed = 12345;

    /// <summary>Ритм с сегментами темпа: (с какой секунды, BPM). Кик на каждой доле, хэт на восьмых. gain масштабирует всё.</summary>
    public static float[] Rhythm(double totalSec, (double From, double Bpm)[] tempo, float gain = 1f,
        float noise = 0.02f, bool hats = true)
    {
        int n = (int)(totalSec * SampleRate);
        var rnd = new Random(Seed);
        var buf = new float[n];

        // Фон: шум (по умолчанию 0.02).
        for (int i = 0; i < n; i++) buf[i] = gain * noise * (float)(2 * rnd.NextDouble() - 1);

        for (int s = 0; s < tempo.Length; s++)
        {
            double segStart = tempo[s].From;
            double segEnd = s + 1 < tempo.Length ? tempo[s + 1].From : totalSec;
            double interval = 60.0 / tempo[s].Bpm;
            for (int j = 0; ; j++)
            {
                double beatT = segStart + j * interval;
                if (beatT >= segEnd) break;
                AddKick(buf, beatT, gain);
                double hatT = beatT + 0.5 * interval;
                if (hats && hatT < segEnd) AddHat(buf, hatT, gain, rnd);
            }
        }
        return buf;
    }

    /// <summary>Только белый шум заданной амплитуды (равномерный в ±amp).</summary>
    public static float[] WhiteNoise(double totalSec, float amp)
    {
        int n = (int)(totalSec * SampleRate);
        var rnd = new Random(Seed);
        var buf = new float[n];
        for (int i = 0; i < n; i++) buf[i] = amp * (float)(2 * rnd.NextDouble() - 1);
        return buf;
    }

    /// <summary>Белый шум, амплитуда линейно растёт от 0 до peak за totalSec.</summary>
    public static float[] RampNoise(double totalSec, float peak)
    {
        int n = (int)(totalSec * SampleRate);
        var rnd = new Random(Seed);
        var buf = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)SampleRate;
            buf[i] = (float)(peak * t / totalSec * (2 * rnd.NextDouble() - 1));
        }
        return buf;
    }

    public static float[] Zeros(double totalSec) => new float[(int)(totalSec * SampleRate)];

    private static void AddKick(float[] buf, double t0, float gain)
    {
        int start = (int)(t0 * SampleRate);
        int len = (int)(0.5 * SampleRate);
        for (int i = 0; i < len && start + i < buf.Length; i++)
        {
            double t = i / (double)SampleRate;
            double env = Math.Exp(-t / 0.120);
            buf[start + i] += (float)(gain * 0.8 * env * Math.Sin(2 * Math.PI * 55.0 * t));
        }
    }

    private static void AddHat(float[] buf, double t0, float gain, Random rnd)
    {
        int start = (int)(t0 * SampleRate);
        int len = (int)(0.030 * SampleRate);
        for (int i = 0; i < len && start + i < buf.Length; i++)
            buf[start + i] += gain * 0.15f * (float)(2 * rnd.NextDouble() - 1);
    }
}

internal static class Harness
{
    public const int ChunkSamples = 480; // 10 мс

    /// <summary>Подаёт сигнал кусками по 10 мс, после каждого куска Read(0.01f). Возвращает (время конца куска, кадр).</summary>
    public static List<(double T, AudioFrame F)> Run(float[] signal, AudioConfig? config = null)
    {
        var analyzer = new BeatAnalyzer(Synth.SampleRate, config ?? new AudioConfig());
        var list = new List<(double T, AudioFrame F)>(signal.Length / ChunkSamples + 1);
        double t = 0;
        for (int i = 0; i < signal.Length; i += ChunkSamples)
        {
            int n = Math.Min(ChunkSamples, signal.Length - i);
            analyzer.Push(signal.AsSpan(i, n));
            t += n / (double)Synth.SampleRate;
            list.Add((t, analyzer.Read(0.01f)));
        }
        return list;
    }

    public static int CountBeats(List<(double T, AudioFrame F)> frames, double fromSec = 2.0)
        => frames.Count(x => x.T >= fromSec && x.F.Beat);
}
