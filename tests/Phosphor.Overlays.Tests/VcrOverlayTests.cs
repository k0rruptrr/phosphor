using System.Text;
using Phosphor.Core;
using Phosphor.Overlays;

namespace Phosphor.Overlays.Tests;

public class VcrOverlayTests
{
    private static readonly Rgba Scene = Rgba.Hex(0x333333);
    private static readonly Rgba Bg = Rgba.Hex(0x101010);
    private static readonly Rgba Plate = new(8, 8, 8);

    private static OverlayContext Ctx(DateTime? clock = null, float fatigue = 0f) =>
        new OverlayContext("plasma", "kassette", 0, 1, 59.9f, 0, clock,
            AudioFrame.Silent, new Organism(0f, 0f, fatigue, 0f));

    /// <summary>Сетка, заполненная «сценой»: символ '#', серый Fg, тёмный Bg.</summary>
    private static CharGrid Filled(int cols, int rows)
    {
        var g = new CharGrid(cols, rows);
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
                g.Put(x, y, '#', Scene, Bg);
        return g;
    }

    private static VcrOverlay Make(int cols, int rows)
    {
        var v = new VcrOverlay();
        v.Init(cols, rows, new Random(1));
        return v;
    }

    private static string Row(CharGrid g, int y)
    {
        var sb = new StringBuilder(g.Cols);
        for (int x = 0; x < g.Cols; x++) sb.Append(g[x, y].Ch);
        return sb.ToString();
    }

    private static int CountChar(CharGrid g, int y, char c)
    {
        int n = 0;
        for (int x = 0; x < g.Cols; x++) if (g[x, y].Ch == c) n++;
        return n;
    }

    private static bool IsSextant(char c) => GlyphSet.IsSextant(c);

    // ---- пиксельный шрифт ----

    [Theory]
    [InlineData("PLAYREWSPAUTOTRACKING")]
    [InlineData("0123456789:[]=-►◄")]
    public void Every_used_glyph_is_a_3x3_cell_block(string chars)
    {
        foreach (char c in chars)
        {
            Assert.True(PixelFont.Has(c), $"нет глифа '{c}'");
            Assert.Equal(PixelFont.CellsW * PixelFont.CellsH, PixelFont.Bits(c).Length);
            Assert.Contains(PixelFont.Bits(c), b => b != 0);
        }
    }

    [Fact]
    public void Eight_and_zero_differ()
    {
        Assert.NotEqual(PixelFont.Bits('0'), PixelFont.Bits('8'));
    }

    [Fact]
    public void Glyph_block_stays_inside_3x3_sextants()
    {
        foreach (char c in "PLAY0123456789")
            foreach (int b in PixelFont.Bits(c))
                Assert.True(b >= 0 && b <= 63);
    }

    // ---- мелкий режим (узкая сетка) ----

    [Fact]
    public void Id_is_vcr()
    {
        Assert.Equal("vcr", new VcrOverlay().Id);
    }

    [Fact]
    public void Small_clock_shows_hh_mm_in_top_right()
    {
        var g = Filled(40, 40);
        var v = Make(40, 40);
        v.Update(0.2f);                          // фаза «двоеточие вкл»
        v.Apply(g, Ctx(new DateTime(2026, 10, 10, 14, 5, 0)));
        Assert.Contains("SP 14:05", Row(g, 2));
    }

    [Fact]
    public void Small_clock_colon_blinks()
    {
        var g = Filled(40, 40);
        var v = Make(40, 40);
        v.Update(0.7f);                          // фаза «двоеточие выкл»
        v.Apply(g, Ctx(new DateTime(2026, 10, 10, 14, 5, 0)));
        Assert.Contains("SP 14 05", Row(g, 2));
    }

    [Fact]
    public void Small_null_clock_shows_dashes()
    {
        var g = Filled(40, 40);
        var v = Make(40, 40);
        v.Update(0.2f);
        v.Apply(g, Ctx(null));
        Assert.Contains("SP --:--", Row(g, 2));
    }

    [Fact]
    public void Small_tape_bar_is_half_full_after_90_minutes()
    {
        const int cols = 40, rows = 40;
        var g = Filled(cols, rows);
        var v = Make(cols, rows);
        v.Update(90 * 60f);
        v.Apply(g, Ctx());
        int bar = rows - 3;
        int filled = CountChar(g, bar, '=');
        Assert.InRange(filled, 9, 11);
        Assert.Contains("S[", Row(g, bar));
        Assert.Contains("1:30:00", Row(g, bar));  // 90 мин = 1:30:00 ленты
    }

    [Fact]
    public void Small_tape_end_shows_rew_then_bar_is_nearly_empty()
    {
        const int cols = 40, rows = 40;
        var g = Filled(cols, rows);
        var v = Make(cols, rows);
        v.Update(180 * 60f + 0.1f);              // конец ленты: REW сразу
        v.Apply(g, Ctx());
        Assert.Contains("REW", Row(g, 2));

        v.Update(4.1f);                          // REW отыграл, ленту прошли заново
        g = Filled(cols, rows);
        v.Apply(g, Ctx());
        Assert.Contains("PLAY", Row(g, 2));
        Assert.InRange(CountChar(g, rows - 3, '='), 0, 1);
    }

