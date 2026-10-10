using Phosphor.Core;

namespace Phosphor.Core.Tests;

public sealed class ConfigSaveTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phosphor-core-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Текст JSON без пробелов и отступов — для сравнения структуры.</summary>
    private static string Normalize(System.Text.Json.JsonElement e) =>
        System.Text.Json.JsonSerializer.Serialize(e);

    [Fact]
    public void SaveParseRoundTrip_KeepsPresetsAndParams_AndCreatesBak()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "phosphor.json");
            var original = PhosphorConfig.DefaultJson; // старый конфиг: без scenes.seen
            File.WriteAllText(path, original);

            var cfg = PhosphorConfig.Parse(original);
            cfg.Scenes.Seen = new List<string> { "matrix", "organism" };
            cfg.Save(path);

            Assert.True(File.Exists(path + ".bak"));
            Assert.Equal(original, File.ReadAllText(path + ".bak"));

            var back = PhosphorConfig.Parse(File.ReadAllText(path));
            Assert.Equal(cfg.Filters.Presets.Count, back.Filters.Presets.Count);
            foreach (var key in cfg.Filters.Presets.Keys)
                Assert.Equal(Normalize(cfg.Filters.Presets[key]), Normalize(back.Filters.Presets[key]));
            Assert.Equal(Normalize(cfg.Scenes.Params), Normalize(back.Scenes.Params));
            Assert.Equal(cfg.Scenes.Seen, back.Scenes.Seen);
            Assert.Equal(cfg.Scenes.Enabled, back.Scenes.Enabled);

            // Структурно то же самое: повторная запись даёт тот же текст
            var second = Path.Combine(dir, "second.json");
            back.Save(second);
            Assert.Equal(File.ReadAllText(path), File.ReadAllText(second));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Save_DoesNotOverwriteExistingBak()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "phosphor.json");
            File.WriteAllText(path, PhosphorConfig.DefaultJson);
            File.WriteAllText(path + ".bak", "старая копия");

            PhosphorConfig.Parse(PhosphorConfig.DefaultJson).Save(path);

            Assert.Equal("старая копия", File.ReadAllText(path + ".bak"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Validate_ShuffleActive_IsNotAWarning()
    {
        var cfg = PhosphorConfig.Parse("""{ "filters": { "active": "shuffle" } }""");
        var warnings = new List<string>();

        cfg.Validate(warnings);

        Assert.Empty(warnings);
        Assert.Equal("shuffle", cfg.Filters.Active);
    }

    [Fact]
    public void Validate_UnknownPreset_StillFallsBack()
    {
        var cfg = PhosphorConfig.Parse("""{ "filters": { "active": "nope", "presets": { "clean": [] } } }""");
        var warnings = new List<string>();

        cfg.Validate(warnings);

        Assert.Single(warnings);
        Assert.Equal("clean", cfg.Filters.Active);
    }

    [Fact]
    public void Validate_ClampsFavoriteWeight()
    {
        var cfg = PhosphorConfig.Parse("""{ "shuffle": { "favoriteWeight": 50 } }""");
        cfg.Validate(new List<string>());
        Assert.Equal(10f, cfg.Shuffle.FavoriteWeight);

        cfg = PhosphorConfig.Parse("""{ "shuffle": { "favoriteWeight": 0 } }""");
        cfg.Validate(new List<string>());
        Assert.Equal(1f, cfg.Shuffle.FavoriteWeight);
    }
}
