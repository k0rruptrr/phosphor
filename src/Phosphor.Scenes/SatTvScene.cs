using System.Globalization;
using System.Text;
using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// «Сат-ТВ»: титры распределения транспондеров, как в спутниковом ТВ начала 90-х. Один эфир = один спутник:
/// фон (полосы «жалюзи», синий, градиент), эмблема-антенна, многоязычная шапка, название спутника пиксельным шрифтом
/// и компактная центрированная таблица каналов. Всё уезжает вверх по строке за scrollSec, затем пауза и следующий спутник.
/// Данные — снимки хоста ("sattv"); без данных — экран поиска спутника (ничего не выдумываем).
/// </summary>
public sealed class SatTvScene : IScene, ITextScene, IDataScene, IPreferredRows, ISceneParams
{
    // Крупная ячейка: на экран ~30 строк, как на референсах (текст крупный, шапка занимает ширину).
    public int PreferredRows => 30;

    // Параметры для окна настроек.
    public static readonly SceneParamSpec ScrollSpec = new("scrollSec", SceneParamKind.Float, 0.35f, 0.1f, 2f, "с на строку титров");
    public static readonly SceneParamSpec MaxChSpec = new("maxChannels", SceneParamKind.Int, 60f, 10f, 200f, "каналов на один эфир");
    public static readonly SceneParamSpec StyleSpec = new("style", SceneParamKind.Choice, 0f, 0f, 3f, "фон эфира",
        new[] { "auto", "blinds", "blue", "gradient" });
    private static readonly SceneParamSpec[] ParamList = { ScrollSpec, MaxChSpec, StyleSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    public string Id => "sattv";

    private static readonly string[] Keys = { "sattv", "clock" };
    public IReadOnlyList<string> DataKeys => Keys;

    private const int ModeSearch = 0, ModeScroll = 1, ModePause = 2;
    private const float PauseSec = 2f;
    private const float FreqStepSec = 0.15f, SignalStepSec = 0.2f;
    private const double FreqFrom = 10700.0, FreqSpan = 2050.0, FreqStep = 25.0;
    private const int Margin = 2;
    private const int EmblemW = 12, EmblemH = 8;

    // Цвета (доли 0..1 из референсов).
    private static readonly Rgba BlindLight = F(0.55f, 0.55f, 0.57f);
    private static readonly Rgba BlindDark = F(0.30f, 0.30f, 0.32f);
    private static readonly Rgba BlueBg = F(0.05f, 0.12f, 0.62f);
    private static readonly Rgba GradTop = F(0.08f, 0.10f, 0.55f);
    private static readonly Rgba GradBot = F(0.45f, 0.12f, 0.50f);
    private static readonly Rgba TextWhite = F(0.95f, 0.95f, 0.95f);
    private static readonly Rgba TextFta = F(0.93f, 0.93f, 0.93f);
    private static readonly Rgba TextCoded = F(0.62f, 0.62f, 0.62f);
    private static readonly Rgba EmblemBlue = F(0.10f, 0.20f, 0.85f);
    private static readonly Rgba Yellow = F(0.95f, 0.88f, 0.25f);

    // Шапка: из пяти языков на каждом эфире берутся три случайных.
    private static readonly string[] Templates =
    {
        "THE <SAT> CHANNEL ALLOCATION AS OF TODAY READS AS FOLLOWS:",
        "HIER DIE AKTUELLE <SAT> TRANSPONDERBELEGUNG:",
        "LA RÉPARTITION DES RÉPÉTEURS DE <SAT> SE PRÉSENTE COMME SUIT:",
        "РАСПРЕДЕЛЕНИЕ ТРАНСПОНДЕРОВ <SAT> НА СЕГОДНЯ:",
        "DE ACTUELE KANAALINDELING VAN <SAT>:",
    };

    private const string SearchRu = "ПОИСК СПУТНИКА...";
    private const string SearchEn = "SEARCHING SATELLITE...";
    private const int ProgWanted = 22;      // PROGRAMME — до 22 символов, остальные колонки фиксированы
    private const int GapW = 2;             // зазор между колонками

    // Эмблема: параболическая антенна с двумя дугами сигнала (собственный рисунок, не логотип), секстанты 12×8.
    private static readonly int[] EmblemBits = BuildEmblem();

    private Random _rng = new(0);
    private int _cols, _rows;
    private float _scrollSec = 0.35f;
    private int _maxCh = 60;
    private int _style;                       // 0 — auto (по кругу), 1..3 — фиксированный фон
    private SatTvSetSnapshot? _set;
    private bool _hasClock;
    private DateTime _clock;

    private int _epoch;                       // номер эфира
    private int _bgStyle = 1;                 // фон текущего эфира: 1 блинды, 2 синий, 3 градиент
    private int _mode = ModeSearch;
    private float _acc, _pauseT;
    private int _top;                         // строка сетки, на которой стоит первая строка контента (может быть < 0)
    private int _contentLen;
    private Cell[] _strip = Array.Empty<Cell>(); // контент эфира: строки по _cols клеток, Ch == '\0' — пусто
    private int _wCh, _wPol, _wProg, _wFreq, _wSr, _wLang; // ширины колонок таблицы текущего эфира (0 — колонка снята)
    private int _tableW, _tableX;             // ширина таблицы и её левый край (таблица центрируется по сетке)

    // Экран поиска.
    private float _freqT, _signalT;
    private int _freqStep;
    private string _freqText = "";
    private string _signalText = "";

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = cols;
        _rows = rows;
        _rng = rng;
        _scrollSec = Math.Clamp(options.GetFloat(ScrollSpec.Name, ScrollSpec.Default), ScrollSpec.Min, ScrollSpec.Max);
        _maxCh = Math.Clamp(options.GetInt(MaxChSpec.Name, (int)MaxChSpec.Default), (int)MaxChSpec.Min, (int)MaxChSpec.Max);
        var styleName = options.GetString(StyleSpec.Name, StyleSpec.Options![(int)StyleSpec.Default]);
        _style = Math.Max(0, Array.FindIndex(StyleSpec.Options!, o => string.Equals(o, styleName, StringComparison.OrdinalIgnoreCase)));

        _set = null;
        _hasClock = false;
        _epoch = 0;
        _bgStyle = _style > 0 ? _style : 1;
        _mode = ModeSearch;
        _acc = 0;
        _pauseT = 0;
        _top = _rows;
        _contentLen = 0;
        _strip = new Cell[Math.Max(1, _cols) * 64];
        _freqT = 0;
        _signalT = 0;
        _freqStep = 0;
        UpdateFreqText();
        UpdateSignalText();
    }

