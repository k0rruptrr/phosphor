using Phosphor.Core;

namespace Phosphor.Core.Tests;

public sealed class AppInfoTests
{
    [Fact]
    public void Display_IsNotEmpty_AndContainsVersion()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppInfo.Display));
        Assert.False(string.IsNullOrWhiteSpace(AppInfo.Version));
        Assert.Contains(AppInfo.Version, AppInfo.Display);
    }

    [Fact]
    public void ParseInformationalVersion_FullHash_IsShortenedToSevenChars()
    {
        const string full = "701432d0cc82c5ad1a32a962ed3eea5a2c019a0b"; // 40 символов
        var (version, commit) = AppInfo.ParseInformationalVersion("0.7.0+" + full);

        Assert.Equal("0.7.0", version);
        Assert.Equal(7, commit.Length);
        Assert.Equal("701432d", commit);
    }

    [Fact]
    public void ParseInformationalVersion_DirtySuffix_IsKept()
    {
        var (version, commit) = AppInfo.ParseInformationalVersion("0.7.0+abc1234-dirty");

        Assert.Equal("0.7.0", version);
        Assert.Equal("abc1234-dirty", commit);
    }

    [Fact]
    public void ParseInformationalVersion_FullHashDirty_IsShortenedAndKeepsSuffix()
    {
        var (_, commit) = AppInfo.ParseInformationalVersion("0.7.0+701432d0cc82c5ad1a32a962ed3eea5a2c019a0b-dirty");

        Assert.Equal("701432d-dirty", commit);
    }

    [Fact]
    public void ParseInformationalVersion_NoHash_IsDev()
    {
        var (version, commit) = AppInfo.ParseInformationalVersion("0.7.0");

        Assert.Equal("0.7.0", version);
        Assert.Equal("dev", commit);
    }
}
