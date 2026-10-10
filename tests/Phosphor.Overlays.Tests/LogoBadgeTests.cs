using System.Text;
using System.Text.Json;
using Phosphor.Core;
using Phosphor.Overlays;

namespace Phosphor.Overlays.Tests;

public class LogoBadgeTests
{
    private static OverlayContext Ctx() =>
        new OverlayContext("plasma", "kassette", 0, 1, 59.9f, 0, null,
            AudioFrame.Silent, new Organism(0f, 0f, 0f, 0f));

    private static LogoBadge Configured(string json)
    {
        var b = new LogoBadge();
        b.Configure(new SceneOptions { Params = JsonDocument.Parse(json).RootElement });
        return b;
    }

    private static CharGrid Frame(LogoBadge b)
    {
        b.Update(0f, Ctx());
        var g = new CharGrid(b.Cols, b.Rows);
        b.Draw(g);
        return g;
    }

    private static string Row(CharGrid g, int y)
    {
        var sb = new StringBuilder(g.Cols);
        for (int x = 0; x < g.Cols; x++) sb.Append(g[x, y].Ch);
        return sb.ToString();
    }

    [Fact]
    public void Id_is_logo()
    {
        Assert.Equal("logo", new LogoBadge().Id);
    }

    [Fact]
    public void Default_is_builtin_phosphor_image()
    {
        var b = new LogoBadge();
        b.Configure(new SceneOptions());
        Assert.NotNull(b.ImagePath);
        Assert.EndsWith(Path.Combine("logo", "greyprism.png"), b.ImagePath!);
        Assert.False(b.FileMissing);
        Assert.Equal("tr", b.Placement.Anchor);
        Assert.False(b.Placement.Over);                 // по умолчанию под эффектами
        Assert.Equal(0.08f, b.Placement.Size, 4);
        Assert.Equal(0.03f, b.Placement.Margin, 4);
        Assert.Equal(0.5f, b.Placement.Opacity, 4);
        Assert.Equal(1.5f, b.Placement.Stroke, 4);
        Assert.True(b.Placement.Drift);
    }

    [Fact]
    public void Source_phosphor_points_to_logo_phosphor_png()
    {
        var b = Configured("{\"source\":\"phosphor\"}");
        Assert.EndsWith(Path.Combine("logo", "phosphor.png"), b.ImagePath!);
    }

    [Fact]
    public void Source_file_missing_falls_back_to_text_phosphor()
    {
        var missing = Path.Combine(Path.GetTempPath(), "phosphor-no-such-logo-b1.png");
        var json = JsonSerializer.Serialize(new { source = "file", file = missing });
        var b = Configured(json);
        Assert.Null(b.ImagePath);
        Assert.True(b.FileMissing);
        Assert.Equal(1, b.Rows);
        Assert.Equal("PHOSPHOR".Length, b.Cols);
        Assert.Equal("PHOSPHOR", Row(Frame(b), 0));
    }

    [Fact]
    public void Source_file_existing_uses_image_path()
    {
        var file = Path.GetTempFileName();
        try
        {
            var json = JsonSerializer.Serialize(new { source = "file", file });
            var b = Configured(json);
            Assert.Equal(file, b.ImagePath);
            Assert.False(b.FileMissing);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Source_text_is_text_mode_with_given_text()
    {
        var b = Configured("{\"source\":\"text\",\"text\":\"КАНАЛ 7\"}");
        Assert.Null(b.ImagePath);
        Assert.False(b.FileMissing);
        Assert.Equal(7, b.Cols);
        Assert.Equal(1, b.Rows);
        Assert.Equal("КАНАЛ 7", Row(Frame(b), 0));
    }

    [Fact]
    public void Empty_text_becomes_phosphor()
    {
        var b = Configured("{\"source\":\"text\",\"text\":\"\"}");
        Assert.Equal("PHOSPHOR", Row(Frame(b), 0));
    }

    [Fact]
    public void Text_is_cut_to_forty_chars()
    {
        var b = Configured("{\"source\":\"text\",\"text\":\"" + new string('A', 50) + "\"}");
        Assert.Equal(40, b.Cols);
    }

    [Fact]
    public void Glyph_outside_glyphset_becomes_question_mark()
    {
        // U+2030 (знак промилле) нет в GlyphSet
        Assert.False(GlyphSet.Contains('‰'));
        var b = Configured("{\"source\":\"text\",\"text\":\"A‰B\"}");
        Assert.Equal(3, b.Cols);
        Assert.Equal("A?B", Row(Frame(b), 0));
    }

    [Fact]
    public void Version_is_constant()
    {
        var b = Configured("{\"source\":\"text\",\"text\":\"ABC\"}");
        long v0 = b.Version;
        Frame(b);
        b.Update(5f, Ctx());
        Assert.Equal(v0, b.Version);
    }

    [Fact]
    public void Color_amber_sets_text_fg()
    {
        var b = Configured("{\"source\":\"text\",\"text\":\"AB\",\"color\":\"amber\"}");
        var g = Frame(b);
        Assert.Equal(Rgba.Hex(0xFFB000), g[0, 0].Fg);
    }

    [Fact]
    public void Layer_over_gives_over_true()
    {
        var b = Configured("{\"layer\":\"over\"}");
        Assert.True(b.Placement.Over);
    }

    [Fact]
    public void Params_are_well_formed()
    {
        var specs = new LogoBadge().Params;
        Assert.NotEmpty(specs);
        foreach (var s in specs)
        {
            Assert.InRange(s.Default, s.Min, s.Max);
            if (s.Kind == SceneParamKind.Choice)
            {
                Assert.NotNull(s.Options);
                Assert.Equal((int)s.Max + 1, s.Options!.Length);
            }
            if (s.Kind == SceneParamKind.Bool)
            {
                Assert.Equal(0f, s.Min);
                Assert.Equal(1f, s.Max);
            }
        }
    }
}