    public void OnData(string key, DataSnapshot snapshot)
    {
        if (key == "clock" && snapshot is ClockSnapshot clock)
        {
            _hasClock = true;
            _clock = clock.Local;
        }
        else if (key == "sattv" && snapshot is SatTvSetSnapshot set)
        {
            _set = set;
        }
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;

        if (_mode == ModeSearch)
        {
            UpdateSearch(dt);
            if (HasSatellites()) StartEpisode();
            return;
        }

        if (_mode == ModeScroll)
        {
            _acc += dt;
            while (_acc >= _scrollSec && _mode == ModeScroll)
            {
                _acc -= _scrollSec;
                _top--;
                // Последняя строка таблицы ушла за верх — конец эфира.
                if (_top + _contentLen - 1 < 0)
                {
                    _mode = ModePause;
                    _pauseT = 0;
                    _acc = 0;
                }
            }
            return;
        }

        _pauseT += dt;
        if (_pauseT >= PauseSec)
        {
            _epoch++;
            if (HasSatellites()) StartEpisode();
            else _mode = ModeSearch;
        }
    }

    public void Draw(CharGrid grid)
    {
        int bs = _mode == ModeSearch ? 2 : _bgStyle;
        FillBackground(grid, bs);
        if (_mode == ModeSearch)
        {
            DrawSearch(grid);
            return;
        }

        // Контент эфира поверх фона: клетки с символом перекрывают фон; фон клетки — среднее двух полублоков.
        int cols = Math.Min(grid.Cols, _cols);
        for (int i = 0; i < _contentLen; i++)
        {
            int y = _top + i;
            if ((uint)y >= (uint)grid.Rows) continue;
            var avg = Rgba.Lerp(BgAt(2 * y, bs), BgAt(2 * y + 1, bs), 0.5f);
            int rowBase = i * _cols;
            for (int x = 0; x < cols; x++)
            {
                var c = _strip[rowBase + x];
                if (c.Ch == '\0') continue;
                grid.Cells[y * grid.Cols + x] = new Cell(c.Ch, c.Fg, avg);
            }
        }
    }

    // ---- Эфир ----

