using System.Text.Json.Nodes;
using Phosphor.Core;

namespace Phosphor.Core.Tests;

/// <summary>Миграция маски: crt.mask (и crt.mod.mask) уходит из пресетов — маску теперь задаёт экран (screen.mask).</summary>
public sealed class MaskMigrationTests
{
    private const string Old = """
    {
      "filters": {
        "active": "mine",
        "presets": {
          "mine": [ { "id": "crt", "mask": 0.6, "bloom": 0.5,
                      "mod": { "mask": { "src": "breath", "amount": 0.1 }, "bloom": { "src": "bass", "amount": 0.4 } } } ],
          "other": [ { "id": "mono", "mask": 0.2 } ]
        }
      }
    }
    """;

    private static JsonObject Stage(PhosphorConfig cfg, string preset, int i) =>
        (JsonObject)JsonNode.Parse(cfg.Filters.Presets[preset].GetRawText())![i]!;

    [Fact]
    public void OldCrtMask_IsRemoved_BloomKept()
    {
        var cfg = PhosphorConfig.Parse(Old);
        var notes = new List<string>();

        Assert.True(cfg.MigrateScreen(notes));

        var crt = Stage(cfg, "mine", 0);
        Assert.False(crt.ContainsKey("mask"));
        Assert.Equal(0.5, crt["bloom"]!.GetValue<double>(), 6);
        var mod = (JsonObject)crt["mod"]!;
        Assert.False(mod.ContainsKey("mask"));
        Assert.True(mod.ContainsKey("bloom"));
        Assert.Single(notes);
    }

    [Fact]
    public void Second_Call_IsFalse()
    {
        var cfg = PhosphorConfig.Parse(Old);
        Assert.True(cfg.MigrateScreen(new List<string>()));
        Assert.False(cfg.MigrateScreen(new List<string>()));
    }

    [Fact]
    public void NonCrtStage_MaskIsNotTouched()
    {
        var cfg = PhosphorConfig.Parse(Old);
        cfg.MigrateScreen(new List<string>());

        Assert.Equal(0.2, Stage(cfg, "other", 0)["mask"]!.GetValue<double>(), 6);
    }

    [Fact]
    public void DefaultJson_CrtHasNoMask()
    {
        var cfg = PhosphorConfig.Parse(PhosphorConfig.DefaultJson);
        foreach (var (name, stages) in cfg.Filters.Presets)
        foreach (var stage in stages.EnumerateArray())
        {
            if (!stage.TryGetProperty("id", out var id) || id.GetString() != "crt") continue;
            Assert.False(stage.TryGetProperty("mask", out _), $"пресет {name}: crt содержит mask");
        }
    }
}
