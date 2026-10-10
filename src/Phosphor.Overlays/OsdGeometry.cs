namespace Phosphor.Overlays;

/// <summary>
/// Геометрия OSD видика: постоянное число строк на любой высоте вьюпорта, поэтому буквы одного размера на всех сценах.
/// Высота клетки OSD = H / Rows; ширина — из шрифта этой высоты, колонок столько, чтобы сетка легла на вьюпорт.
/// </summary>
public static class OsdGeometry
{
    public const int Rows = 60;

    /// <summary>Высота клетки OSD в пикселях (как у шрифта: 8..64).</summary>
    public static int CellHeight(int vpH) => Math.Clamp(vpH / Rows, 8, 64);

    /// <summary>Высота клетки OSD для заданного числа строк: H / rows, как у шрифта (8..128).</summary>
    public static int CellHeight(int vpH, int rows) => Math.Clamp(vpH / Math.Max(1, rows), 8, 128);

    /// <summary>
    /// Число строк OSD для стиля и размера (overlay.params.vcr): pixel → round(60 / size), text → round(16 / size), кламп 8..200.
    /// Стиль не "text" считается pixel.
    /// </summary>
    public static int RowsFor(string style, float size)
    {
        float baseRows = style == "text" ? 16f : 60f;
        float s = MathF.Max(size, 0.01f);
        return Math.Clamp((int)MathF.Round(baseRows / s), 8, 200);
    }

    /// <summary>Колонок OSD: ширина вьюпорта, поделённая на ширину клетки шрифта высоты CellHeight.</summary>
    public static int Cols(int vpW, int cellW) => Math.Max(1, (int)Math.Round((double)vpW / Math.Max(1, cellW)));
}
