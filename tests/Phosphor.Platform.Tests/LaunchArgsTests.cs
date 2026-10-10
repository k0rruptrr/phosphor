using Phosphor.Platform;
using Xunit;

namespace Phosphor.Platform.Tests;

public class LaunchArgsTests
{
    [Fact]
    public void NoArgs_IsConfigureWithZeroParent()
    {
        var a = LaunchArgs.Parse(Array.Empty<string>());
        Assert.Equal(LaunchMode.Configure, a.Mode);
        Assert.Equal((nint)0, a.ParentHwnd);
    }

    [Theory]
    [InlineData("/s")]
    [InlineData("/S")]
    [InlineData("-s")]
    public void ScreensaverFlags_AreScreensaverMode(string flag)
    {
        Assert.Equal(LaunchMode.Screensaver, LaunchArgs.Parse(new[] { flag }).Mode);
    }

    [Fact]
    public void PreviewWithSpacedHwnd_ParsesParent()
    {
        var a = LaunchArgs.Parse(new[] { "/p", "12345" });
        Assert.Equal(LaunchMode.Preview, a.Mode);
        Assert.Equal((nint)12345, a.ParentHwnd);
    }

    [Theory]
    [InlineData("/p:12345")]
    [InlineData("/P:12345")]
    public void PreviewWithColonHwnd_ParsesParent(string arg)
    {
        var a = LaunchArgs.Parse(new[] { arg });
        Assert.Equal(LaunchMode.Preview, a.Mode);
        Assert.Equal((nint)12345, a.ParentHwnd);
    }

