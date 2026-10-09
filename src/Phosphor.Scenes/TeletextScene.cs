using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// Телетекст: страницы европейских каналов (NOS, SVT...) с переключением как на старом телевизоре.
/// Данные приходят от хоста ("teletext", "clock"); без данных рисуется процедурная заглушка.
/// </summary>
public sealed class TeletextScene : IScene, IDataScene, IPreferredRows
{
    private const int PageCols = 40;
    private const int PageRows = 25;
    private const float SearchTime = 1.2f;
    private const float WipeTime = 0.4f;
    private const float CounterStep = 0.05f;
    private const float MinHold = 12f;
    private const float HoldRange = 8f;

    private static readonly Rgba Dim = new(110, 110, 110);
    private static readonly IReadOnlyList<string> Keys = new[] { "teletext", "clock" };

    public string Id => "teletext";
    public int PreferredRows => 30;
    public IReadOnlyList<string> DataKeys => Keys;

    private Random _rng = new(0);
    private float _time;

    private TeletextPage[] _pages = Array.Empty<TeletextPage>();
    private string[] _chanName = Array.Empty<string>();
    private string[] _langName = Array.Empty<string>();
    private int[] _chanNo = Array.Empty<int>();
    private int[] _order = Array.Empty<int>();   // порядок показа (индексы в _pages), перемешан в OnData
    private int _pos, _nextPos;                  // позиции в _order: текущая и следующая
    private int _phase;                          // 0 — показ, 1 — поиск, 2 — построчная смена
    private float _phaseT, _holdLeft;

