namespace Phosphor.App.Tests;

/// <summary>Геометрия эффектов бирки: поля холста (только для включённых эффектов) и размещение по подложке.</summary>
public sealed class BadgeEffectGeometryTests
{
    private const int W = 1920, H = 1080;
    private const float Eps = 0.01f;

    [Fact]
    public void Canvas_pad_is_zero_when_no_effects()
    {
        Assert.Equal(0, BadgeGeometry.CanvasPad(100, plate: false, shadow: 0f, glow: 0f, strokePx: 0f));
    }

    [Fact]
    public void Canvas_pad_is_positive_only_when_effect_enabled()
    {
        Assert.True(BadgeGeometry.CanvasPad(100, plate: true, shadow: 0f, glow: 0f, strokePx: 0f) > 0);
        Assert.True(BadgeGeometry.CanvasPad(100, plate: false, shadow: 0.5f, glow: 0f, strokePx: 0f) > 0);
        Assert.True(BadgeGeometry.CanvasPad(100, plate: false, shadow: 0f, glow: 0.5f, strokePx: 0f) > 0);
        Assert.Equal(0, BadgeGeometry.CanvasPad(100, plate: false, shadow: 0f, glow: 0f, strokePx: 0f));
    }

    [Fact]
    public void Canvas_pad_covers_shadow_and_glow_extents()
    {
        // Тень: смещение 0.04·h + размытие 0.06·h = 0.10·h; свечение: 0.12·h
        int th = 100;
        Assert.True(BadgeGeometry.CanvasPad(th, plate: false, shadow: 1f, glow: 0f, strokePx: 0f) >= 10);
        Assert.True(BadgeGeometry.CanvasPad(th, plate: false, shadow: 0f, glow: 1f, strokePx: 0f) >= 12);
    }

    [Fact]
    public void Canvas_pad_grows_with_stroke()
    {
        int small = BadgeGeometry.CanvasPad(100, plate: false, shadow: 0f, glow: 0f, strokePx: 1f);
        int big = BadgeGeometry.CanvasPad(100, plate: false, shadow: 0f, glow: 0f, strokePx: 6f);
        Assert.True(small > 0);
        Assert.True(big >= 6 && big > small);
    }

    [Fact]
    public void Screen_pixels_convert_to_texel_by_texture_scale()
    {
        // PNG 512 px, отрисован высотой 128 px экрана: 1 px экрана = 4 texel; обводка 2 px экрана → 8 texel
        Assert.InRange(BadgeGeometry.ToTexel(2f, 128f, 512), 8f - Eps, 8f + Eps);
        // Текст: масштаб 1 — пиксели совпадают
        Assert.InRange(BadgeGeometry.ToTexel(1.5f, 64f, 64), 1.5f - Eps, 1.5f + Eps);
    }

    [Fact]
    public void Plate_is_detected_only_for_rounded_and_pill()
    {
        Assert.True(BadgeGeometry.IsPlate("rounded"));
        Assert.True(BadgeGeometry.IsPlate("pill"));
        Assert.False(BadgeGeometry.IsPlate("none"));
        Assert.False(BadgeGeometry.IsPlate(null));
    }

    [Fact]
    public void Placement_by_plate_puts_plate_right_edge_at_w_minus_margin()
    {
        // Содержимое: высота 108 px (0.1·H), ширина 216 px; подложка на 0.15·h с каждой стороны → 1.3·h
        float h = 108f, w = 216f, inset = 0.15f * h;
        float plateW = w + 2 * inset, plateH = h + 2 * inset;
        float margin = 0.03f;

        var r = BadgeGeometry.PlaceBox("tr", plateW, plateH, margin, W, H);
        Assert.InRange(r.X + r.W, W - margin * H - Eps, W - margin * H + Eps);
        Assert.InRange(r.Y, margin * H - Eps, margin * H + Eps);
        Assert.InRange(r.H, plateH - Eps, plateH + Eps);
    }

    [Fact]
    public void Place_without_plate_matches_box_placement()
    {
        var a = BadgeGeometry.Place("br", 0.1f, 0.03f, 200, 100, W, H);
        var b = BadgeGeometry.PlaceBox("br", a.W, a.H, 0.03f, W, H);
        Assert.InRange(a.X - b.X, -Eps, Eps);
        Assert.InRange(a.Y - b.Y, -Eps, Eps);
    }
}
