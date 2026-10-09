using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>Как сетка символов изображает пиксели холста.</summary>
public enum CanvasMode
{
    /// <summary>Ячейка = 1×2 пикселя ('▀': верх = Fg, низ = Bg). Цвет точный, разрешение cols × rows*2.</summary>
    HalfBlock,
    /// <summary>Ячейка = 2×3 пикселя (секстель). Разрешение cols*2 × rows*3, но на ячейку только 2 цвета:
    /// пиксели делятся на «светлые» и «тёмные» группы, каждая красится своим средним.</summary>
    Sextant,
}

/// <summary>
/// Низкоразрешающий RGB-холст для «пиксельных» сцен (демосцена, симуляции). Сцена рисует в пиксели,
/// потом Present(grid) переводит их в символы. Пиксели квадратные на глаз при ячейке ~10×18 (полублок 10×9,
/// секстель 5×6). Present не аллоцирует.
/// </summary>
public sealed class RetroCanvas
{
    public CanvasMode Mode { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Row-major, (0,0) — левый верх. Можно писать напрямую: Pixels[y * Width + x].</summary>
    public Rgba[] Pixels { get; }

    private readonly int _cols, _rows;

    public RetroCanvas(int cols, int rows, CanvasMode mode)
    {
        _cols = Math.Max(1, cols);
        _rows = Math.Max(1, rows);
        Mode = mode;
        Width = mode == CanvasMode.HalfBlock ? _cols : _cols * 2;
        Height = mode == CanvasMode.HalfBlock ? _rows * 2 : _rows * 3;
        Pixels = new Rgba[Width * Height];
    }

    public void Clear(Rgba c) => Array.Fill(Pixels, c);

    public void Set(int x, int y, Rgba c)
    {
        if ((uint)x < (uint)Width && (uint)y < (uint)Height) Pixels[y * Width + x] = c;
    }

    public Rgba Get(int x, int y) =>
        (uint)x < (uint)Width && (uint)y < (uint)Height ? Pixels[y * Width + x] : Rgba.Black;

    /// <summary>Залитый прямоугольник [x0,x1) × [y0,y1), обрезается по холсту.</summary>
    public void FillRect(int x0, int y0, int x1, int y1, Rgba c)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        x1 = Math.Min(Width, x1); y1 = Math.Min(Height, y1);
        if (x0 >= x1) return; // целиком вне холста по x (иначе стартовый индекс уедет за последнюю строку)
        for (int y = y0; y < y1; y++)
            Array.Fill(Pixels, c, y * Width + x0, Math.Max(0, x1 - x0));
    }

