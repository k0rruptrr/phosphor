using System.Text.Json;
using System.Text.Json.Nodes;
using Phosphor.Core;

namespace Phosphor.Core.Tests;

public sealed class ScreenMigrationTests
{
    private static readonly string[] GeometryKeys = { "curvature", "cornerRadius", "vignette" };

    private const string Old = """
    {
      "filters": {
        "active": "mine",
        "presets": {
          "mine": [ { "id": "crt", "curvature": 0.1, "bloom": 0.5,
                      "mod": { "curvature": { "src": "breath", "amount": 0.1 }, "bloom": { "src": "bass", "amount": 0.4 } } },
                    { "id": "mono" } ],
          "other": [ { "id": "mono", "vignette": 0.3 } ]
        }
      }
    }
    """;

    private static JsonObject Stage(PhosphorConfig cfg, string preset, int i) =>
        (JsonObject)JsonNode.Parse(cfg.Filters.Presets[preset].GetRawText())![i]!;

    [Fact]
    public void OldCrtGeometry_IsRemoved_RestKept()
    {
        var cfg = PhosphorConfig.Parse(Old);
        var notes = new List<string>();

        Assert.True(cfg.MigrateScreen(notes));

        var crt = Stage(cfg, "mine", 0);
        foreach (var key in GeometryKeys) Assert.False(crt.ContainsKey(key), key);
        Assert.Equal(0.5, crt["bloom"]!.GetValue<double>(), 6);
        var mod = (JsonObject)crt["mod"]!;
        Assert.False(mod.ContainsKey("curvature"));
        Assert.True(mod.ContainsKey("bloom"));
        Assert.Equal("mono", Stage(cfg, "mine", 1)["id"]!.GetValue<string>());
        Assert.Single(notes);
    }

    [Fact]
    public void Second_Call_IsNoOp()
    {
        var cfg = PhosphorConfig.Parse(Old);
        Assert.True(cfg.MigrateScreen(new List<string>()));

        var notes = new List<string>();
        Assert.False(cfg.MigrateScreen(notes));
        Assert.Empty(notes);
    }

    [Fact]
    public void NonCrtStages_AreNotTouched()
    {
        var cfg = PhosphorConfig.Parse(Old);
        cfg.MigrateScreen(new List<string>());

        Assert.Equal(0.3, Stage(cfg, "other", 0)["vignette"]!.GetValue<double>(), 6);
    }

    [Fact]
    public void DefaultJson_HasNoGeometryKeysInCrt()
    {
        var cfg = PhosphorConfig.Parse(PhosphorConfig.DefaultJson);

        Assert.False(cfg.MigrateScreen(new List<string>()));
        foreach (var (name, stages) in cfg.Filters.Presets)
        foreach (var stage in stages.EnumerateArray())
        {
            if (!stage.TryGetProperty("id", out var id) || id.GetString() != "crt") continue;
            foreach (var key in GeometryKeys)
                Assert.False(stage.TryGetProperty(key, out _), $"пресет {name}: crt содержит {key}");
        }
    }
}
