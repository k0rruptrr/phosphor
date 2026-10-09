using System.Globalization;
using System.Text;
using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// «Телетайп»: заголовки информационных агентств печатаются посимвольно, как на ленте в аппаратной.
/// Данные (новости, часы) приходят готовыми от хоста; сцена сама в сеть не ходит.
/// </summary>
public sealed class TeletypeScene : IScene, IDataScene, IAudioReactive
{
    private const string HeaderTitle = "ТЕЛЕТАЙП · ЛЕНТА ИНФОРМАЦИОННЫХ АГЕНТСТВ";
    private const string HeaderLine = "ЛИНИЯ 3";
    private const string Separator = "- - - - -";
    private const string Dots = ". . .";

    /// <summary>Служебные строки, когда новостей нет. Цикл по кругу.</summary>
    private static readonly string[] ServiceTexts =
    {
        "СВЯЗЬ С АГЕНТСТВОМ... НЕТ НЕСУЩЕЙ",
        "ПОВТОР ВЫЗОВА 3/5... ТАЙМ-АУТ",
        "ЛИНИЯ 3: ОЖИДАНИЕ ПЕРЕДАЧИ ЛЕНТЫ",
        "ХОСТ НЕ ОТВЕЧАЕТ. ПЕРЕКЛЮЧЕНИЕ НА РЕЗЕРВ",
        "КОНТРОЛЬНАЯ СУММА НЕ СОВПАЛА. ПОВТОР",
        "ПРИНЯТО 0 БАЙТ. ОЖИДАЙТЕ",
        "ОТКАЗ АГЕНТСТВА 503. ПОВТОР ЗАПРОСА",
        "СИГНАЛ ПОТЕРЯН. ПОИСК НЕСУЩЕЙ...",
    };

    /// <summary>Безопасная зона ЭЛТ: отступ слева/справа в ячейках и сверху/снизу в строках.</summary>
    private const int OffX = 2, OffY = 1;

    /// <summary>Колонка источника: 11 символов (длиннее — обрезается с '.' на конце).</summary>
    private const int SourceWidth = 11;
    /// <summary>Ширина префикса: источник(11) + "  " + "04:31" + "  " = 20.</summary>
    private const int PrefixWidth = 20;
    private const int MaxNews = 60;
    private const float DingLen = 0.15f;
    private const float IdleLen = 30f;
    private const float GapLen = 1.5f;
    private const float FlashLen = 0.1f;
    private const float StallLen = 0.2f;
    private const float DotsLen = 2f;

    private enum Phase { Pick, Type, Ding, Gap, Idle }

    /// <summary>Одна физическая строка ленты: готовые ячейки и диапазон, который печатается.</summary>
    private sealed class PhysLine
    {
        public readonly Cell[] Cells;
        public readonly int Start, End;

        public PhysLine(Cell[] cells, int start, int end)
        {
            Cells = cells;
            Start = start;
            End = end;
        }
    }

    private sealed class NewsEntry
    {
        public string Source = "";
        public string Time = "";
        public string Title = "";
        public PhysLine[] Lines = Array.Empty<PhysLine>();
        public bool Printed;
    }

    public string Id => "teletype";

    public IReadOnlyList<string> DataKeys { get; } = new[] { "news", "clock" };

    private int _cols, _rows, _printRow;
    private Random _rng = new(0);
    private float _speed = 1f, _audioMul = 1f, _beatFlash, _clock;
    private bool _mono;
    private Rgba _main, _dim;

    private string _headRight = "";
    private string _cacheLabel = "";
    private string _clockText = "--:--:--";

    // Лента: кольцевой буфер уже напечатанных строк (самая свежая — индекс _histHead - 1).
    private Cell[] _hist = Array.Empty<Cell>();
    private int _histCap, _histHead, _histCount;
    private Cell[] _cur = Array.Empty<Cell>();
    private CharGrid _inner = null!;

    private PhysLine _blank = null!, _sep = null!;
    private PhysLine[][] _svc = Array.Empty<PhysLine[]>();
    private int _svcIdx;
    private bool _lastWasSvc;
    private readonly List<NewsEntry> _news = new();
    private int _recCount;

    // Текущая запись: строки и индекс следующей; _sepPending — разделитель перед записью.
    private PhysLine[]? _lines;
    private int _li;
    private bool _sepPending;

    // Печать текущей строки.
    private PhysLine? _pl;
    private int _ci;
    private float _rate = 30f, _charWait;

