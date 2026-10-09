using Phosphor.Core;

namespace Phosphor.Audio;

/// <summary>
/// Чистый анализатор звука: без устройств, детерминированный. Push — из потока захвата, Read — из главного потока.
/// Время внутри: по числу сэмплов (для битов) и по сумме dt (для отсутствия Push / тишины). Без DateTime и Stopwatch.
/// Уровни Level/Bass/Mid/High — FFT. Биты — детектор онсетов во временной области (low-pass 150 Гц + огибающие).
/// </summary>
public sealed class BeatAnalyzer
{
    private const int FftSize = 1024;
    private const int FftMask = FftSize - 1;
    private const int FftBits = 10;
    private const int Hop = 512;
    private const int HalfSize = FftSize / 2;

    private const float BassLoHz = 20f;
    private const float BassHiHz = 150f;
    private const float MidHiHz = 2000f;
    private const float HighHiHz = 16000f;

    private const double PeakHalfLifeSec = 4.0;
    private const double ReleaseSec = 0.12;
    private const double PulseTauSec = 0.15;
    private const double BpmWindowSec = 8.0;
    private const double SilenceSec = 3.0;
    private const double SilenceRms = 1e-3;
    private const double AgcFloor = 1e-3;
    private const int MaxBeatHistory = 64;
    private const double BpmMin = 70.0;
    private const double BpmMax = 180.0;

    // Онсет-детектор (временная область).
    private const double LpfHz = 150.0;
    private const double LpfQ = 0.7071;
    private const int OnsetStepSamples = 256;          // ≈5 мс при 48 кГц
    private const double FastSec = 0.025;              // симметричная огибающая fast (τ)
    private const double SlowSec = 0.400;
    private const double SlowFloorPower = 1e-8;        // порог для знаменателя ratio
    private const double OnsetFloorPower = 1e-6;       // абсолютная тишина по fast (амплитуда ≈1e-3)
    private const double OnsetTrackerRatio = 0.1;      // fast ≥ 0.1 × трекера пика fast
    private const double OnsetPeakWindowSec = 0.015;   // окно поиска локального максимума
    private const double OnsetBaseThreshold = 3.5;     // T = T0 / Sensitivity

    private readonly object _lock = new();
    private readonly int _sampleRate;
    private readonly float _maxPulse;
    private readonly double _sensitivity;
    private readonly long _minBeatSamples;

    // Предвычисленные таблицы FFT.
    private readonly double[] _window = new double[FftSize];
    private readonly double[] _twCos = new double[HalfSize];
    private readonly double[] _twSin = new double[HalfSize];
    private readonly int[] _bitrev = new int[FftSize];
    private readonly double _magScale;
    private readonly double[] _re = new double[FftSize];
    private readonly double[] _im = new double[FftSize];

    // Диапазоны бинов [start, end).
    private readonly int _bassStart, _bassEnd, _midStart, _midEnd, _highStart, _highEnd;

    // Вход: кольцевой буфер последних FftSize сэмплов.
    private readonly double[] _ring = new double[FftSize];
    private int _ringPos;
    private int _filled;
    private int _sinceHop;
    private long _totalSamples;

    // Спектр для уровней.
    private readonly double[] _curMag = new double[HalfSize];

    // AGC: трекеры пика по полосам.
    private readonly double _agcDecay;
    private double _peakLevel, _peakBass, _peakMid, _peakHigh;

    // Сглаженные выходы.
    private readonly double _releaseCoef;
    private double _level, _bass, _mid, _high;

    // Биквад low-pass 150 Гц, RBJ cookbook, Direct Form II transposed.
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _z1, _z2;

    // Огибающие.
    private readonly double _fastCoef, _slowCoef;
    /// <summary>Отладочный хук: ratio на каждом шаге онсет-детектора. Без подписчиков — ноль стоимости.</summary>
    public event Action<double>? OnsetRatio;
    private double _fast, _slow;
    private int _stepCounter;
    private readonly double _trackerDecay;
    private double _peakFast;

