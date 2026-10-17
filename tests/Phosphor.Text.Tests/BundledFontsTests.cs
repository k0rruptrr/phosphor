using Phosphor.Text;

namespace Phosphor.Text.Tests;

/// <summary>Встроенные шрифты Fixedsys Excelsior (ttf) и Bedstead (otf, CFF): растеризуются, кириллица без пропусков.</summary>
public class BundledFontsTests
{
    private const int CellH = 18;
    private static readonly string Text = new string(Enumerable.Range(0x20, 0x5F).Select(i => (char)i).ToArray()) + " АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ абвгдеёжзийклмнопрстуфхцчшщъыьэюя";

    private static string Builtin(string file) => Path.Combine(AppContext.BaseDirectory, "fonts", file);

    public static IEnumerable<object[]> Fonts() => new[]
    {
        new object[] { "FSEX302.ttf", "Fixedsys Excelsior" },
        new object[] { "bedstead.otf", "Bedstead" },
    };

    [Theory]
    [MemberData(nameof(Fonts))]
    public void BundledFont_IsMonospaceAndRasterizes(string file, string family)
    {
        string path = Builtin(file);
        Assert.True(File.Exists(path), path);
        Assert.True(FontCatalog.IsMonospace(path));

        var atlas = GlyphRasterizer.Rasterize(CellH, path);
        Assert.True(atlas.CellWidth > 0);
        Assert.Equal(CellH, atlas.CellHeight);

        // Текст без пропусков: ни один символ не в Missing (фолбэк на Consolas/системные не считается)
        foreach (char c in Text)
            Assert.DoesNotContain(c, atlas.Missing);

        // Глифы реально нарисованы (CFF/TrueType контуры), а не пустая клетка
        Assert.True(InkCount(atlas, 'A') > 0, $"{family}: 'A' пустой");
        Assert.True(InkCount(atlas, 'Ж') > 0, $"{family}: 'Ж' пустой");
    }

    [Fact]
    public void FontCatalog_ListsBundledFamilies()
    {
        var families = FontCatalog.List().Select(e => e.Family).ToList();
        Assert.Contains("Fixedsys Excelsior", families);
        Assert.Contains("Bedstead", families);
    }

    private static int InkCount(AtlasBitmap a, char c)
    {
        int g = a.CharToIndex[c];
        int ox = (g % a.AtlasCols) * a.CellWidth, oy = (g / a.AtlasCols) * a.CellHeight;
        int n = 0;
        for (int y = 0; y < a.CellHeight; y++)
            for (int x = 0; x < a.CellWidth; x++)
                if (a.Pixels[(oy + y) * a.Width + ox + x] > 0) n++;
        return n;
    }
}