    private bool _hasClock;
    private DateTime _clock;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _rng = rng;
        _time = 0f;
        _pages = Array.Empty<TeletextPage>();
        _chanName = Array.Empty<string>();
        _langName = Array.Empty<string>();
        _chanNo = Array.Empty<int>();
        _order = Array.Empty<int>();
        _pos = 0;
        _nextPos = 0;
        _phase = 0;
        _phaseT = 0f;
        _holdLeft = NextHold();
        _hasClock = false;
        _clock = default;
    }

    public void OnData(string key, DataSnapshot snapshot)
    {
        switch (snapshot)
        {
            case ClockSnapshot clock when key == "clock":
                _hasClock = true;
                _clock = clock.Local;
                break;
            case TeletextSnapshot tt when key == "teletext":
                SetPages(tt.Pages);
                break;
        }
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;
        _time += dt;

        int n = _pages.Length;
        if (n <= 1) return;

        switch (_phase)
        {
            case 0:
                _holdLeft -= dt;
                if (_holdLeft <= 0f)
                {
                    _nextPos = (_pos + 1) % n;
                    _phase = 1;
                    _phaseT = 0f;
                }
                break;
            case 1:
                _phaseT += dt;
                if (_phaseT >= SearchTime)
                {
                    _phase = 2;
                    _phaseT = 0f;
                }
                break;
            default:
                _phaseT += dt;
                if (_phaseT >= WipeTime)
                {
                    _pos = _nextPos;
                    _phase = 0;
                    _phaseT = 0f;
                    _holdLeft = NextHold();
                }
                break;
        }
    }

    public void Draw(CharGrid grid)
    {
        grid.Clear(Rgba.Black);
        int ox = (grid.Cols - PageCols) / 2;
        int oy = (grid.Rows - PageRows) / 2;
        bool framed = grid.Cols >= 60 && grid.Rows >= 28;

        int n = _pages.Length;
        if (n == 0)
        {
            DrawStub(grid, ox, oy);
            if (framed) DrawFrame(grid, ox, oy, 100, 0, -1, 0, -1);
            return;
        }

        int cur = _order[_pos];
        TeletextPage p = _pages[cur];

        if (_phase == 2)
        {
            int nxt = _order[_nextPos];
            int k = Math.Clamp((int)(_phaseT / WipeTime * PageRows), 0, PageRows);
            DrawRows(grid, ox, oy, _pages[nxt], 0, k);
            DrawRows(grid, ox, oy, p, k, PageRows);
        }
        else
        {
            DrawRows(grid, ox, oy, p, 0, PageRows);
            if (_phase == 1)
            {
                int steps = (int)(_phaseT / CounterStep);
                DrawSearch(grid, ox, oy, _pages[_order[_nextPos]].Page, steps);
            }
        }

        if (framed) DrawFrame(grid, ox, oy, p.Page, p.SubPage, _pos + 1, n, cur);
    }

    private float NextHold() => MinHold + _rng.NextSingle() * HoldRange;

    private void SetPages(IReadOnlyList<TeletextPage> list)
    {
        int n = list.Count;
        _pages = new TeletextPage[n];
        _chanName = new string[n];
        _langName = new string[n];
        _chanNo = new int[n];
        var channels = new List<string>();
        for (int i = 0; i < n; i++)
        {
            var p = list[i];
            _pages[i] = p;
            string ch = GlyphSet.Sanitize(p.Channel);
            _chanName[i] = ch;
            _langName[i] = GlyphSet.Sanitize(p.Lang);
            int k = channels.IndexOf(ch);
            if (k < 0)
            {
                channels.Add(ch);
                k = channels.Count - 1;
            }
            _chanNo[i] = k + 1;
        }

        _order = new int[n];
        for (int i = 0; i < n; i++) _order[i] = i;
        for (int i = n - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (_order[i], _order[j]) = (_order[j], _order[i]);
        }

        _pos = 0;
        _nextPos = 0;
        _phase = 0;
        _phaseT = 0f;
        _holdLeft = NextHold();
    }

    /// <summary>Строки [r0, r1) страницы. Часы в строке 0 — только если колонки 32..39 там пустые (не трогаем чужой текст).</summary>
    private void DrawRows(CharGrid g, int ox, int oy, TeletextPage p, int r0, int r1)
    {
        bool clockSlotFree = _hasClock && r0 == 0 && ClockSlotBlank(p);
        for (int r = r0; r < r1; r++)
        {
            for (int x = 0; x < PageCols; x++)
            {
                char ch = ' ';
                Rgba fg = Rgba.White, bg = Rgba.Black;
                if (x < p.Cols && r < p.Rows)
                {
                    int i = r * p.Cols + x;
                    if (i < p.Chars.Length && i < p.Fg.Length && i < p.Bg.Length && i < p.Mosaic.Length)
                    {
                        ch = p.Mosaic[i] != 0 ? GlyphSet.Sextant(p.Mosaic[i]) : p.Chars[i];
                        if (!GlyphSet.Contains(ch)) ch = '?';
                        fg = TeletextColors.Palette[p.Fg[i] & 7];
                        bg = TeletextColors.Palette[p.Bg[i] & 7];
                    }
                }
                g.Put(ox + x, oy + r, ch, fg, bg);
            }
            if (r == 0 && clockSlotFree)
                WriteTime(g, ox + 32, oy, _clock, TeletextColors.Palette[7], TeletextColors.Palette[0]);
        }
    }

    /// <summary>Колонки 32..39 строки 0 страницы — пробелы (или данных нет).</summary>
    private static bool ClockSlotBlank(TeletextPage p)
    {
        for (int x = 32; x < PageCols; x++)
        {
            int i = x; // строка 0
            if (x >= p.Cols || p.Rows < 1 || i >= p.Chars.Length || i >= p.Mosaic.Length) continue;
            if (p.Mosaic[i] != 0 || p.Chars[i] != ' ') return false;
        }
        return true;
    }

    /// <summary>Строка 0 во время поиска: искомый номер и щёлкающий счётчик пролетающих страниц.</summary>
    private static void DrawSearch(CharGrid g, int ox, int oy, int target, int steps)
    {
        Rgba yel = TeletextColors.Palette[3], blk = TeletextColors.Palette[0], wht = TeletextColors.Palette[7];
        g.Put(ox, oy, 'P', yel, blk);
        Num(g, ox + 1, oy, target, 3, yel, blk);
        Num(g, ox + 5, oy, 100 + steps % 900, 3, yel, blk);
        g.Text(ox + 9, oy, "SEARCH", wht, blk);
    }

    private void DrawStub(CharGrid g, int ox, int oy)
    {
        Rgba white = TeletextColors.Palette[7], yel = TeletextColors.Palette[3], blk = TeletextColors.Palette[0];
        Rgba blue = TeletextColors.Palette[4];

        // Синяя шапка
        for (int x = 0; x < PageCols; x++) g.Put(ox + x, oy, ' ', white, blue);
        g.Text(ox + 1, oy, "PHOSPHOR TEXT  P100", white, blue);

        // Таблица цветных полос: 8 цветов по 5 колонок
        for (int r = 2; r <= 9; r++)
            for (int x = 0; x < PageCols; x++)
                g.Put(ox + x, oy + r, ' ', white, TeletextColors.Palette[x / 5]);

        // Мозаичный узор, медленно текущий
        int ph = (int)(_time * 4f);
        for (int r = 11; r <= 19; r++)
            for (int x = 0; x < PageCols; x++)
            {
                int bits = (x * 5 + r * 9 + ph * 3) & 63;
                g.Put(ox + x, oy + r, GlyphSet.Sextant(bits), TeletextColors.Palette[1 + (x / 5) % 6], blk);
            }

        // Внизу мигающая надпись с анимированными точками (мигание 1 Гц)
        if ((int)(_time * 2f) % 2 == 0)
        {
            g.Text(ox + 10, oy + 22, "SIGNAL SEARCH", yel, blk);
            int dots = (int)(_time * 3f) % 4;
            for (int d = 0; d < dots; d++) g.Put(ox + 23 + d, oy + 22, '.', yel, blk);
        }
    }

    /// <summary>Рамка «пульта»: уголки, подпись канала сверху, палитра слева, номер/счётчик справа.</summary>
    private void DrawFrame(CharGrid g, int ox, int oy, int pageNo, int sub, int idx1, int total, int chanIdx)
    {
        Rgba yel = TeletextColors.Palette[3], wht = TeletextColors.Palette[7];

        // Уголки с отступом 1 от страницы
        g.Put(ox - 1, oy - 1, '┌', Dim);
        g.Put(ox + PageCols, oy - 1, '┐', Dim);
        g.Put(ox - 1, oy + PageRows, '└', Dim);
        g.Put(ox + PageCols, oy + PageRows, '┘', Dim);

        // Подпись канала над страницей
        if (chanIdx >= 0)
        {
            int x = ox + 12, y = oy - 1;
            g.Text(x, y, "CH ", Dim);
            x += 3;
            Num(g, x, y, _chanNo[chanIdx], 2, Dim);
            x += 2;
            g.Text(x, y, " · ", Dim);
            x += 3;
            g.Text(x, y, _chanName[chanIdx], Dim);
            x += _chanName[chanIdx].Length;
            g.Text(x, y, " · ", Dim);
            x += 3;
            g.Text(x, y, _langName[chanIdx], Dim);
        }

        // Палитра слева: 8 образцов с подписями индексов
        for (int i = 0; i < 8; i++)
        {
            int y = oy + 1 + i;
            Num(g, ox - 5, y, i, 1, Dim);
            g.Put(ox - 3, y, '█', TeletextColors.Palette[i]);
        }

        // Справа столбиком: номер страницы (мигает 1 Гц), подстраница, ▲▼, счётчик
        int rx = ox + PageCols + 3;
        bool blinkOn = (int)(_time * 2f) % 2 == 0;
        Rgba numColor = blinkOn ? yel : Dim;
        g.Put(rx, oy, 'P', numColor);
        Num(g, rx + 1, oy, pageNo, 3, numColor);
        g.Put(rx, oy + 1, '/', wht);
        Num(g, rx + 1, oy + 1, sub, 2, wht);
        g.Put(rx, oy + 3, '▲', wht);
        g.Put(rx + 1, oy + 3, '▼', wht);
        if (idx1 >= 0)
        {
            Num(g, rx, oy + 5, idx1, 2, wht);
            g.Put(rx + 2, oy + 5, '/', wht);
            Num(g, rx + 3, oy + 5, total, 2, wht);
        }
        else
        {
            g.Text(rx, oy + 5, "--/--", Dim);
        }

        // Часы всегда вне страницы: под счётчиком, тусклым
        if (_hasClock) WriteTime(g, rx, oy + 7, _clock, Dim, Rgba.Black);
    }

    private static void WriteTime(CharGrid g, int x, int y, DateTime t, Rgba fg, Rgba bg)
    {
        Num(g, x, y, t.Hour, 2, fg, bg);
        g.Put(x + 2, y, ':', fg, bg);
        Num(g, x + 3, y, t.Minute, 2, fg, bg);
        g.Put(x + 5, y, ':', fg, bg);
        Num(g, x + 6, y, t.Second, 2, fg, bg);
    }

    /// <summary>Число с ведущими нулями без аллокаций.</summary>
    private static void Num(CharGrid g, int x, int y, int v, int w, Rgba fg, Rgba? bg = null)
    {
        if (v < 0) v = 0;
        for (int i = w - 1; i >= 0; i--)
        {
            g.Put(x + i, y, (char)('0' + v % 10), fg, bg);
            v /= 10;
        }
    }
}
