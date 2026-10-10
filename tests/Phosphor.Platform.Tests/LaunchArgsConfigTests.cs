using Phosphor.Platform;

namespace Phosphor.Platform.Tests;

/// <summary>--config &lt;путь&gt;: вместо phosphor.json, допустим во всех режимах.</summary>
public class LaunchArgsConfigTests
{
    [Fact]
    public void Config_AllowedInEveryMode()
    {
        string[][] modes =
        {
            new[] { "/s" },
            new[] { "/c" },
            new[] { "/w" },
            new[] { "/p", "42" },
            new[] { "--shot", "out.png" },
            new[] { "--settings-shot", "out.png" },
        };
        foreach (var mode in modes)
        {
            var args = mode.Concat(new[] { "--config", "C:\\tmp\\cfg.json" }).ToArray();
            var a = LaunchArgs.Parse(args);
            Assert.Equal("C:\\tmp\\cfg.json", a.ConfigPath);
        }
    }

    [Fact]
    public void Config_DefaultsToNull()
    {
        Assert.Null(LaunchArgs.Parse(new[] { "/s" }).ConfigPath);
        Assert.Null(LaunchArgs.Parse(Array.Empty<string>()).ConfigPath);
    }

    [Fact]
    public void Config_WithPreview_ParsesSceneAndPreset()
    {
        var a = LaunchArgs.Parse(new[] { "/p", "7", "--scene", "matrix", "--preset", "terminal", "--config", "C:\\t\\p.json" });
        Assert.Equal(LaunchMode.Preview, a.Mode);
        Assert.Equal("matrix", a.Scene);
        Assert.Equal("terminal", a.Preset);
        Assert.Equal("C:\\t\\p.json", a.ConfigPath);
    }

    [Fact]
    public void Config_WithoutValue_Throws()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/s", "--config" }));
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/s", "--config", "--scene" }));
    }

    [Fact]
    public void Config_GivenTwice_Throws()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/s", "--config", "a.json", "--config", "b.json" }));
    }
}