    [Fact]
    public void PreviewWithoutNumber_Throws()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/p" }));
    }

    [Fact]
    public void PreviewWithNonNumber_Throws()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/p", "abc" }));
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/p:abc" }));
    }

    [Fact]
    public void PreviewWithZeroHwnd_Throws()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/p", "0" }));
    }

    [Fact]
    public void ConfigureFlag_HasZeroParent()
    {
        var a = LaunchArgs.Parse(new[] { "/c" });
        Assert.Equal(LaunchMode.Configure, a.Mode);
        Assert.Equal((nint)0, a.ParentHwnd);
    }

    [Fact]
    public void ConfigureWithColonHwnd_ParsesParent()
    {
        var a = LaunchArgs.Parse(new[] { "/c:12345" });
        Assert.Equal(LaunchMode.Configure, a.Mode);
        Assert.Equal((nint)12345, a.ParentHwnd);
    }

    [Fact]
    public void ConfigureWithSpacedHwnd_ParsesParent()
    {
        var a = LaunchArgs.Parse(new[] { "/c", "12345" });
        Assert.Equal(LaunchMode.Configure, a.Mode);
        Assert.Equal((nint)12345, a.ParentHwnd);
    }

    [Fact]
    public void Windowed_HasDefaults()
    {
        var a = LaunchArgs.Parse(new[] { "/w" });
        Assert.Equal(LaunchMode.Windowed, a.Mode);
        Assert.Equal(1280, a.Width);
        Assert.Equal(720, a.Height);
        Assert.Equal(1, a.Seed);
        Assert.Equal(120, a.Frames);
        Assert.Null(a.Scene);
        Assert.Null(a.Preset);
    }

    [Fact]
    public void Shot_ParsesPath()
    {
        var a = LaunchArgs.Parse(new[] { "--shot", "out.png" });
        Assert.Equal(LaunchMode.Shot, a.Mode);
        Assert.Equal("out.png", a.ShotPath);
    }

    [Fact]
    public void Options_WorkWithWindowedInAnyOrder()
    {
        var a = LaunchArgs.Parse(new[]
        {
            "--scene", "aurora", "--size", "1920x1080", "/w", "--preset", "crt", "--seed", "7", "--frames", "30",
        });
        Assert.Equal(LaunchMode.Windowed, a.Mode);
        Assert.Equal("aurora", a.Scene);
        Assert.Equal("crt", a.Preset);
        Assert.Equal(1920, a.Width);
        Assert.Equal(1080, a.Height);
        Assert.Equal(7, a.Seed);
        Assert.Equal(30, a.Frames);
    }

    [Fact]
    public void Options_WorkWithShotInAnyOrder()
    {
        var a = LaunchArgs.Parse(new[] { "--size", "640x360", "--shot", "a.png", "--scene", "s1", "--frames", "5" });
        Assert.Equal(LaunchMode.Shot, a.Mode);
        Assert.Equal("a.png", a.ShotPath);
        Assert.Equal("s1", a.Scene);
        Assert.Equal(640, a.Width);
        Assert.Equal(360, a.Height);
        Assert.Equal(5, a.Frames);
    }

    [Fact]
    public void SizeWithUppercaseX_Parses()
    {
        var a = LaunchArgs.Parse(new[] { "/w", "--size", "800X600" });
        Assert.Equal(800, a.Width);
        Assert.Equal(600, a.Height);
    }

    [Theory]
    [InlineData("1920")]
    [InlineData("0x10")]
    [InlineData("10x0")]
    [InlineData("-5x10")]
    [InlineData("axb")]
    public void BadSize_Throws(string size)
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/w", "--size", size }));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("many")]
    public void BadFrames_Throws(string frames)
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "--shot", "a.png", "--frames", frames }));
    }

    [Fact]
    public void BadSeed_Throws()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/w", "--seed", "xyz" }));
    }

    [Fact]
    public void UnknownArgument_Throws()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/w", "--bogus" }));
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/x" }));
    }

    [Fact]
    public void OptionsWithScreensaverMode_Throw()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/s", "--scene", "a" }));
    }

    [Fact]
    public void TwoModes_Throw()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/s", "/p", "1" }));
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/w", "/c" }));
    }

    [Fact]
    public void ShotWithoutPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "--shot" }));
    }

    [Fact]
    public void OptionWithoutValue_Throws()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/w", "--scene" }));
    }

    [Fact]
    public void DuplicateOption_Throws()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/w", "--seed", "1", "--seed", "2" }));
    }

    [Fact]
    public void OptionsWithoutMode_Throw()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "--scene", "a" }));
    }

    [Fact]
    public void NullArray_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LaunchArgs.Parse(null!));
    }

    [Fact]
    public void PreviewWithSceneAndPreset_ParsesBoth()
    {
        var a = LaunchArgs.Parse(new[] { "/p", "12345", "--scene", "matrix", "--preset", "crt,vhs" });
        Assert.Equal(LaunchMode.Preview, a.Mode);
        Assert.Equal((nint)12345, a.ParentHwnd);
        Assert.Equal("matrix", a.Scene);
        Assert.Equal("crt,vhs", a.Preset);
    }

    [Fact]
    public void PreviewWithFrames_Throws()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/p", "1", "--frames", "10" }));
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/p", "1", "--tab", "dimmer" }));
    }

    [Fact]
    public void SettingsShot_ParsesPathAndDefaultsToScenesTab()
    {
        var a = LaunchArgs.Parse(new[] { "--settings-shot", "out.png" });
        Assert.Equal(LaunchMode.SettingsShot, a.Mode);
        Assert.Equal("out.png", a.ShotPath);
        Assert.Equal("scenes", a.SettingsTab);
    }

    [Theory]
    [InlineData("filters")]
    [InlineData("SHUFFLE")]
    [InlineData("dimmer")]
    [InlineData("network")]
    [InlineData("font")]
    [InlineData("about")]
    [InlineData("overlays")]
    [InlineData("screen")]
    public void SettingsShot_TabIsCaseInsensitive(string tab)
    {
        var a = LaunchArgs.Parse(new[] { "--settings-shot", "x.png", "--tab", tab });
        Assert.Equal(tab.ToLowerInvariant(), a.SettingsTab);
    }

    [Fact]
    public void SettingsShot_UnknownTab_Throws()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "--settings-shot", "x.png", "--tab", "bogus" }));
    }

    [Fact]
    public void SettingsShot_WithoutPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "--settings-shot" }));
    }

    [Fact]
    public void TabWithoutSettingsShot_Throws()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/w", "--tab", "scenes" }));
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "--shot", "a.png", "--tab", "scenes" }));
    }

    [Fact]
    public void SceneOrPresetWithSettingsShot_Throws()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "--settings-shot", "x.png", "--scene", "matrix" }));
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "--settings-shot", "x.png", "--preset", "crt" }));
    }

    [Fact]
    public void SettingsShotAndWindowed_AreTwoModes_Throw()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "--settings-shot", "x.png", "/w" }));
    }

    [Fact]
    public void Overlay_ParsesVcr()
    {
        var a = LaunchArgs.Parse(new[] { "--shot", "a.png", "--overlay", "vcr" });
        Assert.Equal("vcr", a.Overlay);
    }

    [Fact]
    public void Overlay_WithoutFlag_IsNull()
    {
        Assert.Null(LaunchArgs.Parse(new[] { "/w" }).Overlay);
        Assert.Null(LaunchArgs.Parse(new[] { "--shot", "a.png" }).Overlay);
    }

    [Fact]
    public void Overlay_WorksWithPreview()
    {
        var a = LaunchArgs.Parse(new[] { "/p", "12345", "--overlay", "hud" });
        Assert.Equal("hud", a.Overlay);
    }

    [Fact]
    public void Overlay_WithoutValue_Throws()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/w", "--overlay" }));
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/w", "--overlay", "--size", "1920x1080" }));
    }

    [Fact]
    public void Overlay_WithScreensaver_Throws()
    {
        Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(new[] { "/s", "--overlay", "hud" }));
    }
}
