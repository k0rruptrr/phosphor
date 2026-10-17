using Phosphor.Platform;
using Xunit;

namespace Phosphor.Platform.Tests;

public class KeyClassifyTests
{
    // Коды VK — литералами: Win32 внутренний класс
    private const nint VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
    private const nint VK_ESCAPE = 0x1B, VK_VOLUME_UP = 0xAF;
    private const nint VK_A = 0x41;

    private static KeyAction Classify(bool fs, bool win, nint vk, bool ignoreMedia, out UserCommand cmd) =>
        ScreensaverWindow.Classify(fs, win, vk, ignoreMedia, out cmd);

    [Theory]
    [InlineData(0x27, UserCommand.NextScene)] // VK_RIGHT
    [InlineData(0x25, UserCommand.PrevScene)] // VK_LEFT
    [InlineData(0x26, UserCommand.NextPreset)] // VK_UP
    [InlineData(0x28, UserCommand.PrevPreset)] // VK_DOWN
    public void Arrows_AreCommands_InFullscreenAndWindowed(int vkValue, UserCommand expected)
    {
        nint vk = vkValue;
        Assert.Equal(KeyAction.Command, Classify(true, false, vk, true, out var fs));
        Assert.Equal(expected, fs);
        Assert.Equal(KeyAction.Command, Classify(false, true, vk, true, out var w));
        Assert.Equal(expected, w);
    }

    [Fact]
    public void VolumeUp_IgnoredInFullscreen_WhenIgnoreOn_PassThrough()
    {
        Assert.Equal(KeyAction.PassThrough, Classify(true, false, VK_VOLUME_UP, true, out _));
    }

    [Fact]
    public void VolumeUp_InFullscreen_WhenIgnoreOff_Closes()
    {
        Assert.Equal(KeyAction.Close, Classify(true, false, VK_VOLUME_UP, false, out _));
    }

    [Fact]
    public void MediaKeys_Range_0xAD_to_0xB3_AreMediaKeys()
    {
        for (nint vk = 0xAD; vk <= 0xB3; vk++)
            Assert.Equal(KeyAction.PassThrough, Classify(true, false, vk, true, out _));
        Assert.Equal(KeyAction.Close, Classify(true, false, 0xAC, false, out _));
        Assert.Equal(KeyAction.Close, Classify(true, false, 0xB4, false, out _));
    }

    [Fact]
    public void LetterKey_Fullscreen_Closes()
    {
        Assert.Equal(KeyAction.Close, Classify(true, false, VK_A, true, out _));
    }

    [Fact]
    public void LetterKey_Windowed_PassThrough()
    {
        Assert.Equal(KeyAction.PassThrough, Classify(false, true, VK_A, true, out _));
    }

    [Fact]
    public void Escape_Windowed_Closes()
    {
        Assert.Equal(KeyAction.Close, Classify(false, true, VK_ESCAPE, true, out _));
    }

    [Fact]
    public void Preview_AndHidden_PassEverythingThrough()
    {
        Assert.Equal(KeyAction.PassThrough, Classify(false, false, VK_A, false, out _));
        Assert.Equal(KeyAction.PassThrough, Classify(false, false, VK_ESCAPE, false, out _));
        Assert.Equal(KeyAction.PassThrough, Classify(false, false, VK_RIGHT, false, out _));
    }
}
