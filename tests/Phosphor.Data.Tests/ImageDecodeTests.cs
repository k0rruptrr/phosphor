namespace Phosphor.Data.Tests;

public sealed class ImageDecodeTests
{
    private static RgbImage Solid(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 3];
        for (int i = 0; i < px.Length; i += 3) { px[i] = r; px[i + 1] = g; px[i + 2] = b; }
        return new RgbImage(w, h, px);
    }

    private static (byte R, byte G, byte B) At(RgbImage img, int x, int y)
    {
        int i = (y * img.Width + x) * 3;
        return (img.Rgb[i], img.Rgb[i + 1], img.Rgb[i + 2]);
    }

    [Fact]
    public void Stitch_PlacesTopRightTileAtTopRight()
    {
        // grid[row][col]: 0_0 красный, 1_0 (верх-право) зелёный, 0_1 (низ-лево) синий, 1_1 белый
        var red = Solid(2, 2, 255, 0, 0);
        var green = Solid(2, 2, 0, 255, 0);
        var blue = Solid(2, 2, 0, 0, 255);
        var white = Solid(2, 2, 255, 255, 255);
        var img = ImageDecode.Stitch(new[] { new[] { red, green }, new[] { blue, white } });

        Assert.Equal(4, img.Width);
        Assert.Equal(4, img.Height);
        Assert.Equal((255, 0, 0), At(img, 0, 0));
        Assert.Equal((0, 255, 0), At(img, 3, 0));   // угол 1_0 справа сверху
        Assert.Equal((0, 0, 255), At(img, 0, 3));
        Assert.Equal((255, 255, 255), At(img, 3, 3));
    }

    [Fact]
    public void Stitch_RejectsMismatchedTiles()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ImageDecode.Stitch(new[] { new[] { Solid(2, 2, 0, 0, 0), Solid(3, 2, 0, 0, 0) } }));
    }

    [Fact]
    public void Downscale_AveragesBlocks()
    {
        // 4×1: 0, 0, 255, 255 → 2×1: среднее каждой половины
        var src = new RgbImage(4, 1, new byte[] { 0, 0, 0, 0, 0, 0, 255, 255, 255, 255, 255, 255 });
        var dst = ImageDecode.Downscale(src, 2, 1);

        Assert.Equal(2, dst.Width);
        Assert.Equal(1, dst.Height);
        Assert.Equal((0, 0, 0), At(dst, 0, 0));
        Assert.Equal((255, 255, 255), At(dst, 1, 0));
    }

    [Fact]
    public void Downscale_MixedValues_AveragesNotNearest()
    {
        // 2×1: 0 и 255 → 1×1: среднее ~128 (nearest дал бы 0 или 255)
        var src = new RgbImage(2, 1, new byte[] { 0, 0, 0, 255, 255, 255 });
        var dst = ImageDecode.Downscale(src, 1, 1);
        Assert.InRange(dst.Rgb[0], (byte)127, (byte)128);
    }

    [Fact]
    public void Downscale_NoUpscale_ReturnsSource()
    {
        var src = Solid(4, 4, 9, 9, 9);
        Assert.Same(src, ImageDecode.Downscale(src, 8, 8));
    }

    [Fact]
    public void Decode_RealHimawariTile_IncludingOneBitGrayscale()
    {
        // 1_0 — 1-bit grayscale PNG (почти чёрный угол диска)
        var tile = ImageDecode.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sat-him-1_0.png")));
        Assert.Equal(550, tile.Width);
        Assert.Equal(550, tile.Height);
        Assert.Equal(550 * 550 * 3, tile.Rgb.Length);
    }

    [Fact]
    public void Decode_RealHimawariComposite_Is1100AndDownscalesTo480()
    {
        var grid = new RgbImage[2][];
        foreach (var (x, y) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
        {
            grid[y] ??= new RgbImage[2];
            grid[y][x] = ImageDecode.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"sat-him-{x}_{y}.png")));
        }
        var disk = ImageDecode.Stitch(grid);
        Assert.Equal(1100, disk.Width);
        Assert.Equal(1100, disk.Height);

        var small = ImageDecode.Downscale(disk, 480, 480);
        Assert.Equal(480, small.Width);
        Assert.Equal(480, small.Height);
    }

    [Fact]
    public void Decode_RealMeteosatFrame_KeepsSize()
    {
        var img = ImageDecode.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sat-meteosat-frame.png")));
        Assert.Equal(480, img.Width);
        Assert.Equal(270, img.Height);
    }

    [Fact]
    public void Decode_RealViirsJpeg_IsRgb()
    {
        var img = ImageDecode.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sat-viirs.jpg")));
        Assert.Equal(480, img.Width);
        Assert.Equal(270, img.Height);
        Assert.Equal(480 * 270 * 3, img.Rgb.Length);
    }

    [Fact]
    public void DarkFraction_CountsNearBlackPixels()
    {
        var black = Solid(4, 4, 0, 0, 0);
        var half = new RgbImage(2, 1, new byte[] { 0, 0, 0, 200, 200, 200 });
        Assert.Equal(1.0, ImageDecode.DarkFraction(black, 12));
        Assert.Equal(0.5, ImageDecode.DarkFraction(half, 12));
    }
}