    /// <summary>Перенести холст в сетку целиком (все ячейки перезаписываются).</summary>
    public void Present(CharGrid grid)
    {
        int cols = Math.Min(_cols, grid.Cols), rows = Math.Min(_rows, grid.Rows);
        var cells = grid.Cells;
        if (Mode == CanvasMode.HalfBlock)
        {
            for (int y = 0; y < rows; y++)
            {
                int top = 2 * y * Width, bot = top + Width;
                for (int x = 0; x < cols; x++)
                    cells[y * grid.Cols + x] = new Cell('▀', Pixels[top + x], Pixels[bot + x]);
            }
            return;
        }

        Span<Rgba> px = stackalloc Rgba[6];
        Span<int> lum = stackalloc int[6];
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < cols; x++)
        {
            int min = int.MaxValue, max = int.MinValue;
            for (int i = 0; i < 6; i++)
            {
                var c = Pixels[(3 * y + i / 2) * Width + 2 * x + (i & 1)];
                px[i] = c;
                int l = c.R * 299 + c.G * 587 + c.B * 114;
                lum[i] = l;
                if (l < min) min = l;
                if (l > max) max = l;
            }
            ref var cell = ref cells[y * grid.Cols + x];
            if (max - min < 8000) // почти однотонно (~8/255 по яркости) — одна заливка, без шума от квантования
            {
                var avg = Average(px, 63);
                cell = new Cell(' ', avg, avg);
                continue;
            }
            int mid = (min + max) / 2, bits = 0;
            for (int i = 0; i < 6; i++) if (lum[i] > mid) bits |= 1 << i;
            cell = new Cell(GlyphSet.Sextant(bits), Average(px, bits), Average(px, ~bits & 63));
        }
    }

    private static Rgba Average(ReadOnlySpan<Rgba> px, int mask)
    {
        int r = 0, g = 0, b = 0, n = 0;
        for (int i = 0; i < 6; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            r += px[i].R; g += px[i].G; b += px[i].B; n++;
        }
        return n == 0 ? Rgba.Black : new Rgba((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }
}

/// <summary>
/// Логика с фиксированной частотой, как у демок на PAL (50 Гц) / NTSC (60 Гц): Advance(dt) возвращает,
/// сколько тиков пора сделать в этом кадре. Время сцены дискретно — движение «ступеньками», как на железе.
/// </summary>
public struct RetroClock
{
    public float Hz;
    public long Ticks;
    private float _acc;

    public RetroClock(float hz) { Hz = hz; Ticks = 0; _acc = 0; }

    /// <summary>Время сцены в секундах, кратное 1/Hz.</summary>
    public readonly float Time => Ticks / Hz;

    /// <summary>Сколько тиков сделать (не больше 8 — после зависания не догоняем бесконечно).</summary>
    public int Advance(float dt)
    {
        _acc += Math.Max(0, dt) * Hz;
        int n = Math.Min(8, (int)_acc);
        _acc -= (int)_acc;
        Ticks += n;
        return n;
    }
}

/// <summary>Палитры железа эпохи.</summary>
public static class RetroPalettes
{
    /// <summary>Commodore 64, 16 цветов (Pepto/VICE): 0 чёрный … 15 светло-серый.</summary>
    public static readonly Rgba[] C64 =
    {
        Rgba.Hex(0x000000), Rgba.Hex(0xFFFFFF), Rgba.Hex(0x68372B), Rgba.Hex(0x70A4B2),
        Rgba.Hex(0x6F3D86), Rgba.Hex(0x588D43), Rgba.Hex(0x352879), Rgba.Hex(0xB8C76F),
        Rgba.Hex(0x6F4F25), Rgba.Hex(0x433900), Rgba.Hex(0x9A6759), Rgba.Hex(0x444444),
        Rgba.Hex(0x6C6C6C), Rgba.Hex(0x9AD284), Rgba.Hex(0x6C5EB5), Rgba.Hex(0x959595),
    };

    /// <summary>Amiga OCS: 4 бита на канал (4096 цветов). Квантует цвет как это делал copper.</summary>
    public static Rgba Amiga12(Rgba c) => new((byte)((c.R >> 4) * 17), (byte)((c.G >> 4) * 17), (byte)((c.B >> 4) * 17));

    /// <summary>Ближайший цвет палитры (евклид в RGB).</summary>
    public static Rgba Nearest(ReadOnlySpan<Rgba> pal, Rgba c)
    {
        int best = 0, bd = int.MaxValue;
        for (int i = 0; i < pal.Length; i++)
        {
            int dr = pal[i].R - c.R, dg = pal[i].G - c.G, db = pal[i].B - c.B;
            int d = dr * dr + dg * dg + db * db;
            if (d < bd) { bd = d; best = i; }
        }
        return pal[best];
    }

    /// <summary>Цвет из 256-цветной «копперной» таблицы: гладкий градиент по точкам (как раскраска плазмы на VGA).</summary>
    public static Rgba Gradient(ReadOnlySpan<Rgba> stops, float t)
    {
        t -= MathF.Floor(t);
        float f = t * stops.Length;
        int i = (int)f % stops.Length;
        return Rgba.Lerp(stops[i], stops[(i + 1) % stops.Length], f - (int)f);
    }
}
