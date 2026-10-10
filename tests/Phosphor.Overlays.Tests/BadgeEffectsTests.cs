using System.Text.Json;
using Phosphor.Core;
using Phosphor.Overlays;

namespace Phosphor.Overlays.Tests;

/// <summary>Эффекты бирок (подложка, тень, свечение, силуэт): параметры, значения из Configure, умолчания.</summary>
public class BadgeEffectsTests
{
    private static SceneOptions Opts(string json) => new() { Params = JsonDocument.Parse(json).RootElement };

    private static readonly string[] NewNames =
        { "plate", "plateColor", "plateOpacity", "shadow", "glow", "silhouette" };

    [Fact]
    public void Clock_without_params_has_all_effects_off()
    {
        var b = new ClockBadge();
        b.Configure(new SceneOptions());
        Assert.Equal("none", b.Effects.Plate);
        Assert.Equal("black", b.Effects.PlateColor);
        Assert.Equal(0f, b.Effects.Shadow);
        Assert.Equal(0f, b.Effects.Glow);
        Assert.False(b.Effects.Silhouette);
    }

    [Fact]
    public void Logo_without_params_has_all_effects_off()
    {
        var b = new LogoBadge();
        b.Configure(new SceneOptions());
        Assert.Equal("none", b.Effects.Plate);
        Assert.Equal(0f, b.Effects.Shadow);
        Assert.Equal(0f, b.Effects.Glow);
        Assert.False(b.Effects.Silhouette);
    }

    [Fact]
    public void Clock_plate_pill_is_read_into_effects()
    {
        var b = new ClockBadge();
        b.Configure(Opts("{\"plate\":\"pill\",\"plateColor\":\"white\",\"plateOpacity\":0.3}"));
        Assert.Equal("pill", b.Effects.Plate);
        Assert.Equal("white", b.Effects.PlateColor);
        Assert.Equal(0.3f, b.Effects.PlateOpacity, 4);
    }

    [Fact]
    public void Logo_shadow_glow_silhouette_are_read_into_effects()
    {
        var b = new LogoBadge();
        b.Configure(Opts("{\"shadow\":0.7,\"glow\":0.6,\"silhouette\":true,\"plate\":\"rounded\"}"));
        Assert.Equal(0.7f, b.Effects.Shadow, 4);
        Assert.Equal(0.6f, b.Effects.Glow, 4);
        Assert.True(b.Effects.Silhouette);
        Assert.Equal("rounded", b.Effects.Plate);
    }

    [Fact]
    public void Effects_values_are_clamped_to_range()
    {
        var b = new ClockBadge();
        b.Configure(Opts("{\"shadow\":5,\"glow\":-1,\"plateOpacity\":2}"));
        Assert.Equal(1f, b.Effects.Shadow, 4);
        Assert.Equal(0f, b.Effects.Glow, 4);
        Assert.Equal(1f, b.Effects.PlateOpacity, 4);
    }

    [Fact]
    public void Unknown_plate_falls_back_to_none()
    {
        var b = new LogoBadge();
        b.Configure(Opts("{\"plate\":\"hexagon\"}"));
        Assert.Equal("none", b.Effects.Plate);
    }

    [Fact]
    public void Tint_is_badge_color()
    {
        var b = new LogoBadge();
        b.Configure(Opts("{\"color\":\"green\"}"));
        Assert.Equal(Rgba.Hex(0x3CFF3C), b.Effects.Tint);
    }

    [Fact]
    public void New_params_are_appended_to_clock_and_logo()
    {
        foreach (IOverlayParams p in new IOverlayParams[] { new ClockBadge(), new LogoBadge() })
        {
            var names = p.Params.Select(s => s.Name).ToArray();
            Assert.Equal(NewNames, names[^NewNames.Length..]);
            var specs = p.Params.TakeLast(NewNames.Length).ToArray();
            Assert.Equal(SceneParamKind.Choice, specs[0].Kind);
            Assert.Equal(new[] { "none", "rounded", "pill" }, specs[0].Options);
            Assert.Equal(SceneParamKind.Choice, specs[1].Kind);
            Assert.Equal(new[] { "black", "white", "grey" }, specs[1].Options);
            Assert.Equal(SceneParamKind.Float, specs[2].Kind);
            Assert.Equal(SceneParamKind.Float, specs[3].Kind);
            Assert.Equal(SceneParamKind.Float, specs[4].Kind);
            Assert.Equal(SceneParamKind.Bool, specs[5].Kind);
            Assert.Equal(0f, specs[5].Default);
        }
    }

    [Fact]
    public void Logo_source_has_greyprism_as_last_option_and_default()
    {
        var spec = new LogoBadge().Params.First(s => s.Name == "source");
        Assert.Equal(new[] { "phosphor", "file", "text", "greyprism" }, spec.Options);
        Assert.Equal(3f, spec.Default);
        Assert.Equal(3f, spec.Max);
    }

    [Fact]
    public void Logo_source_greyprism_points_to_logo_greyprism_png()
    {
        var b = new LogoBadge();
        b.Configure(Opts("{\"source\":\"greyprism\"}"));
        Assert.EndsWith(Path.Combine("logo", "greyprism.png"), b.ImagePath!);
        Assert.False(b.FileMissing);
    }

    [Fact]
    public void Logo_stroke_label_is_plain_px()
    {
        var spec = new LogoBadge().Params.First(s => s.Name == "stroke");
        Assert.Equal("обводка, px", spec.Description);
    }
}
