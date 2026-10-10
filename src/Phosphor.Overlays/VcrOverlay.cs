using Phosphor.Core;

namespace Phosphor.Overlays;

/// <summary>
/// OSD видика 90-х поверх сцены: PLAY, часы, счётчик ленты S[====  ]E, AUTO TRACKING.
/// Крупный режим (cols >= 60 и rows >= 12): пиксельный шрифт 5×7 из секстантов (PixelFont), шкала — блоками █.
/// Мелкий режим (меньше): обычный текст в клетках. Под надписью — тёмная подложка (Bg) с полем в 1 клетку.
/// Рисует только символами из GlyphSet, поэтому проходит через фильтры вместе со сценой.
/// Дирижёр включает его только для пресетов со стадией vhs (см. OverlayConfig.VcrFor).
/// </summary>
public sealed class VcrOverlay : IOverlay
{
    public string Id => "vcr";

    // Константы OSD
    private const int Margin = 2;                    // отступ от краёв, в клетках
    private const double TapeSec = 180 * 60;         // кассета E-180
    private const int BarWide = 20;                  // ширина шкалы (cols >= 40, в крупном режиме — меньше при узкой сетке)
    private const int BigMinCols = 60;               // крупный шрифт — от этой ширины
    private const int BigMinRows = 12;
    private const int BigBarOverhead = 51;           // клетки под надписи крупной строки счётчика (кроме шкалы)
    private const double PlayBlinkFor = 3.0;         // первые секунды PLAY мигает
    private const double BlinkHalf = 0.5;            // полупериод мигания PLAY
    private const double RewSec = 4.0;               // надпись REW после конца ленты
    private const double RewQuick = 1.0;             // шкала уезжает к пустой за это время
    private const float AutoTrackFatigue = 0.6f;     // совпадает с началом эпизодов автотрекинга в kassette
    private const int BufLen = 64;

    private static readonly Rgba Fg = Rgba.White;
    private static readonly Rgba PlateColor = new(8, 8, 8);      // (0.03, 0.03, 0.03): тёмная подложка под надписью
    private static readonly bool HasPlayGlyph = GlyphSet.Contains('►');
    private static readonly bool HasRewGlyph = GlyphSet.Contains('◄');
    private const string PlayBig = "PLAY ►";
    private const string RewBig = "REW ◄◄";
    private static readonly string PlaySmall = HasPlayGlyph ? "PLAY ►" : "PLAY >";
    private static readonly string RewSmall = HasRewGlyph ? "REW ◄◄" : "REW <<";
    private const string AutoText = "AUTO TRACKING";

    private double _t;          // секунды с Init (сумма dt)
    private int _lap;           // номер прохода ленты (t / 180 мин)
    private double _rewLeft;    // сколько секунд ещё показывать REW (после конца ленты)

    private readonly char[] _buf = new char[BufLen];
    private int _bn;

    public void Init(int cols, int rows, Random rng)
    {
        _t = 0;
        _lap = 0;
        _rewLeft = 0;
    }

    public void Update(float dt)
    {
        _t += dt;
        if (_rewLeft > 0) _rewLeft -= dt;
        int lap = (int)(_t / TapeSec);
        if (lap != _lap)
        {
            _lap = lap;
            _rewLeft = RewSec;
        }
    }