    private bool HasSatellites() => _set is not null && _set.Satellites.Count > 0;

    private void StartEpisode()
    {
        var sats = _set!.Satellites;
        var sat = sats[_epoch % sats.Count];
        _bgStyle = _style > 0 ? _style : (_epoch % 3) + 1;
        BuildEpisode(sat);
        _top = _rows;
        _acc = 0;
        _pauseT = 0;
        _mode = ModeScroll;
    }

    /// <summary>Раскладывает контент эфира в буфер строк. Вызывается только при смене спутника (не на каждом кадре).</summary>
    private void BuildEpisode(SatTvSatellite sat)
    {
        _contentLen = 0;
        Array.Clear(_strip, 0, _strip.Length);
        int w = Math.Max(1, _cols - 2 * Margin);
        var emblemColor = _bgStyle == 1 ? EmblemBlue : TextWhite;

        // 1. Эмблема (только в начале эфира) и позиция спутника под ней.
        int ex = Math.Max(0, (_cols - EmblemW) / 2);
        for (int r = 0; r < EmblemH; r++)
        {
            int row = AddLine();
            for (int cx = 0; cx < EmblemW; cx++)
            {
                int bits = EmblemBits[r * EmblemW + cx];
                if (bits != 0) PutCell(row, ex + cx, GlyphSet.Sextant(bits), emblemColor);
            }
        }
        PutCentered(AddLine(), Clean(sat.Position), TextWhite);
        AddLine();

        // 2. Шапка: три случайных языка из пяти, по словам в ширину сетки.
        var order = new int[Templates.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        for (int i = 0; i < 3; i++)
        {
            int j = i + _rng.Next(order.Length - i);
            (order[i], order[j]) = (order[j], order[i]);
        }
        string satUpper = Clean(sat.Name);
        for (int k = 0; k < 3; k++)
        {
            if (k > 0) AddLine();
            string text = Templates[order[k]].Replace("<SAT>", satUpper);
            foreach (var line in Wrap(text, w)) PutCentered(AddLine(), line, TextWhite);
        }
        if (_hasClock)
        {
            AddLine();
            PutCentered(AddLine(), _clock.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), TextWhite);
        }
        AddLine();

        // 3. Название спутника крупно (пиксельный шрифт), на узкой сетке — обычным текстом.
        if (_cols >= 60 && IsPixelText(satUpper) && satUpper.Length * PixelFont.Advance <= w)
        {
            int top = AddLine();
            AddLine();
            AddLine();
            int nx = Math.Max(0, (_cols - (satUpper.Length * PixelFont.Advance - 1)) / 2);
            for (int i = 0; i < satUpper.Length; i++)
            {
                var bits = PixelFont.Bits(satUpper[i]);
                for (int cy = 0; cy < PixelFont.CellsH; cy++)
                    for (int cx = 0; cx < PixelFont.CellsW; cx++)
                    {
                        int b = bits[cy * PixelFont.CellsW + cx];
                        if (b != 0) PutCell(top + cy, nx + i * PixelFont.Advance + cx, GlyphSet.Sextant(b), Yellow);
                    }
            }
        }
        else
        {
            PutCentered(AddLine(), satUpper, Yellow);
        }
        AddLine();

        // 4. Таблица: заголовок с подчёркиванием, затем каналы (окно из maxChannels).
        ComputeColumns();
        PutText(AddLine(), _tableX, HeaderRow(), TextWhite);
        PutText(AddLine(), _tableX, new string('─', _tableW), TextWhite);

        int n = sat.Channels.Count;
        int m = Math.Min(n, _maxCh);
        int start = n > m ? _rng.Next(0, n - m + 1) : 0;
        for (int i = 0; i < m; i++)
        {
            var ch = sat.Channels[start + i];
            var color = ch.Fta ? TextFta : TextCoded;
            PutText(AddLine(), _tableX, ChannelRow(i + 1, ch), color);
        }
        AddLine();
    }

    /// <summary>
    /// Колонки фиксированной ширины: CH 3, POL 3, PROGRAMME до 22 (остаток), FREQ 9, SR 5, LANGUAGE 11, зазор 2.
    /// Таблица центрируется по ширине сетки. Если PROGRAMME меньше 8 символов — снимаются LANGUAGE, затем SR.
    /// </summary>
    private void ComputeColumns()
    {
        _wCh = 3; _wPol = 3; _wFreq = 9; _wSr = 5; _wLang = 11;
        int avail = _cols - 2 * Margin;
        _wProg = FitProgramme(avail);
        if (_wProg < 8) { _wLang = 0; _wProg = FitProgramme(avail); }
        if (_wProg < 8) { _wSr = 0; _wProg = FitProgramme(avail); }
        _tableW = TableWidth(_wProg);
        _tableX = Math.Max(0, (_cols - _tableW) / 2);
    }