    // Онсет: состояние локального максимума.
    private readonly double _onsetThreshold;
    private readonly long _peakWindowSamples;
    private double _prevRatio;
    private bool _cand;
    private long _candStart;
    private long _candSample;
    private double _candRatio;
    private double _candFast;
    private double _candTracker;

    // Биты и темп.
    private readonly long[] _beatTimes = new long[MaxBeatHistory];
    private int _beatHead;
    private int _beatNum;
    private readonly long[] _bpmTimes = new long[MaxBeatHistory];
    private readonly double[] _intervals = new double[MaxBeatHistory];
    private long _lastBeatSample = long.MinValue / 2;
    private float _bpm;
    private bool _beatLatched;
    private float _pulse;

    // Активность.
    private double _silentSec = SilenceSec; // до первого громкого хопа считаем тишиной
    private double _noPushSec;

    public BeatAnalyzer(int sampleRate, AudioConfig config)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        ArgumentNullException.ThrowIfNull(config);

        _sampleRate = sampleRate;
        _maxPulse = config.MaxPulse;
        _sensitivity = Math.Max(0.01, config.Sensitivity);
        _minBeatSamples = (long)Math.Round(config.MinBeatIntervalMs * sampleRate / 1000.0);

        double hopSec = (double)Hop / sampleRate;
        _agcDecay = Math.Pow(0.5, hopSec / PeakHalfLifeSec);
        _releaseCoef = Math.Exp(-hopSec / ReleaseSec);

