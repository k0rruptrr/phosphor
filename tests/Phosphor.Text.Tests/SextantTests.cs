using Phosphor.Core;
using Phosphor.Text;
using StbImageWriteSharp;

namespace Phosphor.Text.Tests;

public class SextantTests
{
    private const int CellH = 18;
    private static readonly AtlasBitmap Atlas = GlyphRasterizer.Rasterize(CellH);

    private static byte[] CellPixels(char c)
    {
        int g = Atlas.CharToIndex[c];
        int ox = (g % Atlas.AtlasCols) * Atlas.CellWidth;
        int oy = (g / Atlas.AtlasCols) * Atlas.CellHeight;
        int w = Atlas.CellWidth, h = Atlas.CellHeight;
        var result = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                result[y * w + x] = Atlas.Pixels[(oy + y) * Atlas.Width + ox + x];
        return result;
    }

    // Область бита в пикселях ячейки: [x1, x2) × [y1, y2).
    private static (int X1, int X2, int Y1, int Y2) Region(int bit)
    {
        int w = Atlas.CellWidth, h = Atlas.CellHeight;
        int cm = w / 2;
        int r1 = (int)Math.Round(h / 3.0, MidpointRounding.AwayFromZero);
        int r2 = (int)Math.Round(2 * h / 3.0, MidpointRounding.AwayFromZero);
        int[] ys = { 0, r1, r2, h };
        int[] xs = { 0, cm, w };
        int row = bit / 2, col = bit % 2;
        return (xs[col], xs[col + 1], ys[row], ys[row + 1]);
    }

    private static (int X, int Y) Center(int bit)
    {
        var (x1, x2, y1, y2) = Region(bit);
        return ((x1 + x2 - 1) / 2, (y1 + y2 - 1) / 2);
    }

    [Fact]
    public void SextantsAreNotMissing()
    {
        foreach (var c in GlyphSet.Sextants)
            Assert.DoesNotContain(c, Atlas.Missing);
    }

    [Fact]
    public void EmptySextantIsAllZero()
    {
        var cell = CellPixels(GlyphSet.Sextant(0));
        Assert.All(cell, b => Assert.Equal(0, b));
    }

    [Fact]
    public void FullSextantIsAll255()
    {
        var cell = CellPixels(GlyphSet.Sextant(63));
        Assert.All(cell, b => Assert.Equal(255, b));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void SingleBitPaintsExactlyItsOwnRegion(int bit)
    {
        var cell = CellPixels(GlyphSet.Sextant(1 << bit));
        int w = Atlas.CellWidth, h = Atlas.CellHeight;
        var (x1, x2, y1, y2) = Region(bit);

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool inside = x >= x1 && x < x2 && y >= y1 && y < y2;
                Assert.Equal(inside ? 255 : 0, cell[y * w + x]);
            }

        for (int other = 0; other < 6; other++)
        {
            var (cx, cy) = Center(other);
            byte expected = other == bit ? (byte)255 : (byte)0;
            Assert.Equal(expected, cell[cy * w + cx]);
        }
    }

    [Fact]
    public void FullSextantTopAndBottomRowsAreSolid()
    {
        // Две ячейки друг над другом стыкуются без щели: верх и низ полностью закрашены.
        var cell = CellPixels(GlyphSet.Sextant(63));
        int w = Atlas.CellWidth, h = Atlas.CellHeight;
        for (int x = 0; x < w; x++)
        {
            Assert.Equal(255, cell[x]);
            Assert.Equal(255, cell[(h - 1) * w + x]);
        }
    }

    [Fact]
    public void Sextant8x8SheetIsSavedAndAllDistinct()
    {
        const int Cols = 8, Rows = 8;
        int cw = Atlas.CellWidth, ch = Atlas.CellHeight;
        int W = Cols * cw, H = Rows * ch;
        var canvas = new byte[W * H];
        var seen = new HashSet<string>();

        for (int i = 0; i < 64; i++)
        {
            var cell = CellPixels(GlyphSet.Sextant(i));
            seen.Add(Convert.ToBase64String(cell));
            int dx = (i % Cols) * cw, dy = (i / Cols) * ch;
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                    canvas[(dy + y) * W + dx + x] = cell[y * cw + x];
        }

        Assert.Equal(64, seen.Count);

        string path = Path.Combine(Path.GetTempPath(), "phosphor-sextants.png");
        using (var fs = File.Create(path))
        {
            new ImageWriter().WritePng(canvas, W, H, ColorComponents.Grey, fs);
        }
        Assert.True(new FileInfo(path).Length > 0);
    }
}
