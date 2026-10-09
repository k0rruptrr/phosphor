using System.Globalization;
using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// «Табло погоды»: split-flap (Solari) с прогнозом на 7 дней. Пластинка = ячейка с фоном 0x1A1A1A;
/// над каждой строкой пластинок — строка-«тень» из '▄'. Данные (погода, часы) приходят снимками от хоста;
/// сцена сама не ходит в сеть и не читает реальное время, кроме снимка "clock".
/// </summary>
public sealed class WeatherBoardScene : IScene, IDataScene, IPreferredRows, IAudioReactive
{
    public string Id => "weatherboard";
    public int PreferredRows => 36;

    private static readonly string[] Keys = { "weather", "clock" };
    public IReadOnlyList<string> DataKeys => Keys;

    /// <summary>Порядок символов при перелистывании (от текущего к целевому, по кругу).</summary>
    private const string Alphabet = " АБВГДЕЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ0123456789+-°.:·ABCDEFGHIJKLMNOPQRSTUVWXYZ/";
    private const float StepSec = 0.03f;
    private const int Margin = 2;
    private const int DayRows = 7;
    private const float TickerSpeed = 8f;
    private const string NoCity = "───";
    private const string BlinkText = "ОЖИДАНИЕ ДАННЫХ";
    private const string NoDataTicker = "ДАННЫЕ: OPEN-METEO · ОЖИДАНИЕ ДАННЫХ · ";

    private static readonly Rgba PlateBg = Rgba.Hex(0x1A1A1A);
    private static readonly Rgba ShadowFg = Rgba.Hex(0x0E0E0E);
    private static readonly Rgba Warm = Rgba.Hex(0xF2EEE6);
    private static readonly Rgba Cold = Rgba.Hex(0xA8D8FF);
    private static readonly Rgba Accent = Rgba.Hex(0xFF6600);
    private static readonly Rgba Dim = Rgba.Hex(0x5E5A52);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private Random _rng = new(0);
    private int _cols, _rows;
    private int _x0, _x1;                       // горизонтальный диапазон пластинок (включительно)
    private int[] _lineY = Array.Empty<int>();  // [0] шапка, [1] сейчас, [2] шапка таблицы, [3..9] дни
    private int _tickerY;
    private int _clockX, _dateX, _blinkX;

    // Пластинки (struct-of-arrays). Индекс пластинки = слот во всех массивах.
    private int _n;
    private int[] _plateAt = Array.Empty<int>(); // cols*rows → индекс пластинки или -1
    private int[] _px = Array.Empty<int>(), _py = Array.Empty<int>();
    private char[] _cur = Array.Empty<char>(), _tgt = Array.Empty<char>(), _newTgt = Array.Empty<char>();
    private Rgba[] _fg = Array.Empty<Rgba>(), _newFg = Array.Empty<Rgba>();
    private int[] _pos = Array.Empty<int>();    // позиция в Alphabet во время перелистывания
    private int[] _steps = Array.Empty<int>();  // оставшиеся шаги; 0 — пластинка стоит
    private float[] _wait = Array.Empty<float>();
    private float[] _base = Array.Empty<float>();  // базовая задержка старта (волна слева направо)
    private float[] _stepT = Array.Empty<float>();
    private bool[] _flash = Array.Empty<bool>();   // этот кадр — первый кадр шага (рисуется Scale 0.6)

    // Данные
    private bool _hasWeather, _hasClock;
    private WeatherSnapshot? _weather;
    private DateTime _clock;
    private string _ticker = NoDataTicker;
    private float _tickerPos;

    // Время
    private float _t;
    private bool _blinkOn = true;
    private float _waveLeft;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = cols;
        _rows = rows;
        _rng = rng;
        _x0 = Margin;
        _x1 = cols - 1 - Margin;

        _lineY = new int[3 + DayRows];
        _lineY[0] = Margin + 1;
        _lineY[1] = Margin + 4;
        _lineY[2] = Margin + 7;
        for (int i = 0; i < DayRows; i++) _lineY[3 + i] = Margin + 9 + 2 * i;
        _tickerY = rows - 1 - Margin;

        _clockX = _x1 - 4;
        _dateX = _clockX - 9;
        _blinkX = _dateX - 17;

        int lo = Math.Max(0, _x0), hi = Math.Min(cols - 1, _x1);
        int span = Math.Max(1, _x1 - _x0);
        int linesIn = 0;
        foreach (int y in _lineY) if (y >= 0 && y < rows) linesIn++;
        int n = hi >= lo ? linesIn * (hi - lo + 1) : 0;