    public void Apply(CharGrid grid, in OverlayContext ctx)
    {
        int cols = grid.Cols, rows = grid.Rows;
        bool big = cols >= BigMinCols && rows >= BigMinRows;
        int adv = big ? PixelFont.Advance : 1;          // шаг символа, клеток
        int h = big ? PixelFont.CellsH : 1;             // высота строки текста, клеток
        bool rew = _rewLeft > 0;
        double pos = _t % TapeSec;                      // позиция ленты в проходе, с
        int barY = rows - 1 - Margin - (h - 1);         // нижняя строка OSD — на 2 клетки выше нижнего края

        // Левый верх: REW после конца ленты, иначе PLAY (мигает первые 3 с)
        if (rew)
        {
            string s = big ? RewBig : RewSmall;
            Emit(grid, Margin, Margin, s, big);
            Plate(grid, Margin, Margin, s.Length * adv, h);
        }
        else if (_t >= PlayBlinkFor || Math.Floor(_t / BlinkHalf) % 2 == 0)
        {
            string s = big ? PlayBig : PlaySmall;
            Emit(grid, Margin, Margin, s, big);
            Plate(grid, Margin, Margin, s.Length * adv, h);
        }

        // Правый верх: SP и часы HH:MM (двоеточие мигает раз в секунду)
        _bn = 0;
        Append("SP ");
        if (ctx.Clock is DateTime clk)
        {
            AppendNum(clk.Hour, 2);
            _buf[_bn++] = (_t % 1.0) < 0.5 ? ':' : ' ';
            AppendNum(clk.Minute, 2);
        }
        else Append("--:--");
        int cw = _bn * adv;
        int cx = cols - Margin - cw;
        Emit(grid, cx, Margin, _buf.AsSpan(0, _bn), big);
        Plate(grid, cx, Margin, cw, h);

        // AUTO TRACKING по центру верхней строки, между PLAY и часами (мигает 1 Гц при усталости сцены).
        // Если между ними не помещается — не рисуем, чтобы не слипаться с соседними надписями.
        if (ctx.Organism.Fatigue >= AutoTrackFatigue && (_t % 1.0) < 0.5)
        {
            int aw = AutoText.Length * adv;
            int lo = Margin + PlayBig.Length * adv + 2;   // левый край свободной полосы
            int hi = cx - 2;                              // правый край свободной полосы
            if (hi - lo >= aw)
            {
                int ax = lo + (hi - lo - aw) / 2;
                Emit(grid, ax, Margin, AutoText, big);
                Plate(grid, ax, Margin, aw, h);
            }
        }

        // Счётчик ленты: S[====    ]E 0:12:34
        int inner = big
            ? Math.Min(BarWide, cols - BigBarOverhead)
            : (cols >= 40 ? BarWide : Math.Max(0, cols / 2 - 6));
        // Во время REW шкала быстро уезжает влево к пустой; иначе — доля ленты (180 мин)
        double frac = rew
            ? Math.Max(0, 1 - (RewSec - _rewLeft) / RewQuick)
            : pos / TapeSec;
        int filled = (int)Math.Round(frac * inner);

        int x = Margin;
        x += Emit(grid, x, barY, "S[", big);
        int barX = x;
        for (int i = 0; i < inner; i++)
        {
            if (big)
            {
                // Шкала — блоки █ высотой 2 строки; остальные клетки строки — пустые
                for (int dy = 0; dy < h; dy++) grid.Put(barX + i, barY + dy, ' ', Fg);
                if (i < filled)
                {
                    grid.Put(barX + i, barY, '█', Fg);
                    grid.Put(barX + i, barY + 1, '█', Fg);
                }
            }
            else grid.Put(barX + i, barY, i < filled ? '=' : ' ', Fg);
        }
        x += inner;

        _bn = 0;
        Append("]E ");
        int secs = (int)pos;
        AppendNum(secs / 3600, 1);
        _buf[_bn++] = ':';
        AppendNum(secs / 60 % 60, 2);
        _buf[_bn++] = ':';
        AppendNum(secs % 60, 2);
        x += Emit(grid, x, barY, _buf.AsSpan(0, _bn), big);
        Plate(grid, Margin, barY, x - Margin, h);
    }

    private void Append(string s)
    {
        for (int i = 0; i < s.Length && _bn < BufLen; i++) _buf[_bn++] = s[i];
    }

    private void AppendNum(int v, int digits)
    {
        for (int d = digits - 1; d >= 0; d--)
        {
            if (_bn >= BufLen) return;
            int p = 1;
            for (int k = 0; k < d; k++) p *= 10;
            _buf[_bn++] = (char)('0' + v / p % 10);
        }
    }

    /// <summary>
    /// Выводит строку с (x, y). Крупно — пиксельным шрифтом (3×3 клетки на символ, шаг 4, интервал пустой),
    /// мелко — по одной клетке на символ. Возвращает сдвиг курсора в клетках. Что за границей — обрезается.
    /// </summary>
    private static int Emit(CharGrid grid, int x, int y, ReadOnlySpan<char> s, bool big)
    {
        int start = x;
        foreach (char c in s)
        {
            if (!big)
            {
                grid.Put(x, y, c, Fg);
                x++;
                continue;
            }
            var bits = PixelFont.Bits(c);
            for (int cy = 0; cy < PixelFont.CellsH; cy++)
                for (int cx = 0; cx < PixelFont.CellsW; cx++)
                {
                    int b = bits[cy * PixelFont.CellsW + cx];
                    grid.Put(x + cx, y + cy, b == 0 ? ' ' : GlyphSet.Sextant(b), Fg);
                }
            // межбуквенный интервал — пустой столбец клеток, чтобы сцена не просвечивала между буквами
            for (int cy = 0; cy < PixelFont.CellsH; cy++) grid.Put(x + PixelFont.CellsW, y + cy, ' ', Fg);
            x += PixelFont.Advance;
        }
        return x - start;
    }

    /// <summary>Тёмная подложка под надписью: прямоугольник w×h плюс поле в 1 клетку. Меняет только Bg.</summary>
    private static void Plate(CharGrid grid, int x, int y, int w, int h)
    {
        for (int py = y - 1; py <= y + h; py++)
            for (int px = x - 1; px <= x + w; px++)
                if (grid.InBounds(px, py)) grid[px, py].Bg = PlateColor;
    }
}