    private int FitProgramme(int avail)
    {
        int others = TableWidth(0);
        int room = avail - others - (others > 0 ? GapW : 0);
        return Math.Clamp(room, 0, ProgWanted);
    }

    /// <summary>Ширина таблицы: сумма ненулевых колонок и зазоры между ними.</summary>
    private int TableWidth(int prog)
    {
        int sum = 0, n = 0;
        foreach (int w in new[] { _wCh, _wPol, prog, _wFreq, _wSr, _wLang })
            if (w > 0) { sum += w; n++; }
        return sum + GapW * Math.Max(0, n - 1);
    }

    private string HeaderRow() => TableRow("CH", "POL", "PROGRAMME", "FREQ", "SR", "LANGUAGE");

    private string ChannelRow(int number, SatTvChannel ch) => TableRow(
        number.ToString(CultureInfo.InvariantCulture),
        ch.Pol.ToString(),
        Clean(ch.Name),
        ch.FreqMhz.ToString("F2", CultureInfo.InvariantCulture),
        ch.SymbolRate > 0 ? ch.SymbolRate.ToString(CultureInfo.InvariantCulture) : "-",
        Clean(ch.Country));

    /// <summary>Строка таблицы: ячейки колонок с ненулевой шириной через зазор.</summary>
    private string TableRow(string ch, string pol, string prog, string freq, string sr, string lang)
    {
        var sb = new StringBuilder();
        AddCell(sb, ch, _wCh);
        AddCell(sb, pol, _wPol);
        AddCell(sb, prog, _wProg);
        AddCell(sb, freq, _wFreq);
        AddCell(sb, sr, _wSr);
        AddCell(sb, lang, _wLang);
        return sb.ToString();
    }

    private void AddCell(StringBuilder sb, string text, int width)
    {
        if (width <= 0) return;
        if (sb.Length > 0) sb.Append(' ', GapW);
        sb.Append(Fit(text, width));
    }

    // ---- Экран поиска ----

    private void UpdateSearch(float dt)
    {
        _freqT += dt;
        if (_freqT >= FreqStepSec)
        {
            _freqT = 0;
            _freqStep++;
            UpdateFreqText();
        }
        _signalT += dt;
        if (_signalT >= SignalStepSec)
        {
            _signalT = 0;
            UpdateSignalText();
        }
    }

    private void UpdateFreqText()
    {
        double f = FreqFrom + (_freqStep * FreqStep) % FreqSpan;
        _freqText = f.ToString("F2", CultureInfo.InvariantCulture) + " MHZ";
    }

    private void UpdateSignalText()
    {
        int lit = _rng.Next(0, 7);
        var bar = new char[8];
        for (int i = 0; i < bar.Length; i++) bar[i] = i < lit ? '█' : ' ';
        _signalText = "SIGNAL [" + new string(bar) + "]";
    }

    private void DrawSearch(CharGrid grid)
    {
        int cy = grid.Rows / 2;
        CenterText(grid, cy - 3, SearchRu, TextWhite);
        CenterText(grid, cy - 2, SearchEn, TextWhite);
        CenterText(grid, cy, _freqText, Yellow);
        CenterText(grid, cy + 2, _signalText, TextWhite);
    }

    private static void CenterText(CharGrid grid, int y, string s, Rgba fg)
    {
        int x = Math.Max(0, (grid.Cols - s.Length) / 2);
        grid.Text(x, y, s, fg);
    }

    // ---- Фон ----

    private void FillBackground(CharGrid grid, int bs)
    {
        for (int y = 0; y < grid.Rows; y++)
        {
            var top = BgAt(2 * y, bs);
            var bot = BgAt(2 * y + 1, bs);
            int rowBase = y * grid.Cols;
            for (int x = 0; x < grid.Cols; x++)
                grid.Cells[rowBase + x] = top == bot
                    ? new Cell(' ', TextWhite, top)
                    : new Cell('▀', top, bot);
        }
    }

