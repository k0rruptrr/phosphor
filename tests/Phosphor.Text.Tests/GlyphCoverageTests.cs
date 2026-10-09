using Phosphor.Core;
using Phosphor.Text;

namespace Phosphor.Text.Tests;

public class GlyphCoverageTests
{
    private const int CellH = 18;
    private static readonly GlyphAtlas Atlas = GlyphAtlas.FromBitmap(GlyphRasterizer.Rasterize(CellH));

    [Fact]
    public void SpaceHasZeroCoverage()
    {
        Assert.Equal(0f, Atlas.Coverage(' '));
    }

    [Fact]
    public void FullBlockHasFullCoverage()
    {
        Assert.True(Atlas.Coverage('█') > 0.9f, $"покрытие '█' = {Atlas.Coverage('█')}");
    }

    [Fact]
    public void CoverageIsInUnitRange()
    {
        for (int i = 0; i < Atlas.GlyphCount; i++)
        {
            float v = Atlas.Coverage(GlyphSet.All[i]);
            Assert.InRange(v, 0f, 1f);
        }
    }

    [Fact]
    public void CoverageOfUnknownCharEqualsCoverageOfQuestionMark()
    {
        Assert.Equal(Atlas.Coverage('?'), Atlas.Coverage('漢'));
    }

    [Fact]
    public void CoverageIsMonotonicAlongRamp()
    {
        // Ступени рампы по возрастанию плотности.
        const string ramp = " .:=#@";
        for (int i = 0; i + 1 < ramp.Length; i++)
        {
            float a = Atlas.Coverage(ramp[i]), b = Atlas.Coverage(ramp[i + 1]);
            Assert.True(a < b, $"покрытие '{ramp[i]}' ({a:F3}) не меньше '{ramp[i + 1]}' ({b:F3})");
        }
    }
}