    private Phase _phase = Phase.Pick;
    private float _phaseT, _gapLen, _dotsAt;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        // Внутренняя сетка — без безопасной зоны; Draw переносит её в grid со смещением.
        _cols = Math.Max(1, cols - 2 * OffX);
        _rows = Math.Max(1, rows - 2 * OffY);
        _rng = rng;
        _speed = MathF.Max(0.05f, options.Speed);
        _printRow = Math.Max(2, _rows - 3); // экранная строка rows-4

        var name = (options.Palette ?? "default").ToLowerInvariant();
        _mono = name is "green" or "amber" or "mono";
        if (_mono)
        {
            _main = Palettes.Base(name);
            _dim = _main.Scale(0.45f);
        }
        else
        {
            _main = Rgba.Hex(0xF2EEDD);
            _dim = _main.Scale(0.45f);
        }

        _cacheLabel = "";
        _clockText = "--:--:--";
        _headRight = "";

        _histCap = _rows;
        _hist = new Cell[_histCap * _cols];
        _histHead = 0;
        _histCount = 0;
        _cur = BlankRow();
        _inner = new CharGrid(_cols, _rows);

        _blank = new PhysLine(BlankRow(), 0, 0);
        var sepCells = BlankRow();
        WriteText(sepCells, 0, Separator, _dim);
        _sep = new PhysLine(sepCells, 0, Math.Min(_cols, Separator.Length));

        _svc = new PhysLine[ServiceTexts.Length][];
        for (int i = 0; i < ServiceTexts.Length; i++)
            _svc[i] = Build(null, null, ServiceTexts[i], _main, _main, _main);
        _svcIdx = rng.Next(ServiceTexts.Length);

        _news.Clear();
        _lastWasSvc = false;
        _recCount = 0;
        _lines = null;
        _li = 0;
        _sepPending = false;
        _pl = null;
        _ci = 0;
        _charWait = 0f;
        _rate = 30f;
        _phase = Phase.Pick;
        _phaseT = 0f;
        _gapLen = 0f;
        _dotsAt = 0f;
        _audioMul = 1f;
        _beatFlash = 0f;
        _clock = 0f;

