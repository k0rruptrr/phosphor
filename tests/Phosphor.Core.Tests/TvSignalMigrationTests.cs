using System.Text.Json.Nodes;
using Phosphor.Core;

namespace Phosphor.Core.Tests;

public sealed class TvSignalMigrationTests
{
    private const string Old = """
    {
      "filters": {
        "active": "secam",
        "presets": {
          "secam": [ { "id": "tv", "mode": "secam", "signal": 0.3, "fires": 0.5,
                       "mod": { "signal": { "src": "fatigue", "amount": -0.5 }, "fires": { "src": "breath", "amount": 0.2 } } },
                     { "id": "crt" } ],
          "other": [ { "id": "tv", "mode": "pal" }, { "id": "mono" } ]
        }
      }
    }
    """;

    private static JsonObject Stage(PhosphorConfig cfg, string preset, int i) =>
        (JsonObject)JsonNode.Parse(cfg.Filters.Presets[preset].GetRawText())![i]!;

    [Fact]
    public void SignalStage_BecomesInterference_Inverted()
    {
        var cfg = PhosphorConfig.Parse(Old);
        var notes = new List<string>();

        Assert.True(cfg.MigrateTvSignal(notes));

        var tv = Stage(cfg, "secam", 0);
        Assert.False(tv.ContainsKey("signal"));
        Assert.Equal(0.7, tv["interference"]!.GetValue<double>(), 6);
        Assert.Equal(0.5, tv["fires"]!.GetValue<double>(), 6);
        Assert.Equal("crt", Stage(cfg, "secam", 1)["id"]!.GetValue<string>());
        Assert.Single(notes);
    }

    [Fact]
    public void SignalMod_BecomesInterference_AmountSignFlipped()
    {
        var cfg = PhosphorConfig.Parse(Old);
        Assert.True(cfg.MigrateTvSignal(new List<string>()));

        var mod = (JsonObject)Stage(cfg, "secam", 0)["mod"]!;
        Assert.False(mod.ContainsKey("signal"));
        var inter = (JsonObject)mod["interference"]!;
        Assert.Equal("fatigue", inter["src"]!.GetValue<string>());
        Assert.Equal(0.5, inter["amount"]!.GetValue<double>(), 6);
        // чужие модуляции не трогаются
        Assert.True(mod.ContainsKey("fires"));
    }

    [Fact]
    public void NonTvPresets_AreNotTouched()
    {
        var cfg = PhosphorConfig.Parse(Old);
        Assert.True(cfg.MigrateTvSignal(new List<string>()));

        var other = Stage(cfg, "other", 0);
        Assert.Equal("pal", other["mode"]!.GetValue<string>());
        Assert.False(other.ContainsKey("interference"));
    }

    [Fact]
    public void Second_Call_IsNoOp()
    {
        var cfg = PhosphorConfig.Parse(Old);
        Assert.True(cfg.MigrateTvSignal(new List<string>()));

        var notes = new List<string>();
        Assert.False(cfg.MigrateTvSignal(notes));
        Assert.Empty(notes);
        Assert.Equal(0.7, Stage(cfg, "secam", 0)["interference"]!.GetValue<double>(), 6);
    }
}
