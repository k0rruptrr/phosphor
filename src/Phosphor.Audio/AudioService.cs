using NAudio.Wave;
using Phosphor.Core;

namespace Phosphor.Audio;

/// <summary>
/// Захват того, что играет в колонках (WASAPI loopback). Никогда не бросает исключения наружу.
/// </summary>
public sealed class AudioService : IDisposable
{
    private const float RestartIntervalSec = 2f;
    private const int ChunkFrames = 8192;

    private enum SampleKind { Float32, Pcm16 }

    private readonly AudioConfig _config;
    private readonly Action<string> _log;
    private readonly object _captureLock = new();
    private readonly float[] _mono = new float[ChunkFrames];

    private WasapiLoopbackCapture? _capture;
    private volatile BeatAnalyzer? _analyzer;
    private SampleKind _kind;
    private int _channels;
    private int _bytesPerSample;
    private volatile bool _capturing;
    private volatile bool _pendingRestart;
    private volatile bool _disposed;
    private float _restartTimer;

    private AudioService(AudioConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
    }

    public static AudioService Start(AudioConfig config, Action<string> log)
    {
        var svc = new AudioService(config ?? new AudioConfig(), log ?? (_ => { }));
        try
        {
            lock (svc._captureLock) svc.StartCapture();
        }
        catch (Exception e)
        {
            svc.SafeLog($"Захват звука не запущен: {e.Message}");
            svc._capturing = false;
        }
        return svc;
    }

    public bool Capturing => _capturing;

    /// <summary>= analyzer.Read(dt); если захват не работает — AudioFrame.Silent.</summary>
    public AudioFrame Tick(float dt)
    {
        if (_disposed) return AudioFrame.Silent;
        try
        {
            if (_pendingRestart)
            {
                _restartTimer += dt;
                if (_restartTimer >= RestartIntervalSec)
                {
                    _restartTimer = 0f;
                    TryRestart();
                }
            }

            var analyzer = _analyzer;
            if (analyzer is null) return AudioFrame.Silent;
            var frame = analyzer.Read(dt);
            if (!_capturing) return AudioFrame.Silent;
            // Спектр кладём только при звуке: при тишине Bands = null.
            return frame.Active ? frame with { Bands = analyzer.Bands } : frame;
        }
        catch (Exception e)
        {
            SafeLog($"Ошибка аудио: {e.Message}");
            return AudioFrame.Silent;
        }
    }

    /// <summary>Вызывается под _captureLock. Бросает исключение при неудаче.</summary>
    private void StartCapture()
    {
        _capturing = false;
        DisposeCapture();

        var cap = new WasapiLoopbackCapture();
        try
        {
            var fmt = cap.WaveFormat;
            if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
                _kind = SampleKind.Float32;
            else if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
                _kind = SampleKind.Pcm16;
            else
                throw new NotSupportedException($"формат {fmt.Encoding}, {fmt.BitsPerSample} бит не поддерживается");

            if (fmt.Channels <= 0) throw new NotSupportedException("нет каналов");

            _channels = fmt.Channels;
            _bytesPerSample = fmt.BitsPerSample / 8;
            _analyzer = new BeatAnalyzer(fmt.SampleRate, _config);

            cap.DataAvailable += OnData;
            cap.RecordingStopped += OnStopped;
            _capture = cap;
            cap.StartRecording();
            _capturing = true;
            SafeLog($"Захват звука: {fmt.SampleRate} Гц, {fmt.Channels} кан., {fmt.BitsPerSample} бит");
        }
        catch
        {
            try { cap.Dispose(); } catch { }
            _capture = null;
            throw;
        }
    }

    private void TryRestart()
    {
        lock (_captureLock)
        {
            if (_disposed) return;
            try
            {
                StartCapture();
                _pendingRestart = false;
            }
            catch (Exception e)
            {
                SafeLog($"Перезапуск захвата не удался, повтор через 2 с: {e.Message}");
            }
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_disposed) return;
        var analyzer = _analyzer;
        if (analyzer is null) return;

        int ch = _channels;
        int frameBytes = ch * _bytesPerSample;
        if (frameBytes <= 0) return;
        int frames = e.BytesRecorded / frameBytes;
        int done = 0;
        while (done < frames)
        {
            int n = Math.Min(_mono.Length, frames - done);
            int baseIdx = done * frameBytes;
            if (_kind == SampleKind.Float32)
            {
                for (int i = 0; i < n; i++)
                {
                    float sum = 0f;
                    int p = baseIdx + i * frameBytes;
                    for (int c = 0; c < ch; c++)
                        sum += BitConverter.ToSingle(e.Buffer, p + c * 4);
                    _mono[i] = sum / ch;
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    float sum = 0f;
                    int p = baseIdx + i * frameBytes;
                    for (int c = 0; c < ch; c++)
                        sum += BitConverter.ToInt16(e.Buffer, p + c * 2) / 32768f;
                    _mono[i] = sum / ch;
                }
            }
            analyzer.Push(_mono.AsSpan(0, n));
            done += n;
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        _capturing = false;
        if (_disposed) return;
        SafeLog(e.Exception != null
            ? $"Захват остановлен: {e.Exception.Message}"
            : "Захват остановлен");
        // Перезапуск делаем в Tick, не здесь: Dispose захвата из его же события небезопасен.
        _restartTimer = RestartIntervalSec;
        _pendingRestart = true;
    }

    private void DisposeCapture()
    {
        var cap = _capture;
        _capture = null;
        if (cap is null) return;
        try
        {
            cap.DataAvailable -= OnData;
            cap.RecordingStopped -= OnStopped;
        }
        catch { }
        try { cap.StopRecording(); } catch { }
        try { cap.Dispose(); } catch { }
    }

    private void SafeLog(string message)
    {
        try { _log(message); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _capturing = false;
        lock (_captureLock)
        {
            DisposeCapture();
        }
    }
}
