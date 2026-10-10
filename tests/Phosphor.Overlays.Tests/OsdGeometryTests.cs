using Phosphor.Overlays;

namespace Phosphor.Overlays.Tests;

public class OsdGeometryTests
{
    [Fact]
    public void Rows_are_fixed_at_60_and_cell_height_is_viewport_over_60()
    {
        Assert.Equal(60, OsdGeometry.Rows);
        Assert.Equal(18, OsdGeometry.CellHeight(1080));
        Assert.Equal(12, OsdGeometry.CellHeight(720));
        Assert.Equal(36, OsdGeometry.CellHeight(2160));
    }

    [Fact]
    public void Cell_height_is_clamped_to_font_range()
    {
        Assert.Equal(8, OsdGeometry.CellHeight(100));
        Assert.Equal(64, OsdGeometry.CellHeight(10000));
    }

    [Fact]
    public void Cols_fill_the_viewport_width_in_font_cells()
    {
        Assert.Equal(192, OsdGeometry.Cols(1920, 10));
        Assert.Equal(1, OsdGeometry.Cols(0, 10));
    }

    [Fact]
    public void RowsFor_PixelAndText_FollowFormula()
    {
        Assert.Equal(60, OsdGeometry.RowsFor("pixel", 1f));
        Assert.Equal(30, OsdGeometry.RowsFor("pixel", 2f));
        Assert.Equal(16, OsdGeometry.RowsFor("text", 1f));
        Assert.Equal(32, OsdGeometry.RowsFor("text", 0.5f));
    }

    [Fact]
    public void RowsFor_IsClampedTo8To200()
    {
        Assert.Equal(200, OsdGeometry.RowsFor("pixel", 0.25f));   // 240 -> 200
        Assert.Equal(8, OsdGeometry.RowsFor("text", 4f));          // 4 -> 8
    }

    [Fact]
    public void CellHeight_WithRows_IsViewportOverRowsClamped()
    {
        Assert.Equal(18, OsdGeometry.CellHeight(1080, 60));
        Assert.Equal(36, OsdGeometry.CellHeight(1080, 30));
        Assert.Equal(8, OsdGeometry.CellHeight(100, 60));
        Assert.Equal(128, OsdGeometry.CellHeight(10000, 10));
    }
}
