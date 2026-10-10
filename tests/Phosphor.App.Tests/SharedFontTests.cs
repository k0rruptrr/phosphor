using Phosphor.Core;
using Phosphor.Overlays;
using Phosphor.Platform;

namespace Phosphor.App.Tests;

/// <summary>Кеш шрифтов хоста: ключ (размер, семейство), кламп размера по maxPx. На настоящем GPU (скрытое окно).</summary>
public sealed class SharedFontTests
{
    // Log создаёт файл по PID: второй экземпляр в том же процессе не откроется — один на класс
    private static readonly Lazy<Log> SharedLog = new(() => new Log());

    [Fact]
    public void FontFor_CachesByFamilyAndSize()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var log = SharedLog.Value;
        using var shared = new Shared(win.Gl, PhosphorConfig.Parse(PhosphorConfig.DefaultJson), null!, null!,
            new Dictionary<string, Func<IOverlay>>(), null!, new Random(1), log);

        var vt = shared.FontFor(20, "VT323", 128);
        var grid = shared.FontFor(20, null);
        Assert.NotSame(vt, grid);
        Assert.Same(vt, shared.FontFor(20, "VT323", 128));
        Assert.Same(grid, shared.FontFor(20));
    }

    [Fact]
    public void FontFor_ClampsSizeToMaxPx()
    {
        using var win = ScreensaverWindow.CreateHidden(64, 64);
        var log = SharedLog.Value;
        using var shared = new Shared(win.Gl, PhosphorConfig.Parse(PhosphorConfig.DefaultJson), null!, null!,
            new Dictionary<string, Func<IOverlay>>(), null!, new Random(1), log);

        // Верхний предел — 64 (ограничение GlyphRasterizer), независимо от maxPx
        Assert.Same(shared.FontFor(64, "VT323", 128), shared.FontFor(500, "VT323", 128));
        Assert.Same(shared.FontFor(8, "VT323", 128), shared.FontFor(1, "VT323", 128));
    }
}