    [Fact]
    public void Small_auto_tracking_appears_only_with_high_fatigue()
    {
        const int cols = 40, rows = 40;
        var v = Make(cols, rows);
        v.Update(0.2f);                          // фаза «вкл»

        var hi = Filled(cols, rows);
        v.Apply(hi, Ctx(fatigue: 0.7f));
        Assert.Contains("AUTO TRACKING", Row(hi, 2));

        var lo = Filled(cols, rows);
        v.Apply(lo, Ctx(fatigue: 0.3f));
        Assert.DoesNotContain("AUTO TRACKING", Row(lo, 2));
    }

    [Fact]
    public void Narrow_grid_does_not_throw_or_write_outside()
    {
        const int cols = 30, rows = 10;
        var g = Filled(cols, rows);
        var v = Make(cols, rows);
        v.Update(5f);
        var ex = Record.Exception(() => v.Apply(g, Ctx(new DateTime(2026, 1, 1, 9, 9, 0), fatigue: 0.9f)));
        Assert.Null(ex);
        Assert.Equal(cols * rows, g.Cells.Length);
    }

    // ---- крупный режим (120×40) ----

    [Fact]
    public void Big_changes_only_its_own_cells()
    {
        const int cols = 120, rows = 40;
        var g = Filled(cols, rows);
        var v = Make(cols, rows);
        v.Update(0.1f);
        v.Apply(g, Ctx(new DateTime(2026, 10, 10, 14, 5, 0), fatigue: 0.7f));

        // Текст: верхняя строка (y 2..4) и строка счётчика (y rows-5..rows-3).
        // Подложка: поле в 1 клетку вокруг текста.
        int top = 2, bar = rows - 5;
        bool InText(int y) => (y >= top && y < top + 3) || (y >= bar && y < bar + 3);
        bool InPlate(int y) => (y >= top - 1 && y <= top + 3) || (y >= bar - 1 && y <= bar + 3);
        int textCells = 0, plateCells = 0;
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
            {
                var cell = g[x, y];
                if (cell.Ch != '#')
                {
                    textCells++;
                    Assert.True(InText(y), $"текст задел строку {y} в ({x},{y})");
                }
                if (cell.Bg != Bg)
                {
                    plateCells++;
                    Assert.Equal(Plate, cell.Bg);
                    Assert.True(InPlate(y), $"подложка вне надписи: ({x},{y})");
                }
            }
        Assert.True(textCells > 0);
        Assert.True(plateCells > 0);
    }

    [Fact]
    public void Big_top_left_is_sextant_text_on_dark_plate()
    {
        const int cols = 120, rows = 40;
        var g = Filled(cols, rows);
        var v = Make(cols, rows);
        v.Update(0.1f);                          // PLAY видим
        v.Apply(g, Ctx());
        Assert.True(IsSextant(g[2, 2].Ch));      // первая клетка буквы P
        Assert.Equal(Plate, g[1, 1].Bg);         // поле вокруг надписи
        Assert.Equal(Bg, g[cols - 3, rows - 2].Bg); // далеко от OSD — фон сцены
    }

    [Fact]
    public void Big_tape_bar_is_half_full_after_90_minutes()
    {
        const int cols = 120, rows = 40;
        var g = Filled(cols, rows);
        var v = Make(cols, rows);
        v.Update(90 * 60f);
        v.Apply(g, Ctx());
        int barRow = rows - 5;                    // блоки — верхние 2 строки шкалы
        int blocks = CountChar(g, barRow, '█');
        Assert.InRange(blocks, 9, 11);
        Assert.Equal(blocks, CountChar(g, barRow + 1, '█'));
    }

    [Fact]
    public void Big_auto_tracking_is_sextant_text_when_fatigued()
    {
        const int cols = 120, rows = 40;
        var v = Make(cols, rows);
        v.Update(0.2f);
        var g = Filled(cols, rows);
        v.Apply(g, Ctx(fatigue: 0.7f));
        // Между PLAY (x 2..25) и часами (справа) — текст есть и не слипается с ними: между ними стоит поле
        Assert.Contains(Enumerable.Range(28, 50), x => IsSextant(g[x, 2].Ch));
        Assert.Equal(Plate, g[29, 1].Bg);         // поле слева от AUTO TRACKING (текст с x=30)
        Assert.Equal(Bg, g[84, 0].Bg);            // над строкой OSD — фон сцены
    }

    [Fact]
    public void Big_auto_tracking_is_skipped_when_no_room()
    {
        const int cols = 60, rows = 40;
        var v = Make(cols, rows);
        v.Update(0.2f);
        var withFatigue = Filled(cols, rows);
        v.Apply(withFatigue, Ctx(clock: null, fatigue: 0.7f));
        var without = Filled(cols, rows);
        v.Apply(without, Ctx(clock: null, fatigue: 0f));
        // 60 колонок: между PLAY и часами места под AUTO TRACKING нет — строка такая же, как без усталости
        for (int x = 0; x < cols; x++)
            Assert.Equal(without[x, 2].Ch, withFatigue[x, 2].Ch);
    }
}
