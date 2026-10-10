using Phosphor.Core;

namespace Phosphor.Core.Tests;

public sealed class MigrationTests
{
    private static readonly string[] Catalog = { "a", "b", "c", "d" };

    [Fact]
    public void OldConfigWithoutSeen_EnablesNewScenes_AndIsIdempotent()
    {
        var cfg = PhosphorConfig.Parse("""{ "scenes": { "enabled": ["a", "b"] } }""");
        Assert.Null(cfg.Scenes.Seen);
        var notes = new List<string>();

        Assert.True(cfg.MigrateScenes(Catalog, notes));

        Assert.Contains("c", cfg.Scenes.Enabled);
        Assert.Contains("d", cfg.Scenes.Enabled);
        Assert.Equal(new[] { "a", "b", "c", "d" }, cfg.Scenes.Seen);
        Assert.Equal(2, notes.Count);

        Assert.False(cfg.MigrateScenes(Catalog, notes));
        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public void UserDisabledScenes_StayDisabled_OnlyNewOneIsEnabled()
    {
        var cfg = PhosphorConfig.Parse("""{ "scenes": { "enabled": ["a"], "seen": ["a", "b", "c"] } }""");

        Assert.True(cfg.MigrateScenes(Catalog, new List<string>()));

        Assert.Equal(new[] { "a", "d" }, cfg.Scenes.Enabled);
        Assert.Equal(new[] { "a", "b", "c", "d" }, cfg.Scenes.Seen);
    }

    [Fact]
    public void SceneRemovedFromCatalog_IsNotTouched_NoThrow()
    {
        var cfg = PhosphorConfig.Parse("""{ "scenes": { "enabled": ["a", "x"], "seen": ["a", "x"] } }""");

        Assert.False(cfg.MigrateScenes(new[] { "a" }, new List<string>()));

        Assert.Equal(new[] { "a", "x" }, cfg.Scenes.Enabled);
    }

    [Fact]
    public void CaseInsensitiveIds_AreNotDuplicated()
    {
        var cfg = PhosphorConfig.Parse("""{ "scenes": { "enabled": ["A"], "seen": ["a"] } }""");

        Assert.False(cfg.MigrateScenes(new[] { "A" }, new List<string>()));
        Assert.Single(cfg.Scenes.Enabled);
    }
}