        _n = n;
        _plateAt = new int[cols * rows];
        Array.Fill(_plateAt, -1);
        _px = new int[n];
        _py = new int[n];
        _cur = new char[n];
        _tgt = new char[n];
        _newTgt = new char[n];
        _fg = new Rgba[n];
        _newFg = new Rgba[n];
        _pos = new int[n];
        _steps = new int[n];
        _wait = new float[n];
        _base = new float[n];
        _stepT = new float[n];
        _flash = new bool[n];

        int k = 0;
        foreach (int y in _lineY)
        {
            if (y < 0 || y >= rows) continue;
            for (int x = lo; x <= hi; x++, k++)
            {
                _px[k] = x;
                _py[k] = y;
                _plateAt[y * cols + x] = k;
                // Волна слева направо: 0..0.4 с по X + небольшой случайный разброс.
                _base[k] = 0.4f * (x - _x0) / span + rng.NextSingle() * 0.05f;
            }
        }

        _t = 0f;
        _blinkOn = true;
        _tickerPos = 0f;
        _hasWeather = false;
        _hasClock = false;
        _weather = null;
        _clock = default;
        _ticker = NoDataTicker;
        _waveLeft = 45f + rng.NextSingle() * 45f;

        Compose(snap: true);
    }

    public void OnData(string key, DataSnapshot snapshot)
    {
        if (key == "clock" && snapshot is ClockSnapshot c)
        {
            _clock = c.Local;
            _hasClock = true;
            Compose(snap: false);
        }
        else if (key == "weather" && snapshot is WeatherSnapshot w)
        {
            _weather = w;
            _hasWeather = true;
            _ticker = BuildTicker(w);
            _tickerPos = 0f;
            Compose(snap: false);
        }
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;

        _t = (_t + dt) % 1000f;
        _blinkOn = ((int)(_t / 0.5f) & 1) == 0;
        if (_ticker.Length > 0) _tickerPos = (_tickerPos + dt * TickerSpeed) % _ticker.Length;

        // Раз в 45–90 с — волна по всему табло без смены значений.
        _waveLeft -= dt;
        if (_waveLeft <= 0f)
        {
            for (int i = 0; i < _n; i++) StartWave(i);
            _waveLeft = 45f + _rng.NextSingle() * 45f;
        }

        for (int i = 0; i < _n; i++)
        {
            _flash[i] = false;
            Advance(i, dt);
        }
    }

    public void Draw(CharGrid grid)
    {
        grid.Clear(Rgba.Black);
        int lo = Math.Max(0, _x0), hi = Math.Min(_cols - 1, _x1);

        // Тени: строка '▄' над каждой строкой пластинок.
        foreach (int y in _lineY)
        {
            int sy = y - 1;
            if (sy < 0 || sy >= _rows) continue;
            for (int x = lo; x <= hi; x++) grid.Put(x, sy, '▄', ShadowFg, Rgba.Black);
        }

        for (int i = 0; i < _n; i++)
        {
            Rgba fg = _flash[i] ? _fg[i].Scale(0.6f) : _fg[i];
            grid.Put(_px[i], _py[i], _cur[i], fg, PlateBg);
        }

        // Мигающее «ОЖИДАНИЕ ДАННЫХ» в шапке, пока данных нет.
        if (!_hasWeather && _blinkOn && _lineY.Length > 0)
        {
            int hy = _lineY[0];
            for (int k = 0; k < BlinkText.Length; k++)
                grid.Put(_blinkX + k, hy, BlinkText[k], Accent, PlateBg);
        }

        // Бегущий тикер.
        if (_tickerY >= 0 && _tickerY < _rows && _ticker.Length > 0)
        {
            int len = _ticker.Length, off = (int)_tickerPos;
            for (int x = lo; x <= hi; x++)
                grid.Put(x, _tickerY, _ticker[(off + x - _x0) % len], Accent, Rgba.Black);
        }
    }

    public void OnAudio(in AudioFrame audio)
    {
        if (!audio.Active || !audio.Beat || _n == 0) return;
        // Одна случайная строка таблицы делает короткую волну; значения не меняются.
        int y = _lineY[3 + _rng.Next(DayRows)];
        for (int i = 0; i < _n; i++)
            if (_py[i] == y) StartWave(i);
    }

    /// <summary>Собрать целевые символы из текущих данных и запустить перелистывание там, где они изменились.</summary>
    private void Compose(bool snap)
    {
        Array.Fill(_newTgt, ' ');
        Array.Fill(_newFg, Warm);
        BuildHeader();
        BuildNow();
        BuildTable();

        for (int i = 0; i < _n; i++)
        {
            _fg[i] = _newFg[i];
            if (snap) Settle(i, _newTgt[i]);
            else if (_newTgt[i] != _tgt[i]) StartFlip(i, _newTgt[i]);
        }
    }

    private void BuildHeader()
    {
        int y = _lineY[0];
        string city = _weather is { } w ? Fit(GlyphSet.Sanitize(w.City.ToUpperInvariant()), 20) : NoCity;
        WriteAt(y, _x0, "ПОГОДА · " + city, Accent);
        WriteAt(y, _clockX, _hasClock ? _clock.ToString("HH:mm", Inv) : "--:--", Warm);
        WriteAt(y, _dateX, _hasClock ? Wd(_clock.DayOfWeek) + " " + _clock.ToString("dd.MM", Inv) : "--.--", Warm);
    }

    private void BuildNow()
    {
        int y = _lineY[1];
        int x = _x0;
        var w = _weather;

        SetTarget(x, y, w is null ? ' ' : Wmo.Icon(w.Now.Code), Warm);
        string temp = w is null ? "--" : Temp(w.Now.TempC);
        WriteAt(y, x + 2, RightFit(temp, 4), w is null ? Warm : TempColor(w.Now.TempC));
        WriteAt(y, x + 7, Fit(w is null ? "--" : Wmo.Ru(w.Now.Code), 14), Warm);
        // Open-Meteo даёт направление «откуда дует»: сторона света — как есть, стрелка — куда дует (+180°).
        string wind = w is null
            ? "ВЕТЕР --"
            : "ВЕТЕР " + Math.Round(w.Now.WindKmh).ToString(Inv) + " КМ/Ч " + CompassFrom(w.Now.WindDirDeg)
              + " " + WindArrow(w.Now.WindDirDeg + 180.0);
        WriteAt(y, x + 22, wind, Warm);
    }

    private void BuildTable()
    {
        // Колонки разделены пробелами внутри пластинок (чёрных зазоров нет).
        // День: 0..5, ДНЁМ: 6..11, НОЧЬЮ: 12..17, ОСАДКИ: 18..24, ПОГОДА: 25..39.
        int x = _x0;
        int h = _lineY[2];
        WriteAt(h, x, Fit("ДЕНЬ", 6), Dim);
        WriteAt(h, x + 6, RightFit("ДНЁМ", 6), Dim);
        WriteAt(h, x + 12, RightFit("НОЧЬЮ", 6), Dim);
        WriteAt(h, x + 18, RightFit("ОСАДКИ", 7), Dim);
        WriteAt(h, x + 25, " " + Fit("ПОГОДА", 14), Dim);

        var w = _weather;
        for (int i = 0; i < DayRows; i++)
        {
            int y = _lineY[3 + i];
            if (w is null || i >= w.Days.Count)
            {
                WriteAt(y, x, Fit("-- --", 6), Warm);
                WriteAt(y, x + 6, RightFit("--", 6), Warm);
                WriteAt(y, x + 12, RightFit("--", 6), Warm);
                WriteAt(y, x + 18, RightFit("--", 7), Warm);
                WriteAt(y, x + 25, " " + Fit("--", 14), Warm);
                continue;
            }

            var d = w.Days[i];
            WriteAt(y, x, Fit(Wd(d.Date.DayOfWeek) + " " + d.Date.Day.ToString("00", Inv), 6), Warm);
            WriteAt(y, x + 6, RightFit(Temp(d.MaxC), 6), TempColor(d.MaxC));
            WriteAt(y, x + 12, RightFit(Temp(d.MinC), 6), TempColor(d.MinC));
            WriteAt(y, x + 18, RightFit(d.PrecipMm.ToString("0.0", Inv), 7), Warm);
            WriteAt(y, x + 25, " " + Fit(Wmo.Ru(d.Code), 14), Warm);
        }
    }

    private static string BuildTicker(WeatherSnapshot w)
    {
        string coords = Math.Abs(w.Lat).ToString("0.0", Inv) + (w.Lat >= 0 ? "N" : "S") + " "
            + Math.Abs(w.Lon).ToString("0.0", Inv) + (w.Lon >= 0 ? "E" : "W");
        return "ДАННЫЕ: OPEN-METEO · ОБНОВЛЕНО " + w.FetchedAt.ToLocalTime().ToString("HH:mm", Inv)
            + (w.FromCache ? " · КЕШ" : "") + " · " + coords + " · ";
    }

    /// <summary>Пластинка стоит на символе t без анимации.</summary>
    private void Settle(int i, char t)
    {
        _tgt[i] = t;
        _cur[i] = t;
        _steps[i] = 0;
        _wait[i] = 0f;
        _stepT[i] = 0f;
        int idx = Alphabet.IndexOf(t);
        _pos[i] = idx < 0 ? 0 : idx;
    }

    /// <summary>Запустить перелистывание от текущего символа к t (вперёд по алфавиту).</summary>
    private void StartFlip(int i, char t)
    {
        _tgt[i] = t;
        int to = Alphabet.IndexOf(t);
        if (to < 0)
        {
            Settle(i, t); // символ вне алфавита табло — без анимации
            return;
        }

        int from = Alphabet.IndexOf(_cur[i]);
        if (from < 0) from = 0;
        int steps = (to - from + Alphabet.Length) % Alphabet.Length;
        if (steps == 0)
        {
            Settle(i, t);
            return;
        }

        if (_steps[i] == 0) _wait[i] = _base[i]; // стоявшая пластинка ждёт волну; идущая — продолжает
        _pos[i] = from;
        _steps[i] = steps;
        _stepT[i] = 0f;
    }

    /// <summary>Полный круг по алфавиту и возврат к исходному символу (без смены значения).</summary>
    private void StartWave(int i)
    {
        if (_steps[i] != 0) return;
        int from = Alphabet.IndexOf(_cur[i]);
        if (from < 0) return;
        _pos[i] = from;
        _steps[i] = Alphabet.Length;
        _stepT[i] = 0f;
        _wait[i] = _base[i];
    }

    private void Advance(int i, float dt)
    {
        if (_steps[i] == 0) return;

        float left = dt;
        if (_wait[i] > 0f)
        {
            _wait[i] -= dt;
            if (_wait[i] > 0f) return;
            left = -_wait[i];
            _wait[i] = 0f;
        }

        _stepT[i] += left;
        while (_stepT[i] >= StepSec && _steps[i] > 0)
        {
            _stepT[i] -= StepSec;
            _steps[i]--;
            _pos[i] = (_pos[i] + 1) % Alphabet.Length;
            _cur[i] = Alphabet[_pos[i]];
            _flash[i] = true;
        }

        if (_steps[i] == 0) _stepT[i] = 0f;
    }

    private void SetTarget(int x, int y, char ch, Rgba fg)
    {
        if ((uint)x >= (uint)_cols || (uint)y >= (uint)_rows) return;
        int p = _plateAt[y * _cols + x];
        if (p < 0) return;
        _newTgt[p] = ch;
        _newFg[p] = fg;
    }

    private void WriteAt(int y, int x, string s, Rgba fg)
    {
        for (int k = 0; k < s.Length; k++) SetTarget(x + k, y, s[k], fg);
    }

    private static string Fit(string s, int w) => s.Length >= w ? s[..w] : s.PadRight(w);

    private static string RightFit(string s, int w) => s.Length >= w ? s[..w] : s.PadLeft(w);

    private static string Temp(double v)
    {
        int t = (int)Math.Round(v, MidpointRounding.AwayFromZero);
        return (t > 0 ? "+" : "") + t.ToString(Inv) + "°";
    }

    private static Rgba TempColor(double v) =>
        Math.Round(v, MidpointRounding.AwayFromZero) < 0 ? Cold : Warm;

    /// <summary>Стрелка по направлению: ближайшая из ↑ → ↓ ←; на границе (45°, 135°…) — по часовой.</summary>
    private static char WindArrow(double deg)
    {
        double d = deg % 360.0;
        if (d < 0) d += 360.0;
        int q = (int)Math.Floor(d / 90.0 + 0.5) % 4;
        return q switch { 0 => '↑', 1 => '→', 2 => '↓', _ => '←' };
    }

    /// <summary>Сторона света, откуда дует, 8 румбов: С СВ В ЮВ Ю ЮЗ З СЗ.</summary>
    private static string CompassFrom(double deg)
    {
        double d = deg % 360.0;
        if (d < 0) d += 360.0;
        int s = (int)Math.Floor(d / 45.0 + 0.5) % 8;
        return s switch
        {
            0 => "С", 1 => "СВ", 2 => "В", 3 => "ЮВ",
            4 => "Ю", 5 => "ЮЗ", 6 => "З", _ => "СЗ",
        };
    }

    private static string Wd(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "ПН",
        DayOfWeek.Tuesday => "ВТ",
        DayOfWeek.Wednesday => "СР",
        DayOfWeek.Thursday => "ЧТ",
        DayOfWeek.Friday => "ПТ",
        DayOfWeek.Saturday => "СБ",
        _ => "ВС",
    };
}
