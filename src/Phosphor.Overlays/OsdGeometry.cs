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

    /// <summary>Колонок OSD: ширина вьюпорта, поделённая на ширину клетки шрифта высоты CellHeight.</summary>
    public static int Cols(int vpW, int cellW) => Math.Max(1, (int)Math.Round((double)vpW / Math.Max(1, cellW)));
}
