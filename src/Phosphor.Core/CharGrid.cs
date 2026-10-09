namespace Phosphor.Core;

/// <summary>Одна ячейка сетки: символ, цвет символа, цвет фона.</summary>
public struct Cell
{
    public char Ch;
    public Rgba Fg;
    public Rgba Bg;
    public Cell(char ch, Rgba fg, Rgba bg) { Ch = ch; Fg = fg; Bg = bg; }
}

/// <summary>
/// Сетка символов — единственное, что видит сцена. Row-major: индекс = y * Cols + x.
/// (0,0) — левый верхний угол. Пробел рисует только фон.
/// </summary>
public sealed class CharGrid
{
    public int Cols { get; }
    public int Rows { get; }
    public Cell[] Cells { get; }

    public CharGrid(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0) throw new ArgumentOutOfRangeException(nameof(cols), "Сетка должна быть хотя бы 1x1");
        Cols = cols; Rows = rows;
        Cells = new Cell[cols * rows];
        Clear(Rgba.Black);
    }

    public bool InBounds(int x, int y) => (uint)x < (uint)Cols && (uint)y < (uint)Rows;

    /// <summary>Прямой доступ. За границей — IndexOutOfRangeException (если не уверен, используй Put).</summary>
    public ref Cell this[int x, int y]
    {
        get
        {
            if (!InBounds(x, y)) throw new IndexOutOfRangeException($"({x},{y}) вне сетки {Cols}x{Rows}");
            return ref Cells[y * Cols + x];
        }
    }

    /// <summary>Заполнить всю сетку пробелами с фоном bg.</summary>
    public void Clear(Rgba bg) => Array.Fill(Cells, new Cell(' ', Rgba.White, bg));

    /// <summary>Поставить символ. За границей — молча игнор. bg=null — фон ячейки не трогаем.</summary>
    public void Put(int x, int y, char ch, Rgba fg, Rgba? bg = null)
    {
        if (!InBounds(x, y)) return;
        ref var c = ref Cells[y * Cols + x];
        c.Ch = ch; c.Fg = fg;
        if (bg.HasValue) c.Bg = bg.Value;
    }

    /// <summary>Строка слева направо от (x,y). Что за границей — обрезается.</summary>
    public void Text(int x, int y, string s, Rgba fg, Rgba? bg = null)
    {
        for (int i = 0; i < s.Length; i++) Put(x + i, y, s[i], fg, bg);
    }
}
