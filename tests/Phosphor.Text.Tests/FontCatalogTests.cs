using Phosphor.Text;

namespace Phosphor.Text.Tests;

public class FontCatalogTests
{
    private static string BuiltinVt323 => Path.Combine(AppContext.BaseDirectory, "fonts", "VT323-Regular.ttf");

    private static string WindowsFont(string file) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", file);

    [Fact]
    public void Enumerate_FindsBuiltinFromFolderCopy()
    {
        string dir = Path.Combine(Path.GetTempPath(), "phos-fonts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(BuiltinVt323, Path.Combine(dir, "VT323-Regular.ttf"));
            var list = FontCatalog.Enumerate(new[] { (dir, true) });
            var entry = Assert.Single(list);
            Assert.Equal("VT323", entry.Family);
            Assert.True(entry.Builtin);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Enumerate_MissingFolder_IsEmpty()
    {
        string dir = Path.Combine(Path.GetTempPath(), "phos-none-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(FontCatalog.Enumerate(new[] { (dir, true) }));
    }

    [Fact]
    public void IsMonospace_Consolas_Yes_Arial_No()
    {
        Assert.True(FontCatalog.IsMonospace(WindowsFont("consola.ttf")));
        Assert.False(FontCatalog.IsMonospace(WindowsFont("arial.ttf")));
    }

    [Fact]
    public void Find_ByFamilyName_IsCaseInsensitive()
    {
        var entry = FontCatalog.Find("vt323");
        Assert.NotNull(entry);
        Assert.Equal("VT323", entry!.Family);
    }

    [Fact]
    public void Find_ByPath_ReturnsFamilyOfFile()
    {
        var entry = FontCatalog.Find(BuiltinVt323);
        Assert.NotNull(entry);
        Assert.Equal("VT323", entry!.Family);
    }

    [Fact]
    public void Find_UnknownName_IsNull()
    {
        Assert.Null(FontCatalog.Find("НетТакогоШрифта-XYZ"));
        Assert.Null(FontCatalog.Find(@"C:\нет\такого\шрифта.ttf"));
    }
}
