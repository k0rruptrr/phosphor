namespace Phosphor.Overlays;

/// <summary>
/// Состояние видика одного монитора: лента (E-180), показ PLAY/часов/счётчика, REW в конце ленты, AUTO TRACKING.
/// Время идёт от старта заставки и не сбрасывается при смене сцены; «показ» запускается включением видика.
/// Чистая логика без графики — тестируется отдельно.
/// </summary>
public sealed class VcrState
{
    public const double TapeSec = 180 * 60;      // кассета E-180
    public const double ShowSec = 4.0;           // показ PLAY/SP/счётчика после включения и после REW (по умолчанию)
    public const double PlayBlinkSec = 2.0;      // первые секунды показа PLAY мигает
    public const double RewSec = 4.0;            // надпись REW после конца ленты
    public const double RewQuick = 1.0;         // шкала уезжает к пустой за это время
    public const double BlinkHalf = 0.5;         // полупериод мигания (PLAY, двоеточие часов, AUTO TRACKING)
    public const float AutoTrackFatigue = 0.6f;  // совпадает с началом эпизодов автотрекинга в kassette

    private double _t;          // секунды с Init (сумма dt)
    private int _lap;           // номер прохода ленты
    private double _showLeft;   // сколько ещё показывать PLAY/SP/счётчик
    private double _showAge;    // сколько показываем с начала текущего показа (для мигания PLAY)
    private double _rewLeft;    // сколько ещё показывать REW (после конца ленты)
    private bool _on;

    /// <summary>Сколько секунд длится показ PLAY/SP/счётчика (настройка showSec, по умолчанию ShowSec).</summary>
    public double ShowLength { get; set; } = ShowSec;

    /// <summary>Секунды с запуска (сумма dt).</summary>
    public double Time => _t;
    /// <summary>Видик включён (сцена с vhs в этом кадре).</summary>
    public bool On => _on;
    /// <summary>Позиция ленты в проходе, с.</summary>
    public double Pos => _t % TapeSec;
    /// <summary>Идёт REW после конца ленты.</summary>
    public bool Rew => _on && _rewLeft > 0;
    /// <summary>Сколько секунд REW ещё идёт (0, если не идёт).</summary>
    public double RewLeft => _rewLeft;
    /// <summary>Показ группы OSD: PLAY/REW слева, часы, счётчик. Во время REW — тоже.</summary>
    public bool ShowOsd => _on && (_rewLeft > 0 || _showLeft > 0);
    /// <summary>Надпись PLAY видна в этом кадре (мигает первые PlayBlinkSec показа).</summary>
    public bool PlayVisible => ShowOsd && !Rew
        && (_showAge >= PlayBlinkSec || Math.Floor(_showAge / BlinkHalf) % 2 == 0);
    /// <summary>Двоеточие часов видно (мигает раз в секунду).</summary>
    public bool ColonVisible => (_t % 1.0) < 0.5;

    /// <summary>AUTO TRACKING видна: усталость держится, мигает 1 Гц.</summary>
    public bool AutoTracking(float fatigue) => _on && fatigue >= AutoTrackFatigue && (_t % 1.0) < 0.5;

    /// <summary>Включение/выключение видика. Включение запускает показ PLAY/часов/счётчика на ShowSec.</summary>
    public void SetOn(bool on)
    {
        if (on && !_on) StartShow();
        if (!on)
        {
            _showLeft = 0;
            _rewLeft = 0;
        }
        _on = on;
    }

    public void Update(float dt)
    {
        _t += dt;
        if (_showLeft > 0)
        {
            _showLeft -= dt;
            _showAge += dt;
            if (_showLeft < 0) _showLeft = 0;
        }
        if (_rewLeft > 0)
        {
            _rewLeft -= dt;
            if (_rewLeft <= 0)
            {
                _rewLeft = 0;
                StartShow();                 // после REW снова показ PLAY на ShowSec
            }
        }
        int lap = (int)(_t / TapeSec);
        if (lap != _lap)
        {
            _lap = lap;
            _rewLeft = RewSec;
            _showLeft = 0;
        }
    }

    private void StartShow()
    {
        _showLeft = ShowLength;
        _showAge = 0;
    }
}