    /// <summary>Цвет полублока sub (две строки на клетку). Блинды — полосы с резким скачком, градиент — по высоте.</summary>
    private Rgba BgAt(int sub, int bs)
    {
        int h2 = Math.Max(2, _rows * 2);
        switch (bs)
        {
            case 1:
            {
                int band = Math.Max(1, h2 / 16);
                float t = ((sub % band) + 0.5f) / band;
                return Rgba.Lerp(BlindLight, BlindDark, t);
            }
            case 3:
            {
                float t = Math.Clamp(sub / (float)(h2 - 1), 0f, 1f);
                return Rgba.Lerp(GradTop, GradBot, t);
            }
            default:
                return BlueBg;
        }
    }

    // ---- Строковый буфер эфира ----

    private int AddLine()
    {
        int row = _contentLen++;
        int need = _contentLen * _cols;
        if (need > _strip.Length)
            Array.Resize(ref _strip, Math.Max(need, _strip.Length * 2));
        Array.Clear(_strip, row * _cols, _cols);
        return row;
    }

    private void PutCell(int row, int x, char ch, Rgba fg)
    {
        if ((uint)x >= (uint)_cols) return;
        _strip[row * _cols + x] = new Cell(ch, fg, Rgba.Transparent);
    }

    private void PutText(int row, int x, string s, Rgba fg)
    {
        for (int i = 0; i < s.Length; i++)
            if (s[i] != ' ') PutCell(row, x + i, s[i], fg);
    }

    private void PutCentered(int row, string s, Rgba fg) =>
        PutText(row, Math.Max(0, (_cols - s.Length) / 2), s, fg);

    private static bool IsPixelText(string s)
    {
        foreach (var c in s)
            if (c != ' ' && !PixelFont.Has(c)) return false;
        return true;
    }

    /// <summary>Перенос по словам. Слово длиннее ширины режется.</summary>
    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var cur = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = word;
            while (w.Length > width)
            {
                if (cur.Length > 0) { lines.Add(cur); cur = ""; }
                lines.Add(w[..width]);
                w = w[width..];
            }
            if (cur.Length == 0) cur = w;
            else if (cur.Length + 1 + w.Length <= width) cur += " " + w;
            else { lines.Add(cur); cur = w; }
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }

    private static string Clean(string s) => GlyphSet.Sanitize(s.ToUpperInvariant());

    private static string Fit(string s, int width)
    {
        if (width <= 0) return "";
        return s.Length > width ? s[..width] : s.PadRight(width);
    }

    private static Rgba F(float r, float g, float b) => new(
        (byte)Math.Clamp(r * 255f + 0.5f, 0f, 255f),
        (byte)Math.Clamp(g * 255f + 0.5f, 0f, 255f),
        (byte)Math.Clamp(b * 255f + 0.5f, 0f, 255f));

    /// <summary>Эмблема в подпикселях 24×24 (2×3 на секстант): нижний сегмент — тарелка, над ней две дуги и облучатель.</summary>
    private static int[] BuildEmblem()
    {
        const int W = EmblemW * 2, H = EmblemH * 3;
        var on = new bool[W * H];
        for (int sy = 0; sy < H; sy++)
            for (int sx = 0; sx < W; sx++)
            {
                double ox = sx - (W - 1) / 2.0, oy = sy - 15.0;
                double dist = Math.Sqrt(ox * ox + oy * oy);
                bool dish = sy >= 18 && (ox * ox) / (11.0 * 11.0) + ((sy - 18) * (sy - 18)) / (6.0 * 6.0) <= 1.0;
                bool arc = sy <= 14 && (Math.Abs(dist - 6.5) < 0.6 || Math.Abs(dist - 10.0) < 0.6);
                bool feed = (Math.Abs(ox) < 0.6 && sy >= 13 && sy <= 17) || (Math.Abs(ox) < 2.0 && sy >= 12 && sy <= 13);
                on[sy * W + sx] = dish || arc || feed;
            }

        var bits = new int[EmblemW * EmblemH];
        for (int cy = 0; cy < EmblemH; cy++)
            for (int cx = 0; cx < EmblemW; cx++)
            {
                int b = 0;
                for (int dy = 0; dy < 3; dy++)
                    for (int dx = 0; dx < 2; dx++)
                        if (on[(cy * 3 + dy) * W + cx * 2 + dx]) b |= 1 << (dy * 2 + dx);
                bits[cy * EmblemW + cx] = b;
            }
        return bits;
    }
}
