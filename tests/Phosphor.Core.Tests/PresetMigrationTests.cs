using Phosphor.Core;
using Xunit;

namespace Phosphor.Core.Tests;

public class PresetMigrationTests
{
    [Fact]
    public void OldConfig_GetsNewBuiltinPreset_Once()
    {
        // Старый конфиг пользователя: пресеты без hi8 и без filters.seen
        var cfg = PhosphorConfig.Parse("""{ "filters": { "active": "clean", "presets": { "clean": [ { "id": "crt" } ] } } }""");
        var notes = new List<string>();
        Assert.True(cfg.MigratePresets(notes));
        Assert.True(cfg.Filters.Presets.ContainsKey("hi8"));
        Assert.Contains(notes, n => n.Contains("hi8"));
        // Свой «clean» пользователя не перезаписан встроенным
        Assert.Equal(1, cfg.Filters.Presets["clean"].GetArrayLength());
        Assert.False(cfg.MigratePresets(new List<string>()));
    }

    [Fact]
    public void PresetDeletedByUser_IsNotRestored()
    {
        var cfg = PhosphorConfig.Parse(PhosphorConfig.DefaultJson);
        cfg.MigratePresets(new List<string>());
        cfg.Filters.Presets.Remove("hi8");
        Assert.False(cfg.MigratePresets(new List<string>()));
        Assert.False(cfg.Filters.Presets.ContainsKey("hi8"));
    }
}
