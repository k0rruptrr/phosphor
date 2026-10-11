using Phosphor.Core;

namespace Phosphor.Core.Tests;

public sealed class Hi8PresetTests
{
    private static void AssertHi8Parses(string json, string source)
    {
        var cfg = PhosphorConfig.Parse(json);
        Assert.True(cfg.Filters.Presets.ContainsKey("hi8"), $"{source}: пресет hi8 не найден");
        var warnings = new List<string>();
        cfg.Validate(warnings);
        Assert.DoesNotContain(warnings, w => w.Contains("hi8", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Hi8_InDefaultJson_ParsesWithoutWarnings()
    {
        AssertHi8Parses(PhosphorConfig.DefaultJson, "DefaultJson");
    }

    [Fact]
    public void Hi8_InRootPhosphorJson_ParsesWithoutWarnings()
    {
        // Ищем корневой phosphor.json, поднимаясь от каталога сборки теста.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? found = null;
        for (var d = dir; d is not null && found is null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "phosphor.json");
            if (File.Exists(candidate)) found = candidate;
        }
        Assert.NotNull(found);
        AssertHi8Parses(File.ReadAllText(found!), found!);
    }
}
