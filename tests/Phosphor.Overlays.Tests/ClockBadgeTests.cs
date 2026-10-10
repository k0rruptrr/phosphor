using System.Text;
using System.Text.Json;
using Phosphor.Core;
using Phosphor.Overlays;

namespace Phosphor.Overlays.Tests;

public class ClockBadgeTests
{
    private static OverlayContext Ctx(DateTime? clock) =>
        new OverlayContext("plasma", "kassette", 0, 1, 59.9f, 0, clock,
            AudioFrame.Silent, new Organism(0f, 0f, 0f, 0f));

    private static ClockBadge Configured(string json)
    {
        var b = new ClockBadge();
        b.Configure(new SceneOptions { Params = JsonDocument.Parse(json).RootElement });
        return b;
    }

    /// <summary>Как хост: Update с часами, затем Draw в сетку Cols×Rows бирки.</summary>
    private static CharGrid Frame(ClockBadge b, DateTime? clock, float dt = 0f)
    {
        b.Update(dt, Ctx(clock));
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
    public void Id_is_clock()
    {
        Assert.Equal("clock", new ClockBadge().Id);
    }

    [Fact]
    public void Default_configure_gives_default_values()
    {
        var b = new ClockBadge();
        b.Configure(new SceneOptions());
        Assert.Equal("br", b.Placement.Anchor);
        Assert.Equal(0.06f, b.Placement.Size, 4);
        Assert.Equal(0.03f, b.Placement.Margin, 4);
        Assert.Equal(0.6f, b.Placement.Opacity, 4);
        Assert.True(b.Placement.Over);
        Assert.True(b.Placement.Drift);
        Assert.Equal(1.5f, b.Placement.Stroke, 4);
        Assert.Equal("", b.Placement.Font);
        Assert.Null(b.ImagePath);
    }

    [Fact]
    public void Hm_pixel_has_three_rows_and_format_width_in_cells()
    {
        var b = new ClockBadge();
        b.Configure(new SceneOptions());
        Assert.Equal(PixelFont.CellsH, b.Rows);
        Assert.Equal(5 * PixelFont.Advance, b.Cols);     // «HH:MM» — 5 символов
    }

    [Fact]
    public void Hms_text_is_eight_cols_one_row()
    {
        var b = Configured("{\"format\":\"hms\",\"style\":\"text\"}");
        Assert.Equal(8, b.Cols);
        Assert.Equal(1, b.Rows);
    }

    [Fact]
    public void Dhm_text_is_eleven_cols_one_row()
    {
        var b = Configured("{\"format\":\"dhm\",\"style\":\"text\"}");
        Assert.Equal(11, b.Cols);
        Assert.Equal(1, b.Rows);
    }

    [Fact]
    public void Hms_text_shows_time_at_colon_phase_on()
    {
        var b = Configured("{\"format\":\"hms\",\"style\":\"text\"}");
        var g = Frame(b, new DateTime(2026, 10, 10, 13, 5, 9));
        Assert.Equal("13:05:09", Row(g, 0));
    }

    [Fact]
    public void Colon_blinks_off_in_second_half_and_back_on()
    {
        var b = Configured("{\"format\":\"hms\",\"style\":\"text\"}");
        var clock = new DateTime(2026, 10, 10, 13, 5, 9);
        Frame(b, clock);                               // t = 0: двоеточие есть
        var g = Frame(b, clock, 0.5f);                 // t = 0.5 с: двоеточия нет
        Assert.Equal("13 05 09", Row(g, 0));
        g = Frame(b, clock, 0.5f);                     // t = 1 с: снова есть
        Assert.Equal("13:05:09", Row(g, 0));
    }

    [Fact]
    public void Blink_off_keeps_colons_visible()
    {
        var b = Configured("{\"format\":\"hms\",\"style\":\"text\",\"blink\":false}");
        var clock = new DateTime(2026, 10, 10, 13, 5, 9);
        Frame(b, clock);
        var g = Frame(b, clock, 0.7f);
        Assert.Equal("13:05:09", Row(g, 0));
    }

    [Fact]
    public void Version_grows_when_seconds_change_in_hms()
    {
        var b = Configured("{\"format\":\"hms\",\"style\":\"text\",\"blink\":false}");
        Frame(b, new DateTime(2026, 10, 10, 13, 5, 9));
        long v1 = b.Version;
        Frame(b, new DateTime(2026, 10, 10, 13, 5, 10));
        Assert.True(b.Version > v1);
    }

    [Fact]
    public void Version_does_not_grow_within_a_minute_in_hm_without_blink()
    {
        var b = Configured("{\"format\":\"hm\",\"style\":\"text\",\"blink\":false}");
        Frame(b, new DateTime(2026, 10, 10, 13, 5, 1));
        long v1 = b.Version;
        Frame(b, new DateTime(2026, 10, 10, 13, 5, 59));
        Assert.Equal(v1, b.Version);
    }

    [Fact]
    public void Null_clock_shows_dashes_for_each_format()
    {
        var hm = Configured("{\"format\":\"hm\",\"style\":\"text\"}");
        Assert.Equal("--:--", Row(Frame(hm, null), 0));

        var hms = Configured("{\"format\":\"hms\",\"style\":\"text\"}");
        Assert.Equal("--:--:--", Row(Frame(hms, null), 0));

        var dhm = Configured("{\"format\":\"dhm\",\"style\":\"text\"}");
        Assert.Equal("--.-- --:--", Row(Frame(dhm, null), 0));
    }

    [Fact]
    public void Dhm_shows_day_and_month_with_dot()
    {
        var b = Configured("{\"format\":\"dhm\",\"style\":\"text\"}");
        var g = Frame(b, new DateTime(2026, 10, 10, 14, 5, 0));
        Assert.Equal("10.10 14:05", Row(g, 0));
    }

    [Fact]
    public void Color_amber_sets_fg_to_ffb000()
    {
        var b = Configured("{\"format\":\"hms\",\"style\":\"text\",\"color\":\"amber\"}");
        var g = Frame(b, new DateTime(2026, 10, 10, 13, 5, 9));
        Assert.Equal(Rgba.Hex(0xFFB000), g[0, 0].Fg);
    }

    [Fact]
    public void Pixel_style_draws_sextants_not_plain_digits()
    {
        var b = Configured("{\"format\":\"hm\",\"style\":\"pixel\"}");
        var g = Frame(b, new DateTime(2026, 10, 10, 13, 5, 0));
        bool sextant = false;
        foreach (var c in g.Cells) if (GlyphSet.IsSextant(c.Ch) && c.Ch != (char)0xE000) sextant = true;
        Assert.True(sextant);
    }

    [Fact]
    public void Position_tl_gives_anchor_tl()
    {
        var b = Configured("{\"position\":\"tl\"}");
        Assert.Equal("tl", b.Placement.Anchor);
    }

    [Fact]
    public void Layer_under_gives_over_false()
    {
        var b = Configured("{\"layer\":\"under\"}");
        Assert.False(b.Placement.Over);
    }

    [Fact]
    public void Font_is_read_outside_params()
    {
        var b = Configured("{\"font\":\"Consolas\"}");
        Assert.Equal("Consolas", b.Placement.Font);
    }

    [Fact]
    public void Params_are_well_formed()
    {
        var specs = new ClockBadge().Params;
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

    [Fact]
    public void TextDump_Clock_ForVisualCheck()
    {
        var sb = new StringBuilder();
        var clock = new DateTime(2026, 10, 10, 13, 5, 9);
        foreach (var style in new[] { "pixel", "text" })
        {
            var b = Configured($"{{\"format\":\"hms\",\"style\":\"{style}\"}}");
            var g = Frame(b, clock);
            sb.Append("== clock hms style=").Append(style).Append(" ==\n");
            for (int y = 0; y < g.Rows; y++) sb.Append(Row(g, y)).Append('\n');
        }
        string path = Path.Combine(Path.GetTempPath(), "phosphor-dump-clock.txt");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        Assert.True(File.Exists(path));
    }
}