        RebuildHeader();
    }

    public void OnData(string key, DataSnapshot snapshot)
    {
        switch (snapshot)
        {
            case ClockSnapshot cs when key == "clock":
                _clockText = cs.Local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                break;
            case NewsSnapshot ns when key == "news":
                ApplyNews(ns);
                break;
            default:
                return;
        }
        RebuildHeader();
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;

        _clock += dt;
        if (_beatFlash > 0f) _beatFlash = MathF.Max(0f, _beatFlash - dt);
        _phaseT += dt;

        switch (_phase)
        {
            case Phase.Pick:
                Pick();
                break;
            case Phase.Type:
                TypeChars(dt);
                break;
            case Phase.Ding:
                if (_phaseT >= DingLen) Feed();
                break;
            case Phase.Gap:
                if (_phaseT >= _gapLen) Enter(Phase.Pick);
                break;
            case Phase.Idle:
                if (_phaseT >= IdleLen)
                {
                    foreach (var e in _news) e.Printed = false;
                    Enter(Phase.Pick);
                }
                break;
        }
    }

    public void Draw(CharGrid grid)
    {
        DrawInner(_inner);
        grid.Clear(Rgba.Black);
        for (int y = 0; y < _rows; y++)
        {
            int gy = y + OffY;
            if ((uint)gy >= (uint)grid.Rows) continue;
            for (int x = 0; x < _cols; x++)
            {
                int gx = x + OffX;
                if ((uint)gx >= (uint)grid.Cols) continue;
                grid.Cells[gy * grid.Cols + gx] = _inner.Cells[y * _cols + x];
            }
        }
    }

    private void DrawInner(CharGrid grid)
    {
        grid.Clear(Rgba.Black);
        int cols = _cols, pr = _printRow;

        // Шапка: подписи тусклые.
        grid.Text(0, 0, HeaderTitle, _dim, Rgba.Black);
        grid.Text(cols - _headRight.Length, 0, _headRight, _dim, Rgba.Black);
        for (int x = 0; x < cols; x++) grid.Put(x, 1, '─', _dim, Rgba.Black);

        // Напечатанная лента: чем выше строка, тем тусклее (от 1.0 у печатаемой до 0.3 у верхней).
        for (int d = 1; d <= _histCount; d++)
        {
            int y = pr - d;
            if (y < 2) break;
            float t = (pr - y) / (float)(pr - 2);
            float b = 1f - 0.7f * t;
            int idx = ((_histHead - d) % _histCap + _histCap) % _histCap;
            int bas = idx * cols;
            for (int x = 0; x < cols; x++)
            {
                var c = _hist[bas + x];
                if (c.Ch != ' ') grid.Put(x, y, c.Ch, c.Fg.Scale(b), Rgba.Black);
            }
        }

        // Печатаемая строка. Удар молоточка — вспышка яркости строки.
        float flash = _beatFlash > 0f ? 0.35f : 0f;
        for (int x = 0; x < cols; x++)
        {
            var c = _cur[x];
            if (c.Ch == ' ') continue;
            var fg = flash > 0f ? Rgba.Lerp(c.Fg, Rgba.White, flash) : c.Fg;
            grid.Put(x, pr, c.Ch, fg, Rgba.Black);
        }

        // Каретка.
        bool dotsShown = _phase == Phase.Idle && _phaseT >= _dotsAt && _phaseT < _dotsAt + DotsLen;
        if (dotsShown)
        {
            grid.Text(0, pr, Dots, _dim, Rgba.Black);
        }
        else if (_phase == Phase.Ding)
        {
            // Дзынь: инверсия, моргает.
            if ((int)(_phaseT / 0.05f) % 2 == 0) grid.Put(_ci, pr, ' ', _main, _main);
        }
        else if (_clock % 1f < 0.5f)
        {
            grid.Put(_ci, pr, '█', _main, Rgba.Black);
        }
    }

    public void OnAudio(in AudioFrame audio)
    {
        if (!audio.Active)
        {
            _audioMul = 1f;
            _beatFlash = 0f;
            return;
        }
        _audioMul = 1f + MathF.Max(0f, audio.Level);
        if (audio.Beat) _beatFlash = FlashLen;
    }

    // ----- состояние ленты -----

    private void Enter(Phase p)
    {
        _phase = p;
        _phaseT = 0f;
    }

    private void Pick()
    {
        if (_lines == null)
        {
            if (_lastWasSvc)
            {
                _lastWasSvc = false;
                _gapLen = GapLen;
                Enter(Phase.Gap);
                return;
            }
            if (!BeginRecord())
            {
                _dotsAt = 4f + _rng.NextSingle() * 12f;
                Enter(Phase.Idle);
                return;
            }
        }

        var pl = NextPhys();
        if (pl == null) return; // запись кончилась; следующая запись начнётся на следующем Pick

        _pl = pl;
        _ci = pl.Start;
        _rate = 25f + _rng.NextSingle() * 20f;
        _charWait = 0.05f;
        Enter(Phase.Type);
    }

    private bool BeginRecord()
    {
        if (_news.Count == 0)
        {
            _lines = _svc[_svcIdx];
            _svcIdx = (_svcIdx + 1) % _svc.Length;
            _li = 0;
            _sepPending = false;
            _lastWasSvc = true;
            return true;
        }

        foreach (var e in _news)
        {
            if (e.Printed) continue;
            e.Printed = true;
            _lines = e.Lines;
            _li = 0;
            _lastWasSvc = false;
            _sepPending = _recCount > 0 && _recCount % 5 == 0;
            _recCount++;
            return true;
        }
        return false;
    }

    private PhysLine? NextPhys()
    {
        if (_sepPending)
        {
            _sepPending = false;
            return _sep;
        }
        if (_lines != null && _li < _lines.Length) return _lines[_li++];
        _lines = null;
        return null;
    }

    private void TypeChars(float dt)
    {
        float mul = _speed * _audioMul;
        _charWait -= dt;
        while (_charWait <= 0f)
        {
            if (_pl == null || _ci >= _pl.End)
            {
                Enter(Phase.Ding);
                return;
            }
            char ch = _pl.Cells[_ci].Ch;
            if ((uint)_ci < (uint)_cols) _cur[_ci] = _pl.Cells[_ci];
            _ci++;
            _charWait += CharDelay(ch, mul);
        }
    }

    private float CharDelay(char ch, float mul)
    {
        float d = 1f / (_rate * mul) * (0.7f + 0.6f * _rng.NextSingle());
        if (ch == ' ') d *= 1.8f;
        else if (ch is '.' or ',' or ':' or ';' or '!' or '?' or '-') d += 0.18f / mul;
        if (_rng.NextSingle() < 0.02f) d += StallLen; // редкая запинка
        return d;
    }

    private void Feed()
    {
        // Бумага подаётся на строку вверх: напечатанная строка уходит в историю.
        Array.Copy(_cur, 0, _hist, _histHead * _cols, _cols);
        _histHead = (_histHead + 1) % _histCap;
        if (_histCount < _histCap) _histCount++;

        Array.Fill(_cur, new Cell(' ', _main, Rgba.Black));
        _pl = null;
        _ci = 0;
        Enter(Phase.Pick);
    }

    // ----- данные -----

    private void ApplyNews(NewsSnapshot ns)
    {
        var printed = new HashSet<string>();
        foreach (var e in _news)
            if (e.Printed) printed.Add(Key(e));

        _news.Clear();
        if (ns.Items != null)
        {
            foreach (var it in ns.Items)
            {
                if (_news.Count >= MaxNews) break;
                var title = GlyphSet.Sanitize(it.Title ?? "").Trim();
                if (title.Length == 0) continue;

                var src = GlyphSet.Sanitize(it.Source ?? "").Trim().ToUpperInvariant();
                if (src.Length > SourceWidth) src = src.Substring(0, SourceWidth - 1) + ".";
                var time = it.Published is { } p
                    ? p.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                    : "--:--";

                var e = new NewsEntry { Source = src, Time = time, Title = title };
                e.Printed = printed.Contains(Key(e));
                e.Lines = Build(e.Source, e.Time, e.Title, SourceColor(e.Source), _dim, _main);
                _news.Add(e);
            }
        }

        _cacheLabel = ns.FromCache
            ? "КЕШ " + ns.FetchedAt.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture) + "  "
            : "";
    }

    private static string Key(NewsEntry e) => e.Source + "|" + e.Title;

    private void RebuildHeader()
    {
        _headRight = _cacheLabel + _clockText + "  " + HeaderLine;
    }

    private Rgba SourceColor(string s)
    {
        if (_mono)
        {
            int k = 0;
            foreach (char c in s) k += c;
            return _main.Scale(k % 4 switch { 0 => 1f, 1 => 0.85f, 2 => 0.7f, _ => 0.55f });
        }
        return s switch
        {
            "ТАСС" or "TASS" => Rgba.Hex(0xFFB000),
            "РИА" or "RIA" => Rgba.Hex(0xFF8A00),
            "ИНТЕРФАКС" or "INTERFAX" => Rgba.Hex(0xE0604A),
            "BBC" => Rgba.Hex(0xFFFFFF),
            "REUTERS" => Rgba.Hex(0xFF9A1A),
            _ when s.StartsWith("R/", StringComparison.Ordinal) => Rgba.Hex(0x3FD8D0),
            _ => Rgba.Hex(0xD8D2BC),
        };
    }

    /// <summary>
    /// Разбивает запись на физические строки. С префиксом: «ИСТОЧНИК  ЧЧ:ММ  заголовок»,
    /// продолжение — с отступом под заголовком. Без префикса — служебная строка.
    /// </summary>
    private PhysLine[] Build(string? src, string? time, string text, Rgba srcColor, Rgba timeColor, Rgba textColor)
    {
        bool prefix = src != null;
        int indent = prefix ? PrefixWidth : 0;
        var chunks = Wrap(text, Math.Max(1, _cols - indent));
        var lines = new PhysLine[chunks.Count + 1];
        for (int i = 0; i < chunks.Count; i++)
        {
            var row = BlankRow();
            if (prefix && i == 0)
            {
                WriteText(row, 0, src!.PadRight(SourceWidth), srcColor);
                WriteText(row, SourceWidth + 2, time ?? "--:--", timeColor);
            }
            WriteText(row, indent, chunks[i], textColor);
            int start = prefix && i > 0 ? indent : 0;
            int end = Math.Min(_cols, indent + chunks[i].Length);
            lines[i] = new PhysLine(row, Math.Min(start, _cols), end);
        }
        lines[chunks.Count] = _blank;
        return lines;
    }

    private static List<string> Wrap(string text, int width)
    {
        var result = new List<string>();
        var line = new StringBuilder();
        foreach (var raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw;
            if (line.Length > 0 && line.Length + 1 + word.Length <= width)
            {
                line.Append(' ').Append(word);
                continue;
            }
            if (line.Length > 0)
            {
                result.Add(line.ToString());
                line.Clear();
            }
            while (word.Length > width)
            {
                result.Add(word.Substring(0, width));
                word = word.Substring(width);
            }
            line.Append(word);
        }
        if (line.Length > 0) result.Add(line.ToString());
        return result;
    }

    private Cell[] BlankRow()
    {
        var row = new Cell[_cols];
        Array.Fill(row, new Cell(' ', _main, Rgba.Black));
        return row;
    }

    private static void WriteText(Cell[] row, int x, string s, Rgba fg)
    {
        for (int i = 0; i < s.Length; i++)
        {
            int p = x + i;
            if ((uint)p < (uint)row.Length) row[p] = new Cell(s[i], fg, Rgba.Black);
        }
    }
}
