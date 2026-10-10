using Phosphor.Core;
using Phosphor.App;

namespace Phosphor.App.Tests;

/// <summary>Фейковая бирка: только для проверки геометрии, без GL и без текста.</summary>
internal sealed class FakeBadge : IBadge
{
    public string Id => "fake";
    public int Cols => 4;
    public int Rows => 1;
    public string? ImagePath => null;
    public BadgePlacement Placement { get; init; } = new("br", 0.1f, 0.03f, 1f, false, true, 0f, "");
    public long Version => 1;
    public void Update(float dt, in OverlayContext ctx) { }
    public void Draw(CharGrid grid) { }
}

/// <summary>Размещение бирки (9 якорей, отступы, пропорции) и дрейф (ограничение, плавность, детерминизм).</summary>
public sealed class BadgeGeometryTests
{
    private const int W = 1920, H = 1080;
    private const int TexW = 200, TexH = 100;   // пропорция 2:1 → при высоте 108 px ширина 216 px
    private const float Size = 0.1f, Margin = 0.03f;
    private const float Eps = 0.01f;

    private static BadgeRect At(string anchor) => BadgeGeometry.Place(anchor, Size, Margin, TexW, TexH, W, H);

    [Fact]
    public void Nine_anchors_land_on_expected_corners_and_centers()
    {
        float m = Margin * H;                 // 32.4
        float w = 216f, h = 108f;             // Size·H, пропорция 2:1
        float cx = (W - w) / 2f, cy = (H - h) / 2f;

        AssertRect(At("tl"), m, m, w, h);
        AssertRect(At("tc"), cx, m, w, h);
        AssertRect(At("tr"), W - m - w, m, w, h);
        AssertRect(At("ml"), m, cy, w, h);
        AssertRect(At("mc"), cx, cy, w, h);
        AssertRect(At("mr"), W - m - w, cy, w, h);
        AssertRect(At("bl"), m, H - m - h, w, h);
        AssertRect(At("bc"), cx, H - m - h, w, h);
        AssertRect(At("br"), W - m - w, H - m - h, w, h);
    }

    [Fact]
    public void Margin_is_fraction_of_height_and_zero_margin_touches_edges()
    {
        var r = BadgeGeometry.Place("tl", Size, 0f, TexW, TexH, W, H);
        Assert.InRange(r.X, -Eps, Eps);
        Assert.InRange(r.Y, -Eps, Eps);

        var m2 = BadgeGeometry.Place("tl", Size, 0.05f, TexW, TexH, W, H * 2);
        // Отступ 0.05 от высоты 2160 = 108 px
        Assert.InRange(m2.X, 108f - Eps, 108f + Eps);
        Assert.InRange(m2.Y, 108f - Eps, 108f + Eps);
    }

    [Fact]
    public void Width_follows_texture_aspect_and_height_is_size_times_H()
    {
        var wide = BadgeGeometry.Place("tl", Size, Margin, 300, 100, W, H);
        Assert.InRange(wide.H, 108f - Eps, 108f + Eps);
        Assert.InRange(wide.W / wide.H, 3f - 0.001f, 3f + 0.001f);

        var tall = BadgeGeometry.Place("tl", Size, Margin, 50, 100, W, H);
        Assert.InRange(tall.W / tall.H, 0.5f - 0.001f, 0.5f + 0.001f);
    }

    [Fact]
    public void Unknown_anchor_falls_back_to_bottom_right()
    {
        AssertRect(BadgeGeometry.Place("xx", Size, Margin, TexW, TexH, W, H), At("br"));
    }

    [Fact]
    public void Fake_badge_placement_drives_rect_inside_the_screen()
    {
        IBadge b = new FakeBadge { Placement = new("tr", 0.2f, 0.02f, 0.5f, true, false, 1f, "") };
        var r = BadgeGeometry.Place(b.Placement.Anchor, b.Placement.Size, b.Placement.Margin, TexW, TexH, W, H);
        Assert.True(r.X >= 0 && r.Y >= 0 && r.X + r.W <= W && r.Y + r.H <= H);
        Assert.InRange(r.H, 216f - Eps, 216f + Eps);   // 0.2 · 1080
        Assert.InRange(r.X + r.W, W - 0.02f * H - Eps, W - 0.02f * H + Eps);
    }

    [Fact]
    public void Drift_stays_within_6_px_per_1080_rows()
    {
        for (double t = 0; t < 600; t += 0.1)
        {
            var (dx, dy) = BadgeGeometry.DriftOffset(0, t, 1080);
            Assert.True(Math.Abs(dx) <= 6f + 1e-4f && Math.Abs(dy) <= 6f + 1e-4f, $"t={t}: ({dx},{dy})");
            var (dx2, dy2) = BadgeGeometry.DriftOffset(1, t, 2160);
            Assert.True(Math.Abs(dx2) <= 12f + 1e-4f && Math.Abs(dy2) <= 12f + 1e-4f, $"2160 t={t}: ({dx2},{dy2})");
        }
    }

    [Fact]
    public void Drift_is_smooth_between_frames_including_target_switches()
    {
        // Шаг кадра 16 мс: сдвиг меняется меньше 0.1 px — и внутри перехода, и на стыке сегментов (120, 240 с)
        for (double t = 0; t < 400; t += 0.016)
        {
            var a = BadgeGeometry.DriftOffset(0, t, 1080);
            var b = BadgeGeometry.DriftOffset(0, t + 0.016, 1080);
            float d = MathF.Sqrt((a.Dx - b.Dx) * (a.Dx - b.Dx) + (a.Dy - b.Dy) * (a.Dy - b.Dy));
            Assert.True(d < 0.1f, $"скачок {d} px at t={t}");
        }
    }

    [Fact]
    public void Drift_is_deterministic_and_differs_between_monitors()
    {
        var a1 = BadgeGeometry.DriftOffset(0, 75.5, 1080);
        var a2 = BadgeGeometry.DriftOffset(0, 75.5, 1080);
        Assert.Equal(a1, a2);

        var m0 = BadgeGeometry.DriftOffset(0, 300, 1080);
        var m1 = BadgeGeometry.DriftOffset(1, 300, 1080);
        Assert.False(Math.Abs(m0.Dx - m1.Dx) < 0.01f && Math.Abs(m0.Dy - m1.Dy) < 0.01f);
    }

    [Fact]
    public void Drift_starts_at_origin()
    {
        var r = BadgeGeometry.DriftOffset(3, 0, 1080);
        Assert.InRange(r.Dx, -Eps, Eps);
        Assert.InRange(r.Dy, -Eps, Eps);
    }

    private static void AssertRect(BadgeRect r, BadgeRect expected) =>
        AssertRect(r, expected.X, expected.Y, expected.W, expected.H);

    private static void AssertRect(BadgeRect r, float x, float y, float w, float h)
    {
        Assert.InRange(r.X, x - Eps, x + Eps);
        Assert.InRange(r.Y, y - Eps, y + Eps);
        Assert.InRange(r.W, w - Eps, w + Eps);
        Assert.InRange(r.H, h - Eps, h + Eps);
    }
}
