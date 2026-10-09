using Phosphor.Core;

namespace Phosphor.Text;

/// <summary>
/// Процедурная псевдографика: линии (одинарные/двойные), скруглённые углы, блоки, штриховки.
/// Покрытие пикселя — 0 или 255, без сглаживания.
/// </summary>
public static class BoxGlyphs
{
    /// <summary>Плечи символа: вверх, вниз, влево, вправо. 0 — нет, 1 — одинарное, 2 — двойное.</summary>
    private static readonly Dictionary<char, (int U, int D, int L, int R)> Lines = new()
    {
        ['─'] = (0, 0, 1, 1), ['│'] = (1, 1, 0, 0), ['┌'] = (0, 1, 0, 1), ['┐'] = (0, 1, 1, 0),
        ['└'] = (1, 0, 0, 1), ['┘'] = (1, 0, 1, 0), ['├'] = (1, 1, 0, 1), ['┤'] = (1, 1, 1, 0),
        ['┬'] = (0, 1, 1, 1), ['┴'] = (1, 0, 1, 1), ['┼'] = (1, 1, 1, 1),

        ['═'] = (0, 0, 2, 2), ['║'] = (2, 2, 0, 0), ['╔'] = (0, 2, 0, 2), ['╗'] = (0, 2, 2, 0),
        ['╚'] = (2, 0, 0, 2), ['╝'] = (2, 0, 2, 0), ['╠'] = (2, 2, 0, 2), ['╣'] = (2, 2, 2, 0),
        ['╦'] = (0, 2, 2, 2), ['╩'] = (2, 0, 2, 2), ['╬'] = (2, 2, 2, 2),

        ['╒'] = (0, 1, 0, 2), ['╓'] = (0, 2, 0, 1), ['╕'] = (0, 1, 2, 0), ['╖'] = (0, 2, 1, 0),
        ['╘'] = (1, 0, 0, 2), ['╙'] = (2, 0, 0, 1), ['╛'] = (1, 0, 2, 0), ['╜'] = (2, 0, 1, 0),
        ['╞'] = (1, 1, 0, 2), ['╟'] = (2, 2, 0, 1), ['╡'] = (1, 1, 2, 0), ['╢'] = (2, 2, 1, 0),
        ['╤'] = (0, 1, 2, 2), ['╥'] = (0, 2, 1, 1), ['╧'] = (1, 0, 2, 2), ['╨'] = (2, 0, 1, 1),
        ['╪'] = (1, 1, 2, 2), ['╫'] = (2, 2, 1, 1),
    };

    /// <summary>Рисует глиф c в прямоугольник (x0, y0, w, h) буфера dst со страйдом dstStride.</summary>
    public static void Draw(char c, byte[] dst, int dstStride, int x0, int y0, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        var m = new bool[w * h];

        if (GlyphSet.IsSextant(c))
        {
            DrawSextant(m, w, h, c - 0xE000);
        }
        else if (Lines.TryGetValue(c, out var arms))
        {
            DrawLines(m, w, h, arms.U, arms.D, arms.L, arms.R);
        }
        else
        {
            switch (c)
            {
                case '╭': DrawArc(m, w, h, left: false, top: false); break;
                case '╮': DrawArc(m, w, h, left: true, top: false); break;
                case '╯': DrawArc(m, w, h, left: true, top: true); break;
                case '╰': DrawArc(m, w, h, left: false, top: true); break;

                case '█': Fill(m, w, h, 0, 0, w - 1, h - 1, true); break;
                case '▀': Fill(m, w, h, 0, 0, w - 1, h / 2 - 1, true); break;
                case '▄': Fill(m, w, h, 0, h / 2, w - 1, h - 1, true); break;
                case '▌': Fill(m, w, h, 0, 0, w / 2 - 1, h - 1, true); break;
                case '▐': Fill(m, w, h, w / 2, 0, w - 1, h - 1, true); break;
                case '■': DrawSquare(m, w, h, Math.Round(Math.Min(w, h) * 0.6)); break;
                case '▪': DrawSquare(m, w, h, Math.Round(Math.Min(w, h) * 0.4)); break;

                case '░':
                case '▒':
                case '▓':
                    DrawShade(m, w, h, c);
                    break;
            }
        }

        for (int y = 0; y < h; y++)
        {
            int row = (y0 + y) * dstStride + x0;
            for (int x = 0; x < w; x++)
                dst[row + x] = m[y * w + x] ? (byte)255 : (byte)0;
        }
    }

    /// <summary>Диапазон длины len, центрированный на center: [start, end] включительно.</summary>
    private static (int S, int E) Span(int center, int len)
    {
        int s = center - len / 2;
        return (s, s + len - 1);
    }

    private static void Fill(bool[] m, int w, int h, int x1, int y1, int x2, int y2, bool v)
    {
        x1 = Math.Max(0, x1); y1 = Math.Max(0, y1);
        x2 = Math.Min(w - 1, x2); y2 = Math.Min(h - 1, y2);
        for (int y = y1; y <= y2; y++)
            for (int x = x1; x <= x2; x++)
                m[y * w + x] = v;
    }

