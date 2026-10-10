using Phosphor.Core;

namespace Phosphor.Core.Tests;

public class OverlayConfigVcrTests
{
    [Fact]
    public void Vcr_is_on_by_default()
    {
        Assert.True(new OverlayConfig().Vcr);
    }

    [Fact]
    public void Vcr_applies_only_to_chains_with_vhs()
    {
        var cfg = new OverlayConfig();
        Assert.True(cfg.VcrFor(new[] { "vhs", "chroma", "crt" }));
        Assert.True(cfg.VcrFor(new[] { "VHS" }));
        Assert.False(cfg.VcrFor(new[] { "crt" }));
        Assert.False(cfg.VcrFor(Array.Empty<string>()));
    }

    [Fact]
    public void Vcr_false_disables_it_even_with_vhs()
    {
        var cfg = new OverlayConfig { Vcr = false };
        Assert.False(cfg.VcrFor(new[] { "vhs", "crt" }));
    }

    [Fact]
    public void Parses_vcr_key_from_overlay_section()
    {
        var cfg = PhosphorConfig.Parse("{ \"overlay\": { \"vcr\": false } }");
        Assert.False(cfg.Overlay.Vcr);
        Assert.True(PhosphorConfig.Parse("{}").Overlay.Vcr);
    }
}