        for (int n = 0; n < FftSize; n++)
            _window[n] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * n / FftSize);
        for (int i = 0; i < HalfSize; i++)
        {
            double a = 2.0 * Math.PI * i / FftSize;
            _twCos[i] = Math.Cos(a);
            _twSin[i] = -Math.Sin(a);
        }
        for (int i = 0; i < FftSize; i++)
        {
            int r = 0;
            for (int b = 0; b < FftBits; b++)
                if ((i & (1 << b)) != 0) r |= 1 << (FftBits - 1 - b);
            _bitrev[i] = r;
        }
        // Амплитудная нормировка: синус амплитуды A даёт бин ≈ A.
        _magScale = 4.0 / FftSize;

        _bassStart = BinOf(BassLoHz, true);
        _bassEnd = BinOf(BassHiHz, false);
        _midStart = BinOf(BassHiHz, true);
        _midEnd = BinOf(MidHiHz, false);
        _highStart = BinOf(MidHiHz, true);
        _highEnd = BinOf(HighHiHz, false);

        // Биквад LPF (RBJ cookbook).
        double w0 = 2.0 * Math.PI * LpfHz / sampleRate;
        double cosW = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2.0 * LpfQ);
        double a0 = 1.0 + alpha;
        _b0 = (1.0 - cosW) / 2.0 / a0;
        _b1 = (1.0 - cosW) / a0;
        _b2 = _b0;
        _a1 = -2.0 * cosW / a0;
        _a2 = (1.0 - alpha) / a0;

        // Огибающие: однополюсные, по времени.
        _fastCoef = 1.0 - Math.Exp(-1.0 / (FastSec * sampleRate));
        _slowCoef = 1.0 - Math.Exp(-1.0 / (SlowSec * sampleRate));
        double stepSec = (double)OnsetStepSamples / sampleRate;
        _trackerDecay = Math.Pow(0.5, stepSec / PeakHalfLifeSec);

        _onsetThreshold = OnsetBaseThreshold / _sensitivity;
        _peakWindowSamples = (long)Math.Round(OnsetPeakWindowSec * sampleRate);
    }

    /// <summary>Индекс бина по частоте: start — первый бин с f ≥ hz, иначе — первый бин с f > hz.</summary>
    private int BinOf(float hz, bool start)
    {
        double bin = hz * FftSize / _sampleRate;
        int idx = start ? (int)Math.Ceiling(bin) : (int)Math.Floor(bin) + 1;
        return Math.Clamp(idx, 1, HalfSize);
    }

    /// <summary>Любая длина. FFT-окна 1024 с шагом 512 (Ханн) для уровней; онсеты — посэмплово.</summary>
    public void Push(ReadOnlySpan<float> mono)
    {
        if (mono.Length == 0) return;
        lock (_lock)
        {
            _noPushSec = 0;
            for (int i = 0; i < mono.Length; i++)
            {
                double s = mono[i];

                _ring[_ringPos] = s;
                _ringPos = (_ringPos + 1) & FftMask;
                if (_filled < FftSize) _filled++;
                _totalSamples++;
                if (_filled == FftSize && ++_sinceHop >= Hop)
                {
                    _sinceHop = 0;
                    ProcessHop();
                }

                // Низкие частоты → огибающие (DF2T биквад).
                double y = _b0 * s + _z1;
                _z1 = _b1 * s - _a1 * y + _z2;
                _z2 = _b2 * s - _a2 * y;
                double e = y * y;
                _fast += (e - _fast) * _fastCoef;
                _slow += (e - _slow) * _slowCoef;

                if (++_stepCounter >= OnsetStepSamples)
                {
                    _stepCounter = 0;
                    OnsetStep();
                }
            }
        }
    }

    /// <summary>Вызывается раз в кадр из главного потока. dt — секунды с прошлого вызова.</summary>
    public AudioFrame Read(float dt)
    {
        lock (_lock)
        {
            double d = Math.Clamp(dt, 0f, 1000f);
            _noPushSec = Math.Min(_noPushSec + d, 1e6);
            _silentSec = Math.Min(_silentSec + d, 1e6);

            bool beat = _beatLatched;
            _beatLatched = false;

            bool active = _silentSec < SilenceSec && _noPushSec < SilenceSec;
            if (!active)
            {
                _pulse = 0f;
                return AudioFrame.Silent;
            }

            if (beat) _pulse = _maxPulse;
            else _pulse = (float)(_pulse * Math.Exp(-d / PulseTauSec));
            _pulse = Math.Min(_pulse, _maxPulse);

            return new AudioFrame(
                Active: true,
                Level: (float)_level,
                Bass: (float)_bass,
                Mid: (float)_mid,
                High: (float)_high,
                Beat: beat,
                BeatPulse: _pulse,
                Bpm: _bpm);
        }
    }

    /// <summary>Шаг онсет-детектора (каждые 256 сэмплов): ratio = fast / max(slow, floor), локальный максимум.</summary>
    private void OnsetStep()
    {
        double ratio = _fast / Math.Max(_slow, SlowFloorPower);
        OnsetRatio?.Invoke(ratio);
        _peakFast = Math.Max(_fast, _peakFast * _trackerDecay);
        long now = _totalSamples;

        if (!_cand)
        {
            if (ratio > _onsetThreshold && ratio > _prevRatio)
            {
                _cand = true;
                _candStart = now;
                _candSample = now;
                _candRatio = ratio;
                _candFast = _fast;
                _candTracker = _peakFast;
            }
        }
        else if (ratio > _candRatio)
        {
            _candSample = now;
            _candRatio = ratio;
            _candFast = _fast;
            _candTracker = _peakFast;
        }
        else if (ratio < _prevRatio || now - _candStart >= _peakWindowSamples)
        {
            CommitCandidate();
        }

        _prevRatio = ratio;
    }

    private void CommitCandidate()
    {
        _cand = false;
        bool loudEnough = _candFast > OnsetFloorPower && _candFast >= OnsetTrackerRatio * _candTracker;
        if (!loudEnough) return;
        if (_candSample - _lastBeatSample < _minBeatSamples) return;

        _lastBeatSample = _candSample;
        _beatLatched = true;
        RecordBeat(_candSample);
    }

    private void ProcessHop()
    {
        // Окно в хронологическом порядке: самый старый сэмпл — в позиции _ringPos.
        double sumSq = 0;
        for (int n = 0; n < FftSize; n++)
        {
            double s = _ring[(_ringPos + n) & FftMask];
            sumSq += s * s;
            _re[n] = s * _window[n];
            _im[n] = 0;
        }
        bool loud = Math.Sqrt(sumSq / FftSize) >= SilenceRms;

        Fft();
        for (int k = 0; k < HalfSize; k++)
            _curMag[k] = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) * _magScale;

        double total = BandSum(1, HalfSize);
        double bass = BandSum(_bassStart, _bassEnd);
        double mid = BandSum(_midStart, _midEnd);
        double high = BandSum(_highStart, _highEnd);

        double nLevel = Agc(total, ref _peakLevel);
        double nBass = Agc(bass, ref _peakBass);
        double nMid = Agc(mid, ref _peakMid);
        double nHigh = Agc(high, ref _peakHigh);

        _level = Smooth(_level, nLevel);
        _bass = Smooth(_bass, nBass);
        _mid = Smooth(_mid, nMid);
        _high = Smooth(_high, nHigh);

        if (loud) _silentSec = 0;
    }

    private void RecordBeat(long t)
    {
        _beatTimes[_beatHead] = t;
        _beatHead = (_beatHead + 1) % MaxBeatHistory;
        if (_beatNum < MaxBeatHistory) _beatNum++;
        UpdateBpm(t);
    }

    private void UpdateBpm(long now)
    {
        long windowStart = now - (long)(BpmWindowSec * _sampleRate);
        int m = 0;
        for (int i = 0; i < _beatNum; i++)
        {
            int idx = (_beatHead - _beatNum + i + MaxBeatHistory) % MaxBeatHistory;
            long t = _beatTimes[idx];
            if (t >= windowStart) _bpmTimes[m++] = t;
        }

        int ic = 0;
        for (int j = 1; j < m; j++)
            _intervals[ic++] = _bpmTimes[j] - _bpmTimes[j - 1];

        if (ic == 0)
        {
            _bpm = 0f;
            return;
        }

        Array.Sort(_intervals, 0, ic);
        double median = (ic % 2 == 1)
            ? _intervals[ic / 2]
            : 0.5 * (_intervals[ic / 2 - 1] + _intervals[ic / 2]);
        if (median <= 0)
        {
            _bpm = 0f;
            return;
        }

        double bpm = 60.0 * _sampleRate / median;
        while (bpm < BpmMin) bpm *= 2;
        while (bpm > BpmMax) bpm /= 2;
        _bpm = (float)bpm;
    }

    private double BandSum(int start, int end)
    {
        double sum = 0;
        for (int k = start; k < end; k++) sum += _curMag[k];
        return sum;
    }

    private double Agc(double energy, ref double peak)
    {
        peak = Math.Max(energy, peak * _agcDecay);
        return Math.Clamp(energy / Math.Max(peak, AgcFloor), 0.0, 1.0);
    }

    /// <summary>Атака: 0.5 за хоп. Спад: экспонента с постоянной ReleaseSec (по времени хопа).</summary>
    private double Smooth(double y, double x)
    {
        return x > y ? y + (x - y) * 0.5 : x + (y - x) * _releaseCoef;
    }

    /// <summary>In-place radix-2 Cooley–Tukey на _re/_im (вещественный вход, _im = 0).</summary>
    private void Fft()
    {
        for (int i = 0; i < FftSize; i++)
        {
            int j = _bitrev[i];
            if (j > i)
            {
                (_re[i], _re[j]) = (_re[j], _re[i]);
                (_im[i], _im[j]) = (_im[j], _im[i]);
            }
        }

        for (int size = 2; size <= FftSize; size <<= 1)
        {
            int half = size >> 1;
            int step = FftSize / size;
            for (int start = 0; start < FftSize; start += size)
            {
                for (int j = 0, t = 0; j < half; j++, t += step)
                {
                    double wr = _twCos[t], wi = _twSin[t];
                    int a = start + j;
                    int b = a + half;
                    double xr = _re[b] * wr - _im[b] * wi;
                    double xi = _re[b] * wi + _im[b] * wr;
                    _re[b] = _re[a] - xr;
                    _im[b] = _im[a] - xi;
                    _re[a] += xr;
                    _im[a] += xi;
                }
            }
        }
    }
}
