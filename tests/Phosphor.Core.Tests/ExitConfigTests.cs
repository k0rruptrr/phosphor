using Phosphor.Core;

namespace Phosphor.Core.Tests;

public sealed class ExitConfigTests
{
    [Fact]
    public void OldConfigWithoutNewKeys_DefaultsToIgnoreBoth()
    {
        var cfg = PhosphorConfig.Parse("""{ "exit": { "mouseThresholdPx": 20 } }""");
        Assert.Equal(20, cfg.Exit.MouseThresholdPx);
        Assert.True(cfg.Exit.IgnoreMouseMove);
        Assert.True(cfg.Exit.IgnoreMediaKeys);
    }

    [Fact]
    public void ExplicitFalse_IsRespected()
    {
        var cfg = PhosphorConfig.Parse("""{ "exit": { "ignoreMouseMove": false, "ignoreMediaKeys": false } }""");
        Assert.False(cfg.Exit.IgnoreMouseMove);
        Assert.False(cfg.Exit.IgnoreMediaKeys);
    }
}
