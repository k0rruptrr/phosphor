using Phosphor.Text;

namespace Phosphor.Text.Tests;

/// <summary>Выбор основного шрифта сетки: ширина ячейки по шрифту, фолбэк на Consolas с предупреждением.</summary>
public class FontSelectionTests
{
    private const int CellH = 18;

    private static string BuiltinVt323 => Path.Combine(AppContext.BaseDirectory, "fonts", "VT323-Regular.ttf");

    [Fact]
    public void Vt323_GivesOtherCellWidth_ThanConsolas()
    {
        var consolas = GlyphRasterizer.Rasterize(CellH, "Consolas");
        var vt323 = GlyphRasterizer.Rasterize(CellH, "VT323");
        Assert.NotEqual(consolas.CellWidth, vt323.CellWidth);
        Assert.Equal(CellH, vt323.CellHeight);
    }

    [Fact]
    public void Path_ToBuiltinFile_GivesSameWidthAsName()
    {
        var byName = GlyphRasterizer.Rasterize(CellH, "VT323");
        var byPath = GlyphRasterizer.Rasterize(CellH, BuiltinVt323);
        Assert.Equal(byName.CellWidth, byPath.CellWidth);
    }

    [Fact]
    public void UnknownFont_FallsBackToConsolas_WithWarning()
    {
        var warnings = new List<string>();
        AtlasBitmap atlas = null!;
        var ex = Record.Exception(() => atlas = GlyphRasterizer.Rasterize(CellH, "НетТакогоШрифта-XYZ", warnings.Add));
        Assert.Null(ex);
        Assert.Single(warnings);
        Assert.Equal(GlyphRasterizer.Rasterize(CellH, "Consolas").CellWidth, atlas.CellWidth);
    }

    [Fact]
    public void NullOrEmptyFont_IsConsolas_WithoutWarning()
    {
        var warnings = new List<string>();
        var def = GlyphRasterizer.Rasterize(CellH, null, warnings.Add);
        var empty = GlyphRasterizer.Rasterize(CellH, "  ", warnings.Add);
        Assert.Empty(warnings);
        Assert.Equal(def.CellWidth, empty.CellWidth);
        Assert.Equal(GlyphRasterizer.Rasterize(CellH).CellWidth, def.CellWidth);
    }
}
