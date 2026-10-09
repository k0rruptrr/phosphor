using Phosphor.Core;
using Phosphor.Text;
using StbImageWriteSharp;

namespace Phosphor.Text.Tests;

public class GlyphRasterizerTests
{
    private const int CellH = 18;
    private static readonly AtlasBitmap Atlas = GlyphRasterizer.Rasterize(CellH);

    private static int Index(char c) => Atlas.CharToIndex[c];

    private static byte Px(AtlasBitmap a, int glyph, int x, int y)
    {
        int ox = (glyph % a.AtlasCols) * a.CellWidth;
        int oy = (glyph / a.AtlasCols) * a.CellHeight;
        return a.Pixels[(oy + y) * a.Width + ox + x];
    }

    private static byte[] CellPixels(AtlasBitmap a, char c)
    {
        int g = a.CharToIndex[c];
        var result = new byte[a.CellWidth * a.CellHeight];
        for (int y = 0; y < a.CellHeight; y++)
            for (int x = 0; x < a.CellWidth; x++)
                result[y * a.CellWidth + x] = Px(a, g, x, y);
        return result;
    }

    private static byte CellAt(byte[] cell, int x, int y) => cell[y * Atlas.CellWidth + x];

    [Fact]
    public void AllGlyphSetCharsExceptMissingAreMappedOutsideQuestionMark()
    {
        int q = Index('?');
        foreach (var c in GlyphSet.All)
        {
            if (c == '?' || Atlas.Missing.Contains(c)) continue;
            Assert.NotEqual(q, Index(c));
        }
    }

    [Fact]
    public void MissingDoesNotContainAsciiCyrillicKatakanaOrBox()
    {
        foreach (var c in GlyphSet.Ascii + GlyphSet.Cyrillic + GlyphSet.Katakana + GlyphSet.Box)
            Assert.DoesNotContain(c, Atlas.Missing);
    }

    [Fact]
    public void CellWidthIsInConsolasRange()
    {
        Assert.InRange(Atlas.CellWidth, 9, 11);
        Assert.Equal(CellH, Atlas.CellHeight);
    }

    [Fact]
    public void SpaceIsEmptyAndFullBlockIsSolid()
    {
        var space = CellPixels(Atlas, ' ');
        Assert.All(space, b => Assert.Equal(0, b));

        var full = CellPixels(Atlas, '█');
        Assert.All(full, b => Assert.Equal(255, b));
    }

    [Fact]
    public void UnknownCharMapsToQuestionMarkIndex()
    {
        Assert.Equal(Index('?'), Atlas.CharToIndex['漢']);
    }

    [Theory]
    [InlineData('─')]
    [InlineData('═')]
    public void HorizontalLinesAreSeamlessLeftToRight(char c)
    {
        var cell = CellPixels(Atlas, c);
        int w = Atlas.CellWidth, h = Atlas.CellHeight;
        for (int y = 0; y < h; y++)
            Assert.Equal(CellAt(cell, 0, y), CellAt(cell, w - 1, y));
    }

    [Theory]
    [InlineData('│')]
    [InlineData('║')]
    public void VerticalLinesAreSeamlessTopToBottom(char c)
    {
        var cell = CellPixels(Atlas, c);
        int w = Atlas.CellWidth, h = Atlas.CellHeight;
        for (int x = 0; x < w; x++)
            Assert.Equal(CellAt(cell, x, 0), CellAt(cell, x, h - 1));
    }

    [Theory]
    [InlineData('┼')]
    [InlineData('╬')]
    public void CrossesTouchAllFourEdges(char c)
    {
        var cell = CellPixels(Atlas, c);
        int w = Atlas.CellWidth, h = Atlas.CellHeight;
        bool top = false, bottom = false, left = false, right = false;
        for (int x = 0; x < w; x++)
        {
            top |= CellAt(cell, x, 0) > 0;
            bottom |= CellAt(cell, x, h - 1) > 0;
        }
        for (int y = 0; y < h; y++)
        {
            left |= CellAt(cell, 0, y) > 0;
            right |= CellAt(cell, w - 1, y) > 0;
        }
        Assert.True(top && bottom && left && right);
    }

    [Fact]
    public void DoubleHorizontalLineHasTwoStripesOfThicknessT()
    {
        var cell = CellPixels(Atlas, '═');
        int w = Atlas.CellWidth, h = Atlas.CellHeight;
        int t = Math.Max(1, (int)Math.Round(h / 14.0));
        int cx = w / 2;

        var runs = new List<int>();
        int run = 0;
        for (int y = 0; y < h; y++)
        {
            if (CellAt(cell, cx, y) > 0) run++;
            else if (run > 0) { runs.Add(run); run = 0; }
        }
        if (run > 0) runs.Add(run);

        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.Equal(t, r));
    }

    [Fact]
    public void DoubleCrossCenterIsEmpty()
    {
        var cell = CellPixels(Atlas, '╬');
        int cx = Atlas.CellWidth / 2, cy = Atlas.CellHeight / 2;
        Assert.Equal(0, CellAt(cell, cx, cy));
    }

    [Theory]
    [InlineData('░', 0.25)]
    [InlineData('▒', 0.50)]
    [InlineData('▓', 0.75)]
    public void ShadingCoverageIsCloseToNominal(char c, double expected)
    {
        var cell = CellPixels(Atlas, c);
        double covered = cell.Count(b => b > 0) / (double)cell.Length;
        Assert.InRange(covered, expected - 0.08, expected + 0.08);
    }

    [Fact]
    public void SaveAtlasPng()
    {
        string path = Path.Combine(Path.GetTempPath(), "phosphor-atlas.png");
        using (var fs = File.Create(path))
        {
            new ImageWriter().WritePng(Atlas.Pixels, Atlas.Width, Atlas.Height, ColorComponents.Grey, fs);
        }
        Assert.True(new FileInfo(path).Length > 0);
    }
}
