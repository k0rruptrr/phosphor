using System.Globalization;
using System.Text;
using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>Часы: табло split-flap (Solari).</summary>
public sealed class SplitFlapScene : ClocksScene { public SplitFlapScene() : base(0) { } public override string Id => "splitflap"; }

/// <summary>Часы: ряд газоразрядных ламп ИН-14.</summary>
public sealed class NixieScene : ClocksScene { public NixieScene() : base(1) { } public override string Id => "nixie"; }

/// <summary>Часы: мировое время с картой терминатора.</summary>
public sealed class WorldClockScene : ClocksScene { public WorldClockScene() : base(2) { } public override string Id => "worldclock"; }

/// <summary>Часы: аналоговый циферблат на секстантах.</summary>
public sealed class AnalogClockScene : ClocksScene { public AnalogClockScene() : base(3) { } public override string Id => "analogclock"; }

/// <summary>
/// Часы — четыре отдельные сцены (раньше одна «clocks» с циферблатами по кругу, и дирижёр уходил раньше, чем
/// доходила очередь второго): табло split-flap (Solari), ряд газоразрядных ламп ИН-14 с проволочными цифрами,
/// мировые часы с картой терминатора и аналоговый циферблат на секстантах.
/// Время приходит снимком "clock", город первой строкой — из снимка "weather" (если есть).
/// </summary>
public abstract class ClocksScene : IScene, IDataScene, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec SecondsSpec = new("seconds", SceneParamKind.Bool, 1f, 0f, 1f, "показывать секунды");
    public static readonly SceneParamSpec SweepSpec = new("sweep", SceneParamKind.Bool, 1f, 0f, 1f,
        "секундная стрелка: плавно (вкл) или шагом (выкл)");
    private static readonly SceneParamSpec[] DigitalParams = { SecondsSpec };
    private static readonly SceneParamSpec[] AnalogParams = { SweepSpec };

    /// <summary>У цифровых циферблатов — секунды; у аналогового секундная стрелка всегда, параметр — её ход.</summary>
    public IReadOnlyList<SceneParamSpec> Params => _face == 3 ? AnalogParams : DigitalParams;

    /// <summary>Циферблат по старому имени из clocks.face (тесты, миграция): flap, nixie, world, analog.</summary>
    public static ClocksScene ForFace(string face) => face.ToLowerInvariant() switch
    {
        "nixie" => new NixieScene(),
        "world" => new WorldClockScene(),
        "analog" => new AnalogClockScene(),
        _ => new SplitFlapScene(),
    };

    protected ClocksScene(int face) => _face = face;

    public abstract string Id { get; }

    private static readonly string[] Keys = { "clock", "weather" };
    public IReadOnlyList<string> DataKeys => Keys;

    /// <summary>Циферблаты в порядке круга (индекс 0 — «табло»).</summary>
    private static readonly string[] Faces = { "flap", "nixie", "world", "analog" };

    private const int FlapW = 4, FlapH = 7;      // плашка: 3 колонки знака + зазор; 7 строк (верх, щель, низ)
    private const int DashG = 10, BlankG = 11;   // индексы глифов в Font
    private const int SepW = 3;                  // зазор между парами ламп (ячейки)
    private const float FlipStep = 0.05f;        // длительность одной фазы переворота, с
    private const float CathodeSec = 3f;        // длительность прогона катодов, с
    private const float SnapMaxAge = 3f;        // дальше этого не экстраполируем время снимка
    private const string DatePattern = "-- --.--.----";
    private static readonly string[] Wd = { "ПН", "ВТ", "СР", "ЧТ", "ПТ", "СБ", "ВС" };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly Rgba Warm = Rgba.Hex(0xF2EEE6);
    private static readonly Rgba Cold = Rgba.Hex(0xA8D8FF);
    private static readonly Rgba Dim = Rgba.Hex(0x6F7F90);
    private static readonly Rgba PlateBg = Rgba.Hex(0x1A1A1A);
    private static readonly Rgba ShadowFg = Rgba.Hex(0x0E0E0E);
    private static readonly Rgba Amber = Rgba.Hex(0xFFB000);
    private static readonly Rgba NeonLit = Rgba.Hex(0xFFB060);   // горящая проволока
    private static readonly Rgba NeonDot = Rgba.Hex(0xFF5A1A);
    private static readonly Rgba NeonOff = Rgba.Hex(0x3A1A0A);
    private static readonly Rgba GhostFg = Rgba.Lerp(Rgba.Hex(0x0C0907), Rgba.Hex(0xFFB060), 0.08f); // призраки 0.08
    private static readonly Rgba Glass = Rgba.Hex(0x0C0907);
    private static readonly Rgba TubeFg = Rgba.Hex(0x4A3A28);
    private static readonly Rgba MeshFg = Rgba.Hex(0x2A1C10);
    private static readonly Rgba Halo = Rgba.Hex(0x3A1E06);
    private static readonly Rgba DayBand = Rgba.Hex(0x1F1B0C);
    private static readonly Rgba NightBand = Rgba.Hex(0x0B1222);
    private static readonly Rgba NightFg = Rgba.Hex(0x6F8FC0);
    private static readonly Rgba DayFill = Rgba.Hex(0x1A170C);
    private static readonly Rgba NightFill = Rgba.Hex(0x060A12);
    private static readonly Rgba TermFg = Rgba.Hex(0xFFC870);
    private static readonly Rgba GratFg = Rgba.Hex(0x2E3E50);

    /// <summary>Шрифт 3×5 для цифр 0–9, минуса (10) и пустого знака (11).</summary>
    private static readonly string[][] Font =
    {
        new[] { "###", "#.#", "#.#", "#.#", "###" }, // 0
        new[] { ".#.", "##.", ".#.", ".#.", "###" }, // 1
        new[] { "###", "..#", "###", "#..", "###" }, // 2
        new[] { "###", "..#", "###", "..#", "###" }, // 3
        new[] { "#.#", "#.#", "###", "..#", "..#" }, // 4
        new[] { "###", "#..", "###", "..#", "###" }, // 5
        new[] { "###", "#..", "###", "#.#", "###" }, // 6
        new[] { "###", "..#", "..#", "..#", "..#" }, // 7
        new[] { "###", "#.#", "###", "#.#", "###" }, // 8
        new[] { "###", "#.#", "###", "..#", "###" }, // 9
        new[] { "...", "...", "###", "...", "..." }, // -
        new[] { "...", "...", "...", "...", "..." }, // пусто
    };

    /// <summary>Семисегментные штрихи цифр 0–9, минус (10) и пусто (11): a — верх, b — верх-право, c — низ-право, d — низ, e — низ-лево, f — верх-лево, g — середина.</summary>
    private static readonly string[] Segments = { "abcdef", "bc", "abdeg", "abcdg", "bcfg", "acdfg", "acdefg", "abc", "abcdefg", "abcdfg", "g", "" };
    private const string AllSegments = "abcdefg";

    /// <summary>Город для мировых часов. FallbackH — смещение от UTC, если зона не найдена в системе; Code — код аэропорта.</summary>
    public sealed record WorldCity(string Name, string WinId, double Lat, double Lon, double FallbackH, string Code);

    /// <summary>Фиксированный набор городов; город погоды (если есть) идёт перед ними.</summary>
    public static readonly WorldCity[] WorldCities =
    {
        new("МОСКВА", "Russian Standard Time", 55.75, 37.62, 3, "MOW"),
        new("ЛОНДОН", "GMT Standard Time", 51.51, -0.13, 0, "LON"),
        new("НЬЮ-ЙОРК", "Eastern Standard Time", 40.71, -74.01, -5, "NYC"),
        new("ЛОС-АНДЖЕЛЕС", "Pacific Standard Time", 34.05, -118.24, -8, "LAX"),
        new("БЕЛГРАД", "Central Europe Standard Time", 44.79, 20.45, 1, "BEG"),
        new("ДУБАЙ", "Arabian Standard Time", 25.2, 55.27, 4, "DXB"),
        new("ТОКИО", "Tokyo Standard Time", 35.68, 139.69, 9, "TYO"),
        new("СИДНЕЙ", "AUS Eastern Standard Time", -33.87, 151.21, 10, "SYD"),
    };

    private static readonly Dictionary<string, TimeZoneInfo?> ZoneCache = new(StringComparer.Ordinal);

    /// <summary>Строка таблицы мировых часов.</summary>
    private struct Row
    {
        public string Name, Code;
        public double Lat, Lon;
        public TimeSpan Off;
        public bool Day;
    }

    /// <summary>Переворот одной плашки: Cur — видимый знак, Tgt — куда идём, From — что падает.</summary>
    private struct Flip
    {
        public char Cur, Tgt, From;
        public float Delay, T;
        public int Stage; // 0 — стоит, 1 — верх уже новый, низ падает (старый верх), 2 — низ новый
    }

    private readonly int _face;          // индекс в Faces
    private float _sinceSnap, _runT = -1f;
    private float _aspect = 1.8f;        // высота/ширина ячейки (SceneOptions.CellAspect)
    private bool _seconds, _sweep, _hasClock, _flapInit;
    private DateTime _clock, _utc;
    private long _lastRunKey = long.MinValue;
    private double _subDecl, _subLon;    // положение Солнца (радианы) на момент последнего снимка
    private readonly Flip[] _big = new Flip[6];
    private readonly Flip[] _date = new Flip[13];
    private readonly Row[] _rows = new Row[1 + WorldCities.Length];
    private int _rowCount;
    private bool _hasWeatherCity;
    private string _wName = "", _wCode = "";
    private double _wLat, _wLon;
    private byte[] _sub = Array.Empty<byte>(); // черновик субпикселей секстантов (циферблаты рисуют в него)
    private int _subW, _subH;

    /// <summary>Ширина субпикселя относительно его высоты: ячейка 2×3 субпикселя, субпиксель = (A·cw/3) по высоте и (cw/2) по ширине.</summary>
    private double SubK => 1.5 / _aspect;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _seconds = options.GetBool(SecondsSpec.Name, SecondsSpec.Default > 0f);
        _sweep = options.GetBool(SweepSpec.Name, SweepSpec.Default > 0f);
        _aspect = Math.Clamp(options.CellAspect, 0.5f, 4f);

        _sinceSnap = 0f;
        _runT = -1f;
        _lastRunKey = long.MinValue;
        _hasClock = false;
        _flapInit = false;
        _clock = default;
        _utc = default;
        _subDecl = 0;
        _subLon = 0;
        _hasWeatherCity = false;
        for (int k = 0; k < _big.Length; k++) Immediate(ref _big[k], '-');
        for (int k = 0; k < _date.Length; k++) Immediate(ref _date[k], DatePattern[k]);
        RebuildRows();
        _sub = new byte[Math.Max(1, cols * 2) * Math.Max(1, rows * 3)];
    }

    public void OnData(string key, DataSnapshot snapshot)
    {
        if (key == "clock" && snapshot is ClockSnapshot c) Apply(c.Local);
        else if (key == "weather" && snapshot is WeatherSnapshot w) ApplyWeather(w);
    }

    private void ApplyWeather(WeatherSnapshot w)
    {
        string name = GlyphSet.Sanitize(w.City ?? "").ToUpperInvariant().Trim();
        if (name.Length > 12) name = name.Substring(0, 12);
        _hasWeatherCity = name.Length > 0;
        _wName = name;
        _wCode = Code3(name);
        _wLat = w.Lat;
        _wLon = w.Lon;
        RebuildRows();
    }

    /// <summary>Код фиксированного города с таким же названием (если город погоды — один из них).</summary>
    private static string? FixedCode(string name)
    {
        foreach (var city in WorldCities) if (city.Name == name) return city.Code;
        return null;
    }

    /// <summary>Код из трёх латинских букв названия; если их нет — HOM.</summary>
    private static string Code3(string name)
    {
        var sb = new StringBuilder(3);
        foreach (char ch in name)
        {
            if (ch >= 'A' && ch <= 'Z' && sb.Length < 3) sb.Append(ch);
        }
        return sb.Length == 3 ? sb.ToString() : "HOM";
    }

    private void Apply(DateTime local)
    {
        bool first = !_flapInit;
        _flapInit = true;
        _clock = local;
        _hasClock = true;
        _sinceSnap = 0f;
        _utc = local.Kind == DateTimeKind.Utc ? local : local.ToUniversalTime();

        int h = local.Hour, m = local.Minute, s = local.Second;
        Span<int> dg = stackalloc int[] { h / 10, h % 10, m / 10, m % 10, s / 10, s % 10 };
        for (int k = 0; k < _big.Length; k++)
        {
            char ch = (char)('0' + dg[k]);
            if (first) Immediate(ref _big[k], ch);
            else Aim(ref _big[k], ch, k * 0.04f); // щелчки разрядов по очереди, слева направо
        }

        string date = Wd[((int)local.DayOfWeek + 6) % 7] + " " + local.ToString("dd.MM.yyyy", Inv);
        for (int k = 0; k < _date.Length; k++)
        {
            if (first) Immediate(ref _date[k], date[k]);
            else Aim(ref _date[k], date[k], 0.3f + k * 0.03f);
        }

        // Прогон катодов: каждые 10 минут, ровно в :00.
        long key = h * 60L + m;
        if (m % 10 == 0 && s == 0 && key != _lastRunKey)
        {
            _lastRunKey = key;
            _runT = 0f;
        }

        RebuildRows();
    }

    /// <summary>Пересчитать строки мировых часов: город погоды (если есть), затем фиксированный набор.</summary>
    private void RebuildRows()
    {
        var utc = DateTime.SpecifyKind(_utc, DateTimeKind.Utc);
        var (decl, subLon) = Sun(utc);
        _subDecl = _hasClock ? decl : _subDecl;
        _subLon = _hasClock ? subLon : _subLon;
        int k = 0;
        if (_hasWeatherCity)
        {
            _rows[k++] = new Row
            {
                Name = _wName, Code = FixedCode(_wName) ?? _wCode, Lat = _wLat, Lon = _wLon,
                Off = TimeZoneInfo.Local.GetUtcOffset(utc),
                Day = SinElev(_wLat, _wLon, decl, subLon) > 0,
            };
        }
        foreach (var city in WorldCities)
        {
            if (_hasWeatherCity && city.Name == _wName) continue; // город погоды уже первой строкой
            _rows[k++] = new Row
            {
                Name = city.Name, Code = city.Code, Lat = city.Lat, Lon = city.Lon,
                Off = OffsetOf(city, utc),
                Day = SinElev(city.Lat, city.Lon, decl, subLon) > 0,
            };
        }
        _rowCount = k;
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;

        _sinceSnap = Math.Min(_sinceSnap + dt, SnapMaxAge);
        if (_runT >= 0f)
        {
            _runT += dt;
            if (_runT >= CathodeSec) _runT = -1f;
        }
        for (int k = 0; k < _big.Length; k++) Step(ref _big[k], dt);
        for (int k = 0; k < _date.Length; k++) Step(ref _date[k], dt);
    }

    public void Draw(CharGrid grid)
    {
        grid.Clear(Rgba.Black);
        switch (FaceIndex())
        {
            case 0: DrawFlap(grid); break;
            case 1: DrawNixie(grid); break;
            case 2: DrawWorld(grid); break;
            default: DrawAnalog(grid); break;
        }
    }

    // ---- Публичные диагностики (для тестов и отладки; в Draw не используются) ----

    /// <summary>Имя текущего циферблата: flap, nixie, world, analog.</summary>
    public string CurrentFace => Faces[FaceIndex()];

    /// <summary>Идёт ли прогон катодов.</summary>
    public bool CathodeRunning => _runT >= 0f;

    /// <summary>Знаки, которые показывают плашки табло (с учётом параметра seconds).</summary>
    public string ShownTime()
    {
        var sb = new StringBuilder();
        int n = _seconds ? 6 : 4;
        for (int k = 0; k < n; k++)
        {
            sb.Append(_big[k].Cur);
            if (k < n - 1 && IsGroupEnd(k)) sb.Append(':');
        }
        return sb.ToString();
    }

    /// <summary>Цифры в лампах ИН-14 (с учётом прогона катодов).</summary>
    public string LampDigits()
    {
        int n = _seconds ? 6 : 4;
        var chars = new char[n];
        for (int i = 0; i < n; i++)
        {
            int g = LampGlyph(i);
            chars[i] = g == DashG ? '-' : (char)('0' + g);
        }
        return new string(chars);
    }

    /// <summary>Строки мировых часов по порядку (первая — город погоды, если он есть).</summary>
    public string[] WorldRowNames()
    {
        var names = new string[_rowCount];
        for (int i = 0; i < _rowCount; i++) names[i] = _rows[i].Name;
        return names;
    }

    /// <summary>Коды строк мировых часов (для тестов).</summary>
    public string[] WorldRowCodes()
    {
        var codes = new string[_rowCount];
        for (int i = 0; i < _rowCount; i++) codes[i] = _rows[i].Code;
        return codes;
    }

    /// <summary>Углы стрелок в градусах от 12 часов по часовой: часовая, минутная, секундная.</summary>
    public static (float Hour, float Minute, float Second) HandAngles(DateTime t, bool sweep)
    {
        float sec = t.Second + t.Millisecond / 1000f;
        float hour = ((t.Hour % 12) + t.Minute / 60f + sec / 3600f) * 30f;
        float minute = (t.Minute + sec / 60f) * 6f;
        float second = sweep ? sec * 6f : t.Second * 6f;
        return (hour, minute, second);
    }

    /// <summary>Смещение города от UTC в момент utc (с учётом перехода на летнее время). Без зоны — фиксированное.</summary>
    public static TimeSpan OffsetOf(WorldCity city, DateTime utc)
    {
        var zone = ZoneOf(city.WinId);
        return zone != null
            ? zone.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc))
            : TimeSpan.FromHours(city.FallbackH);
    }

    /// <summary>Солнце над точкой (день) в момент utc. Терминатор — граница IsDay.</summary>
    public static bool IsDay(double latDeg, double lonDeg, DateTime utc)
    {
        var (decl, subLon) = Sun(utc);
        return SinElev(latDeg, lonDeg, decl, subLon) > 0;
    }

    // ---- Вспомогательные функции ----

    private int FaceIndex() => _face;

    private DateTime LocalNow() => _clock.AddSeconds(_sinceSnap);

    /// <summary>Доля текущей секунды (0..1) по оценке времени между снимками.</summary>
    private float NowFrac()
    {
        float f = (float)(_clock.Ticks % TimeSpan.TicksPerSecond) / TimeSpan.TicksPerSecond + _sinceSnap;
        return f % 1f;
    }

    private int TimeDigit(int i)
    {
        return i switch
        {
            0 => _clock.Hour / 10,
            1 => _clock.Hour % 10,
            2 => _clock.Minute / 10,
            3 => _clock.Minute % 10,
            4 => _clock.Second / 10,
            _ => _clock.Second % 10,
        };
    }

    private int LampGlyph(int i)
    {
        if (_runT >= 0f)
        {
            float t = _runT - i * 0.1f; // лампы стартуют по очереди
            if (t >= 0f) return (int)(t * 10f) % 10;
        }
        if (!_hasClock) return DashG;
        return TimeDigit(i);
    }

    private static bool IsGroupEnd(int k) => k == 1 || k == 3;

    /// <summary>Раскладка знаков с разделителями (после часов и минут — двоеточие). Возвращает ширину.</summary>
    private static int Pack(int n, int cellW, Span<int> xs)
    {
        int x = 0;
        for (int k = 0; k < n; k++)
        {
            xs[k] = x;
            x += cellW;
            if (k < n - 1) x += IsGroupEnd(k) ? 3 : 1;
        }
        return x;
    }

    // ---- Переворот плашек ----

    private static void Immediate(ref Flip f, char ch)
    {
        f.Cur = ch;
        f.Tgt = ch;
        f.From = ch;
        f.Stage = 0;
        f.Delay = 0f;
        f.T = 0f;
    }

    private static void Aim(ref Flip f, char ch, float delay)
    {
        if (f.Tgt == ch) return;
        f.Tgt = ch;
        f.Delay = delay;
    }

    private static void Step(ref Flip f, float dt)
    {
        if (f.Stage == 0)
        {
            if (f.Cur == f.Tgt) return;
            if (f.Delay > 0f)
            {
                f.Delay -= dt;
                return;
            }
            f.From = f.Cur;
            f.Stage = 1;
            f.T = 0f;
        }
        f.T += dt;
        if (f.T < FlipStep) return;
        f.T -= FlipStep;
        if (f.Stage == 1)
        {
            f.Stage = 2;
            return;
        }
        f.Stage = 0;
        f.Cur = f.Tgt;
    }

    // ---- Циферблат 1: табло split-flap ----

    private void DrawFlap(CharGrid g)
    {
        int n = _seconds ? 6 : 4;
        Span<int> xs = stackalloc int[6];
        int w = Pack(n, FlapW, xs);
        int ox = (g.Cols - w) / 2;
        int y0 = Math.Max(0, (g.Rows - (FlapH + 3)) / 2);
        for (int k = 0; k < n; k++) DrawBigFlap(g, ox + xs[k], y0, _big[k]);
        for (int k = 0; k < n - 1; k++)
        {
            if (!IsGroupEnd(k)) continue;
            int cx = ox + xs[k] + FlapW + 1;
            g.Put(cx, y0 + 2, '■', Warm);
            g.Put(cx, y0 + 4, '■', Warm);
        }
        int dx = (g.Cols - _date.Length) / 2;
        int dy = y0 + FlapH + 2;
        for (int k = 0; k < _date.Length; k++) DrawSmallFlap(g, dx + k, dy, _date[k]);
    }

    private static bool Lit(int g, int c, int r) => (uint)r < 5u && (uint)c < 3u && Font[g][r][c] == '#';

    private static int GlyphOf(char ch) => ch >= '0' && ch <= '9' ? ch - '0' : ch == '-' ? DashG : BlankG;

    private static void DrawBigFlap(CharGrid g, int x0, int y0, Flip f)
    {
        for (int pr = 0; pr < FlapH; pr++)
        for (int c = 0; c < 3; c++)
        {
            bool lit = false;
            Rgba fg = Warm;
            switch (f.Stage)
            {
                case 0:
                    lit = Lit(GlyphOf(f.Cur), c, pr - 1);
                    break;
                case 1:
                    if (pr <= 2) lit = Lit(GlyphOf(f.Tgt), c, pr - 1);          // верх уже новый
                    else if (pr >= 4)                                            // падающая половина — старая
                    {
                        lit = Lit(GlyphOf(f.From), c, pr - 4);
                        fg = Warm.Scale(0.45f);
                    }
                    break;
                default:
                    if (pr >= 3 && pr <= 5) lit = Lit(GlyphOf(f.Tgt), c, pr - 1); // низ новый
                    break;
            }
            if (lit) g.Put(x0 + c, y0 + pr, '█', fg, PlateBg);
            else if (pr == 3) g.Put(x0 + c, y0 + pr, '─', ShadowFg, PlateBg);
            else g.Put(x0 + c, y0 + pr, ' ', fg, PlateBg);
        }
    }

    private static void DrawSmallFlap(CharGrid g, int x, int y, Flip f)
    {
        switch (f.Stage)
        {
            case 1: g.Put(x, y, '▀', Warm.Scale(0.5f), PlateBg); break;
            case 2: g.Put(x, y, '▄', Warm.Scale(0.5f), PlateBg); break;
            default: g.Put(x, y, f.Cur, Warm.Scale(0.85f), PlateBg); break;
        }
    }

    // ---- Субпиксельный черновик (общий для ламп и аналогового циферблата) ----

    private void BeginDraw(CharGrid g)
    {
        int w2 = g.Cols * 2, h3 = g.Rows * 3;
        if (_sub.Length != w2 * h3) _sub = new byte[w2 * h3];
        else Array.Clear(_sub);
        _subW = w2;
        _subH = h3;
    }

    private void Plot(int x, int y, byte code)
    {
        if ((uint)x < (uint)_subW && (uint)y < (uint)_subH) _sub[y * _subW + x] = code;
    }

    private static int Round(double v) => (int)Math.Round(v);

    /// <summary>Есть ли в ячейке (x,y) горящая проволока (код 2).</summary>
    private bool CellLit(int x, int y)
    {
        if ((uint)x >= (uint)(_subW / 2) || (uint)y >= (uint)(_subH / 3)) return false;
        for (int dy = 0; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
                if (_sub[(y * 3 + dy) * _subW + x * 2 + dx] == 2) return true;
        return false;
    }

    /// <summary>Ореол: в одной из восьми соседних ячеек горит проволока.</summary>
    private bool NearLit(int x, int y)
    {
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if ((dx != 0 || dy != 0) && CellLit(x + dx, y + dy)) return true;
        return false;
    }

    private void ScanCell(int x, int y, out int bits, out byte best)
    {
        bits = 0;
        best = 0;
        // На крошечной сетке лампы (слот не уже 8 ячеек) вылезают за край — там субпикселей нет
        if ((uint)x >= (uint)(_subW / 2) || (uint)y >= (uint)(_subH / 3)) return;
        for (int dy = 0; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                byte code = _sub[(y * 3 + dy) * _subW + x * 2 + dx];
                if (code == 0) continue;
                bits |= 1 << (dy * 2 + dx);
                if (code > best) best = code;
            }
    }

    private void SegLine(double x0, double y0, double x1, double y1, byte code, int sx0, int sy0)
    {
        double len = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        int steps = (int)Math.Ceiling(len * 2) + 1;
        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            Plot(sx0 + Round(x0 + (x1 - x0) * t), sy0 + Round(y0 + (y1 - y0) * t), code);
        }
    }

    /// <summary>Семисегментная цифра проволокой: сегменты из строки segs, размер w×h субпикселей, начало (sx0,sy0).</summary>
    private void DrawSegments(int sx0, int sy0, int w, int h, string segs, byte code)
    {
        double wd = w - 1, ht = h - 1, mid = ht / 2;
        foreach (char s in segs)
        {
            switch (s)
            {
                case 'a': SegLine(0, 0, wd, 0, code, sx0, sy0); break;
                case 'b': SegLine(wd, 0, wd, mid, code, sx0, sy0); break;
                case 'c': SegLine(wd, mid, wd, ht, code, sx0, sy0); break;
                case 'd': SegLine(0, ht, wd, ht, code, sx0, sy0); break;
                case 'e': SegLine(0, mid, 0, ht, code, sx0, sy0); break;
                case 'f': SegLine(0, 0, 0, mid, code, sx0, sy0); break;
                case 'g': SegLine(0, mid, wd, mid, code, sx0, sy0); break;
            }
        }
    }

    // ---- Циферблат 2: ряд ламп ИН-14 с проволочными цифрами ----

    private void DrawNixie(CharGrid g)
    {
        BeginDraw(g);
        int cols = g.Cols, rows = g.Rows;
        int n = _seconds ? 6 : 4;
        int seps = n / 2 - 1;

        // Ряд ламп занимает ~70% ширины экрана.
        int slot = Math.Max(8, (int)((0.7 * cols - seps * SepW) / n));
        int total = n * slot + seps * SepW;
        int ox = (cols - total) / 2;
        Span<int> lx = stackalloc int[6];
        int x = ox;
        for (int i = 0; i < n; i++)
        {
            lx[i] = x;
            x += slot;
            if (i < n - 1 && IsGroupEnd(i)) x += SepW;
        }

        // Цифра ~40% высоты экрана; пропорция ширины к высоте ≈ 0.5 (физически), вписана в слот.
        double k = SubK;
        int dw = slot - 4;                          // в ячейках, по 2 поля с каждой стороны
        double ws = dw * 2.0, hs = ws * k / 0.5;    // субпиксели
        double maxHs = Math.Min(0.4 * rows * 3, (rows - 4) * 3.0);
        if (hs > maxHs)
        {
            hs = maxHs;
            ws = hs * 0.5 / k;
        }
        int wsI = Math.Max(2, Round(ws)), hsI = Math.Max(3, Round(hs));
        int dCellW = Math.Min(slot - 2, (wsI + 1) / 2);
        int digitRows = (hsI + 2) / 3;
        int lampRows = Math.Min(rows, digitRows + 4);
        int y0 = Math.Max(0, (rows - lampRows) / 2);
        int sy0 = (y0 + 2) * 3;

        // Призраки всех цифр — тусклые штрихи; горящая цифра — поверх.
        for (int i = 0; i < n; i++)
        {
            int sx0 = (lx[i] + (slot - dCellW) / 2) * 2;
            DrawSegments(sx0, sy0, wsI, hsI, AllSegments, 1);
        }
        for (int i = 0; i < n; i++)
        {
            int sx0 = (lx[i] + (slot - dCellW) / 2) * 2;
            int d = LampGlyph(i);
            DrawSegments(sx0, sy0, wsI, hsI, Segments[Math.Clamp(d, 0, Segments.Length - 1)], 2);
        }

        // Стекло: скруглённый корпус, сетка-анод ▒ в фоне, проволока — из субпикселей.
        for (int i = 0; i < n; i++)
        {
            for (int cy = y0; cy < y0 + lampRows; cy++)
            for (int cx = lx[i]; cx < lx[i] + slot; cx++)
            {
                int ix = cx - lx[i], iy = cy - y0;
                bool top = iy == 0, bottom = iy == lampRows - 1, left = ix == 0, right = ix == slot - 1;
                if (top || bottom || left || right)
                {
                    char ch = (top, left, bottom, right) switch
                    {
                        (true, true, _, _) => '╭',
                        (true, _, _, true) => '╮',
                        (_, true, true, _) => '╰',
                        (_, _, true, true) => '╯',
                        (true, _, _, _) or (_, _, true, _) => '─',
                        _ => '│',
                    };
                    g.Put(cx, cy, ch, TubeFg, Glass);
                    continue;
                }
                ScanCell(cx, cy, out int bits, out byte best);
                Rgba bg = NearLit(cx, cy) ? Halo : Glass;
                if (bits == 0) g.Put(cx, cy, '▒', MeshFg, bg);
                else g.Put(cx, cy, GlyphSet.Sextant(bits), best == 2 ? NeonLit : GhostFg, bg);
            }
        }

        // Неоновые точки между парами ламп мигают раз в секунду.
        bool dotOn = NowFrac() < 0.5f;
        for (int k2 = 0; k2 < n - 1; k2++)
        {
            if (!IsGroupEnd(k2)) continue;
            int cx = lx[k2] + slot + 1;
            char dot = dotOn ? '●' : '·';
            Rgba fg = dotOn ? NeonDot : NeonOff;
            g.Put(cx, y0 + lampRows / 3, dot, fg);
            g.Put(cx, y0 + 2 * lampRows / 3, dot, fg);
        }
    }

    // ---- Циферблат 3: мировые часы ----

    private void DrawWorld(CharGrid g)
    {
        int cols = g.Cols;
        g.Text(NameX, 1, "ГОРОД", Dim);
        g.Text(TimeX, 1, "ВРЕМЯ", Dim);
        g.Text(OffX, 1, "СМЕЩЕНИЕ", Dim);
        g.Text(StatX, 1, "СУТКИ", Dim);
        for (int x2 = 0; x2 < cols; x2++) g.Put(x2, 2, '─', Dim);

        DateTime est = _utc.AddSeconds(_sinceSnap);
        for (int i = 0; i < _rowCount; i++)
        {
            int y = 3 + i;
            Row r = _rows[i];
            Rgba band = r.Day ? DayBand : NightBand;
            for (int x2 = 0; x2 < cols; x2++) g.Put(x2, y, ' ', Warm, band);
            g.Text(NameX, y, r.Name, Warm, band);

            if (!_hasClock)
            {
                g.Text(TimeX, y, "--:--:--", Dim, band);
            }
            else
            {
                DateTime t = est.Add(r.Off);
                Num2(g, TimeX, y, t.Hour, Warm, band);
                g.Put(TimeX + 2, y, ':', Warm, band);
                Num2(g, TimeX + 3, y, t.Minute, Warm, band);
                if (_seconds)
                {
                    g.Put(TimeX + 5, y, ':', Warm, band);
                    Num2(g, TimeX + 6, y, t.Second, Warm, band);
                }

                TimeSpan off = r.Off;
                g.Text(OffX, y, "UTC", Cold, band);
                g.Put(OffX + 3, y, off < TimeSpan.Zero ? '-' : '+', Cold, band);
                int mins = (int)Math.Abs(off.TotalMinutes);
                Num2(g, OffX + 4, y, mins / 60, Cold, band);
                g.Put(OffX + 6, y, ':', Cold, band);
                Num2(g, OffX + 7, y, mins % 60, Cold, band);
            }

            bool day = _hasClock && r.Day;
            g.Put(StatX, y, day ? '☼' : '◦', day ? Amber : NightFg, band);
            g.Text(StatX + 2, y, day ? "ДЕНЬ" : "НОЧЬ", day ? Amber : NightFg, band);
        }

        // Карта: пропорции по физическим пикселям (360° по ширине ↔ 180° по высоте).
        int y0 = 3 + _rowCount + 1;
        int mh = g.Rows - y0 - 1;
        int mw = cols - 4;
        if (mw > 2 * _aspect * mh) mw = (int)(2 * _aspect * mh);
        else mh = (int)(mw / (2.0 * _aspect));
        if (mw >= 8 && mh >= 4) DrawMap(g, 2 + (cols - 4 - mw) / 2, y0, mw, mh);
    }

    /// <summary>
    /// Карта без береговых линий: день — тусклая заливка, ночь — тёмная, терминатор — яркая линия,
    /// сетка меридианов и параллелей через 30°, города — ● с трёхбуквенным кодом.
    /// </summary>
    private void DrawMap(CharGrid g, int x0, int y0, int w, int h)
    {
        double dLon = 360.0 / w, dLat = 180.0 / h;
        for (int my = 0; my < h; my++)
        {
            double lat = 90 - (my + 0.5) * dLat;
            double pl = Math.Round(lat / 30) * 30;
            bool par = Math.Abs(lat - pl) < dLat / 2;
            for (int mx = 0; mx < w; mx++)
            {
                double lon = -180 + (mx + 0.5) * dLon;
                bool day = SinElev(lat, lon, _subDecl, _subLon) > 0;
                bool dayL = SinElev(lat, lon - dLon, _subDecl, _subLon) > 0;
                bool dayU = SinElev(lat + dLat, lon, _subDecl, _subLon) > 0;
                bool term = dayL != day || dayU != day;
                Rgba fill = day ? DayFill : NightFill;
                double ml = Math.Round(lon / 30) * 30;
                bool mer = Math.Abs(lon - ml) < dLon / 2;

                if (term) g.Put(x0 + mx, y0 + my, '░', TermFg, fill);
                else if (mer && par) g.Put(x0 + mx, y0 + my, '┼', GratFg, fill);
                else if (mer) g.Put(x0 + mx, y0 + my, '│', GratFg, fill);
                else if (par) g.Put(x0 + mx, y0 + my, '─', GratFg, fill);
                else g.Put(x0 + mx, y0 + my, ' ', Warm, fill);
            }
        }

        for (int i = 0; i < _rowCount; i++)
        {
            Row r = _rows[i];
            int mx = Math.Clamp((int)((r.Lon + 180) / 360 * w), 0, w - 1);
            int my = Math.Clamp((int)((90 - r.Lat) / 180 * h), 0, h - 1);
            g.Put(x0 + mx, y0 + my, '●', Amber);
            // Подпись справа от точки, если влезает; иначе слева.
            int lx = mx + 2 + 3 <= w ? x0 + mx + 2 : x0 + mx - 4;
            if (lx >= x0) g.Text(lx, y0 + my, r.Code, Amber);
        }
    }

    private static void Num2(CharGrid g, int x, int y, int v, Rgba fg, Rgba bg)
    {
        v = Math.Clamp(v, 0, 99);
        g.Put(x, y, (char)('0' + v / 10), fg, bg);
        g.Put(x + 1, y, (char)('0' + v % 10), fg, bg);
    }

    private const int NameX = 2, TimeX = 18, OffX = 28, StatX = 39; // колонки таблицы мировых часов

    private static TimeZoneInfo? ZoneOf(string winId)
    {
        if (ZoneCache.TryGetValue(winId, out var z)) return z;
        try
        {
            z = TimeZoneInfo.FindSystemTimeZoneById(winId);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            z = null;
        }
        ZoneCache[winId] = z;
        return z;
    }

    /// <summary>Солнце: склонение и долгота подсолнечной точки (радианы). Склонение — формула Купера.</summary>
    private static (double Decl, double SubLon) Sun(DateTime utc)
    {
        double decl = -0.4091 * Math.Cos(2 * Math.PI / 365.0 * (utc.DayOfYear + 10));
        double h = utc.Hour + utc.Minute / 60.0 + utc.Second / 3600.0;
        return (decl, (12 - h) * Math.PI / 12); // 15° в час, полдень UTC — подсолнечная точка на 0°
    }

    /// <summary>
    /// Синус высоты Солнца над точкой: sin h = sin φ·sin δ + cos φ·cos δ·cos H, H — часовой угол
    /// (H = λ − λ_подсолнечной). &gt; 0 — день.
    /// </summary>
    private static double SinElev(double latDeg, double lonDeg, double decl, double subLon)
    {
        double lat = latDeg * Math.PI / 180, lon = lonDeg * Math.PI / 180;
        return Math.Sin(lat) * Math.Sin(decl) + Math.Cos(lat) * Math.Cos(decl) * Math.Cos(lon - subLon);
    }

    // ---- Циферблат 4: аналоговый, на секстантах ----

    private void DrawAnalog(CharGrid g)
    {
        BeginDraw(g);
        int cols = g.Cols, rows = g.Rows;
        int w2 = cols * 2, h3 = rows * 3;

        // Субпиксель 2×3 в ячейке; ширина субпикселя = SubK × высота — круг корректируем.
        double cx = (w2 - 1) / 2.0, cy = (h3 - 1) / 2.0;
        double r = Math.Min((h3 - 1) / 2.0, SubK * (w2 - 1) / 2.0) - 0.5;
        if (r >= 3)
        {
            for (int i = 0; i < 60; i++)
            {
                double a = i * Math.PI / 30;
                double r0 = r * (i % 5 == 0 ? 0.78 : 0.9);
                for (double t = r0; t <= r; t += 0.5) Plot(Round(cx + t * Math.Sin(a) / SubK), Round(cy - t * Math.Cos(a)), 2);
            }
            for (int d = 0; d < 360; d += 2)
            {
                double a = d * Math.PI / 180;
                Plot(Round(cx + r * Math.Sin(a) / SubK), Round(cy - r * Math.Cos(a)), 2);
            }

            if (_hasClock)
            {
                var (hA, mA, sA) = HandAngles(LocalNow(), _sweep);
                // Тень (сдвиг на субпиксель) — сначала, потом стрелки поверх.
                Hand(cx, cy, hA, -r * 0.12, r * 0.5, 1, true, 1);
                Hand(cx, cy, mA, -r * 0.15, r * 0.8, 1, true, 1);
                Hand(cx, cy, sA, -r * 0.25, r * 0.9, 1, false, 1);
                Hand(cx, cy, hA, -r * 0.12, r * 0.5, 3, true, 0);
                Hand(cx, cy, mA, -r * 0.15, r * 0.8, 4, true, 0);
                Hand(cx, cy, sA, -r * 0.25, r * 0.9, 5, false, 0);
            }
            for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                    Plot(Round(cx) + dx, Round(cy) + dy, 6);
        }

        FlushDial(g);
    }

    private void Hand(double cx, double cy, float deg, double rFrom, double rTo, byte code, bool thick, int shift)
    {
        double a = deg * Math.PI / 180;
        for (double t = rFrom; t <= rTo; t += 0.25)
        {
            int x = Round(cx + t * Math.Sin(a) / SubK) + shift;
            int y = Round(cy - t * Math.Cos(a)) + shift;
            Plot(x, y, code);
            if (thick) Plot(x + 1, y, code);
        }
    }

    private void FlushDial(CharGrid g)
    {
        for (int cy = 0; cy < g.Rows; cy++)
        for (int cx = 0; cx < g.Cols; cx++)
        {
            ScanCell(cx, cy, out int bits, out byte best);
            if (bits == 0) g.Put(cx, cy, ' ', Warm, Rgba.Black);
            else g.Put(cx, cy, GlyphSet.Sextant(bits), CodeColor(best), Rgba.Black);
        }
    }

    private static Rgba CodeColor(byte code) => code switch
    {
        1 => Rgba.Hex(0x202020),
        2 => Dim,
        3 => Warm,
        4 => Warm,
        5 => NeonDot,
        _ => NeonLit,
    };
}