    /// <summary>
    /// Линии. Двойные плечи: сначала толстые полосы (W = 2g + t), затем вычитаются дырки (Hh = 2g − t).
    /// Одинарные плечи рисуются поверх. Все плечи доходят до края ячейки.
    /// </summary>
    private static void DrawLines(bool[] m, int w, int h, int up, int down, int left, int right)
    {
        int t = Math.Max(1, (int)Math.Round(h / 14.0));
        int g = Math.Max(t + 1, (int)Math.Round(h / 9.0));
        int cx = w / 2, cy = h / 2;
        int bandW = 2 * g + t;
        int holeW = 2 * g - t;

        var arms = new[] { ('U', up), ('D', down), ('L', left), ('R', right) };
        int doubles = arms.Count(a => a.Item2 == 2);
        bool multi = doubles > 1;
        int bandExt = multi ? bandW / 2 : t / 2;
        int holeExt = multi ? holeW / 2 : t / 2;

        foreach (var (d, v) in arms)
            if (v == 2) Arm(m, w, h, cx, cy, d, bandW, bandExt, true);
        foreach (var (d, v) in arms)
            if (v == 2) Arm(m, w, h, cx, cy, d, holeW, holeExt, false);
        foreach (var (d, v) in arms)
            if (v == 1) Arm(m, w, h, cx, cy, d, t, t / 2, true);
    }

    /// <summary>
    /// Плечо от края ячейки до центра, продлённое за центр на ext.
    /// len — ширина плеча поперёк своей оси.
    /// </summary>
    private static void Arm(bool[] m, int w, int h, int cx, int cy, char dir, int len, int ext, bool v)
    {
        switch (dir)
        {
            case 'U':
            {
                var (s, e) = Span(cx, len);
                Fill(m, w, h, s, 0, e, cy + ext, v);
                break;
            }
            case 'D':
            {
                var (s, e) = Span(cx, len);
                Fill(m, w, h, s, cy - ext, e, h - 1, v);
                break;
            }
            case 'L':
            {
                var (s, e) = Span(cy, len);
                Fill(m, w, h, 0, s, cx + ext, e, v);
                break;
            }
            default:
            {
                var (s, e) = Span(cy, len);
                Fill(m, w, h, cx - ext, s, w - 1, e, v);
                break;
            }
        }
    }

    /// <summary>
    /// Скруглённый угол: четверть окружности от середины одного края до середины другого.
    /// Центр — в углу ячейки, на который смотрит дуга. Радиусы по осям разные для прямоугольной ячейки,
    /// поэтому дуга — четверть эллипса с полуосями (cx, cy), через середины краёв.
    /// </summary>
    private static void DrawArc(bool[] m, int w, int h, bool left, bool top)
    {
        int t = Math.Max(1, (int)Math.Round(h / 14.0));
        int cx = w / 2, cy = h / 2;
        int ox = left ? 0 : w - 1;
        int oy = top ? 0 : h - 1;
        double a = Math.Max(1, left ? cx : w - 1 - cx);
        double b = Math.Max(1, top ? cy : h - 1 - cy);
        double avg = (a + b) / 2.0;

        for (int y = 0; y < h; y++)
        {
            bool inY = top ? y <= cy + t / 2 : y >= cy - t / 2;
            if (!inY) continue;
            for (int x = 0; x < w; x++)
            {
                bool inX = left ? x <= cx + t / 2 : x >= cx - t / 2;
                if (!inX) continue;
                double dx = (x - ox) / a, dy = (y - oy) / b;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (Math.Abs(d - 1.0) * avg <= t / 2.0) m[y * w + x] = true;
            }
        }
    }

    private static void DrawSquare(bool[] m, int w, int h, double side)
    {
        int s = Math.Max(1, (int)side);
        var (x1, x2) = Span(w / 2, s);
        var (y1, y2) = Span(h / 2, s);
        Fill(m, w, h, x1, y1, x2, y2, true);
    }

    /// <summary>
    /// Секстель: ячейка делится на 2 колонки × 3 ряда без щелей и наложений.
    /// Бит 0 — верх-лево, 1 — верх-право, 2 — середина-лево, 3 — середина-право, 4 — низ-лево, 5 — низ-право.
    /// Колонки: [0, w/2), [w/2, w). Ряды: [0, r1), [r1, r2), [r2, h), r1 = round(h/3), r2 = round(2h/3).
    /// </summary>
    private static void DrawSextant(bool[] m, int w, int h, int bits)
    {
        int cm = w / 2;
        int r1 = (int)Math.Round(h / 3.0, MidpointRounding.AwayFromZero);
        int r2 = (int)Math.Round(2 * h / 3.0, MidpointRounding.AwayFromZero);
        int[] rowStart = { 0, r1, r2 };
        int[] rowEnd = { r1, r2, h };      // исключительно
        int[] colStart = { 0, cm };
        int[] colEnd = { cm, w };          // исключительно
        for (int bit = 0; bit < 6; bit++)
        {
            if ((bits & (1 << bit)) == 0) continue;
            int row = bit / 2, col = bit % 2;
            Fill(m, w, h, colStart[col], rowStart[row], colEnd[col] - 1, rowEnd[row] - 1, true);
        }
    }

    private static void DrawShade(bool[] m, int w, int h, char kind)
    {
        int p = Math.Max(1, h / 16);
        for (int y = 0; y < h; y++)
        {
            int by = y / p;
            for (int x = 0; x < w; x++)
            {
                int bx = x / p;
                bool on = kind switch
                {
                    '░' => bx % 2 == 0 && by % 2 == 0,
                    '▒' => (bx + by) % 2 == 0,
                    _ => !(bx % 2 == 1 && by % 2 == 1),
                };
                m[y * w + x] = on;
            }
        }
    }
}
