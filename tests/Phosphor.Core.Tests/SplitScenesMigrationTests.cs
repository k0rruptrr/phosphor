using Phosphor.Core;

namespace Phosphor.Core.Tests;

/// <summary>clocks / hifi → отдельные сцены (0.12): MigrateSplitScenes, затем обычная MigrateScenes.</summary>
public sealed class SplitScenesMigrationTests
{
    private static readonly string[] Catalog =
        { "matrix", "splitflap", "nixie", "worldclock", "analogclock", "spectrum", "vumeter", "mediaplayer" };

    private static PhosphorConfig Run(string json, out List<string> notes)
    {
        var cfg = PhosphorConfig.Parse(json);
        notes = new List<string>();
        cfg.MigrateSplitScenes(notes);
        cfg.MigrateScenes(Catalog, notes);
        return cfg;
    }

    [Fact]
    public void EnabledOldScene_EnablesAllNewInItsPlace()
    {
        var cfg = Run("""{ "scenes": { "enabled": ["matrix", "clocks", "hifi"], "seen": ["matrix", "clocks", "hifi"] } }""", out var notes);
        Assert.Equal(new[] { "matrix", "splitflap", "nixie", "worldclock", "analogclock", "spectrum", "vumeter", "mediaplayer" }, cfg.Scenes.Enabled);
        Assert.DoesNotContain("clocks", cfg.Scenes.Seen!);
        Assert.DoesNotContain("hifi", cfg.Scenes.Seen!);
        Assert.Contains(notes, n => n.Contains("clocks"));
    }

    [Fact]
    public void DisabledOldScene_NewOnesStayDisabled()
    {
        var cfg = Run("""{ "scenes": { "enabled": ["matrix"], "seen": ["matrix", "clocks", "hifi"] } }""", out _);
        Assert.Equal(new[] { "matrix" }, cfg.Scenes.Enabled);
        foreach (var id in Catalog) Assert.Contains(id, cfg.Scenes.Seen!);
    }

    [Fact]
    public void OldConfigWithoutSeen_DisabledOldScene_NewOnesStayDisabled()
    {
        // seen == null: виденными считаются включённые; clocks упомянута только в параметрах — значит, её знали и выключили
        var cfg = Run("""{ "scenes": { "enabled": ["matrix"], "params": { "clocks": { "seconds": false } } } }""", out _);
        Assert.Equal(new[] { "matrix", "spectrum", "vumeter", "mediaplayer" }, cfg.Scenes.Enabled); // hifi не было вовсе — новые включаются
    }

    [Fact]
    public void FixedFace_EnablesOnlyThatScene_ParamsAndFavoritesMove()
    {
        var cfg = Run("""
        { "scenes": { "enabled": ["clocks", "hifi"], "seen": ["matrix", "clocks", "hifi"], "favorites": ["clocks"],
          "params": { "clocks": { "face": "nixie", "faceSec": 30, "seconds": false, "font": "VT323" },
                      "hifi": { "face": "vu", "peakHold": 2.0 } } } }
        """, out _);
        Assert.Equal(new[] { "nixie", "vumeter" }, cfg.Scenes.Enabled);
        Assert.Equal(new[] { "nixie" }, cfg.Scenes.Favorites);
        var p = cfg.Scenes.Params;
        Assert.False(p.TryGetProperty("clocks", out _));
        Assert.False(p.TryGetProperty("hifi", out _));
        var nixie = p.GetProperty("nixie");
        Assert.False(nixie.GetProperty("seconds").GetBoolean());
        Assert.Equal("VT323", nixie.GetProperty("font").GetString());
        Assert.False(nixie.TryGetProperty("face", out _));
        Assert.False(nixie.TryGetProperty("faceSec", out _));
        Assert.True(p.TryGetProperty("analogclock", out _)); // параметры переезжают ко всем, даже выключенным
        Assert.Equal(2.0, p.GetProperty("spectrum").GetProperty("peakHold").GetDouble());
    }

    [Fact]
    public void ExistingNewParams_AreNotOverwritten_AndMigrationIsIdempotent()
    {
        var cfg = PhosphorConfig.Parse("""
        { "scenes": { "enabled": ["clocks"], "seen": ["clocks"],
          "params": { "clocks": { "seconds": false }, "nixie": { "seconds": true } } } }
        """);
        var notes = new List<string>();
        Assert.True(cfg.MigrateSplitScenes(notes));
        Assert.True(cfg.Scenes.Params.GetProperty("nixie").GetProperty("seconds").GetBoolean());
        int n = notes.Count;
        Assert.False(cfg.MigrateSplitScenes(notes));
        Assert.Equal(n, notes.Count);
    }

    [Fact]
    public void FreshConfig_IsUntouched()
    {
        var cfg = PhosphorConfig.Parse(PhosphorConfig.DefaultJson);
        Assert.False(cfg.MigrateSplitScenes(new List<string>()));
        Assert.Contains("nixie", cfg.Scenes.Enabled);
        Assert.DoesNotContain("clocks", cfg.Scenes.Enabled);
    }
}
