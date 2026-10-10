using System.Text;
using System.Text.Json;
using Phosphor.Core;
using Phosphor.Overlays;

namespace Phosphor.Overlays.Tests;

public class VcrOverlayTests
{
    private static readonly Rgba Scene = Rgba.Hex(0x333333);
    private static readonly Rgba Bg = Rgba.Hex(0x101010);
    private static readonly Rgba PlateColor = new(8, 8, 8);     // старая тёмная подложка — больше не рисуется

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

    /// <summary>Видик включён (как сцена с vhs): показ запускается с нуля.</summary>
    private static VcrOverlay Make(int cols, int rows)
    {
        var v = new VcrOverlay();
        v.Init(cols, rows, new Random(1));
        v.SetOn(true);
        return v;
    }

    /// <summary>Повторное включение — новый показ PLAY/часов/счётчика (как смена сцены на vhs).</summary>
    private static void ShowNow(VcrOverlay v)
    {
        v.SetOn(false);
        v.SetOn(true);
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

    // ---- показ по времени (как у видика) ----

    [Fact]
    public void Id_is_vcr()
    {
        Assert.Equal("vcr", new VcrOverlay().Id);
    }

    [Fact]
    public void Off_tape_draws_nothing()
    {
        var v = new VcrOverlay();
        v.Init(40, 40, new Random(1));
        Assert.False(v.IsVisible(0.9f));
        var g = Filled(40, 40);
        // без IsVisible сцена не должна трогаться: вызывающий Compose пропускает Apply
        Assert.Equal('#', g[0, 0].Ch);
    }

    [Fact]
    public void Play_block_is_visible_for_4_seconds_then_gone()
    {
        const int cols = 120, rows = 40;
        var v = Make(cols, rows);

        v.Update(3.8f);                           // t = 3.8 с: показ идёт
        Assert.True(v.IsVisible(0f));
        var g = Filled(cols, rows);
        v.Apply(g, Ctx());
        Assert.True(IsSextant(g[2, 2].Ch));       // PLAY

        v.Update(1.2f);                           // t = 5.0 с: всё погасло
        Assert.False(v.IsVisible(0f));
    }

    [Fact]
    public void Play_blinks_during_first_two_seconds_of_show()
    {
        var s = new VcrState();
        s.SetOn(true);
        s.Update(0.2f);
        Assert.True(s.PlayVisible);               // фаза «вкл»
        s.Update(0.6f);                           // 0.8 с — фаза «выкл»
        Assert.False(s.PlayVisible);
        s.Update(1.4f);                           // 2.2 с — мигание кончилось, PLAY постоянно
        Assert.True(s.PlayVisible);
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
        ShowNow(v);                              // видик включили на 90-й минуте ленты
        v.Apply(g, Ctx());
        int bar = rows - 3;
        int filled = CountChar(g, bar, '=');
        Assert.InRange(filled, 9, 11);
        Assert.Contains("S[", Row(g, bar));
        Assert.Contains("1:30:00", Row(g, bar));  // 90 мин = 1:30:00 ленты
    }

    [Fact]
    public void Small_tape_end_shows_rew_then_play_then_bar_is_nearly_empty()
    {
        const int cols = 40, rows = 40;
        var g = Filled(cols, rows);
        var v = Make(cols, rows);
        v.Update(180 * 60f + 0.1f);              // конец ленты: REW сразу
        v.Apply(g, Ctx());
        Assert.Contains("REW", Row(g, 2));

        v.Update(4.1f);                          // REW отыграл — показ PLAY ещё 4 с
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
    public void Auto_tracking_blinks_at_1_hz_while_fatigue_holds()
    {
        var v = Make(120, 40);
        v.Update(10.2f);                         // показа нет, фаза «вкл»
        Assert.True(v.IsVisible(0.7f));
        v.Update(0.5f);                          // фаза «выкл»
        Assert.False(v.IsVisible(0.7f));
        v.Update(0.5f);                          // снова «вкл»
        Assert.True(v.IsVisible(0.7f));
        Assert.False(v.IsVisible(0.3f));         // усталость упала — не показываем
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
    public void Big_text_is_only_in_its_rows_and_no_plate_color_is_used()
    {
        const int cols = 120, rows = 40;
        var g = Filled(cols, rows);
        var v = Make(cols, rows);
        v.Update(0.1f);
        ShowNow(v);
        v.Apply(g, Ctx(new DateTime(2026, 10, 10, 14, 5, 0), fatigue: 0.7f));

        // Текст: верхняя строка (y 2..4) и строка счётчика (y rows-5..rows-3).
        int top = 2, bar = rows - 5;
        bool InText(int y) => (y >= top && y < top + 3) || (y >= bar && y < bar + 3);
        int textCells = 0;
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
            {
                var cell = g[x, y];
                if (cell.Ch != ' ')
                {
                    textCells++;
                    Assert.True(InText(y), $"текст задел строку {y} в ({x},{y})");
                }
                Assert.NotEqual(PlateColor, cell.Bg);   // подложки нет
            }
        Assert.True(textCells > 0);
    }

    [Fact]
    public void Big_top_left_is_sextant_text_without_plate()
    {
        const int cols = 120, rows = 40;
        var g = Filled(cols, rows);
        var v = Make(cols, rows);
        v.Update(0.1f);                          // PLAY видим
        v.Apply(g, Ctx());
        Assert.True(IsSextant(g[2, 2].Ch));      // первая клетка буквы P
        Assert.Equal(Rgba.Black, g[1, 1].Bg);    // поля-подложки нет: фон клетки не тронут
    }

    [Fact]
    public void Big_tape_bar_is_half_full_after_90_minutes()
    {
        const int cols = 120, rows = 40;
        var g = Filled(cols, rows);
        var v = Make(cols, rows);
        v.Update(90 * 60f);
        ShowNow(v);
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
        // Между PLAY (x 2..25) и часами (справа) — текст есть и не слипается с ними
        Assert.Contains(Enumerable.Range(28, 50), x => IsSextant(g[x, 2].Ch));
        Assert.DoesNotContain(Enumerable.Range(0, cols), x => g[x, 1].Bg == PlateColor);
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

    // ---- параметры overlay.params.vcr ----

    private static VcrOverlay Configured(int cols, int rows, string json)
    {
        var v = new VcrOverlay();
        v.Configure(new SceneOptions { Params = JsonDocument.Parse(json).RootElement });
        v.Init(cols, rows, new Random(1));
        v.SetOn(true);
        return v;
    }

    /// <summary>Как хост: на каждом кадре свежая сетка, Apply — только если оверлей видим (IsVisible).</summary>
    private static CharGrid Run(VcrOverlay v, int cols, int rows, double seconds)
    {
        int frames = (int)Math.Round(seconds * 60);
        var g = Filled(cols, rows);
        for (int f = 0; f < frames; f++)
        {
            v.Update(1f / 60f);
            g = Filled(cols, rows);
            if (v.IsVisible(0f)) v.Apply(g, Ctx());
        }
        return g;
    }

    private static bool AnyRowContains(CharGrid g, string s)
    {
        for (int y = 0; y < g.Rows; y++)
            if (Row(g, y).Contains(s, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool AnySextant(CharGrid g)
    {
        foreach (var c in g.Cells) if (c.Ch >= 0xE000 && c.Ch <= 0xE03F) return true;   // U+E000..U+E03F, числом
        return false;
    }

    /// <summary>Есть клетки с символом ch и все они цвета fg.</summary>
    private static bool AllFgOf(CharGrid g, char ch, Rgba fg)
    {
        int n = 0;
        foreach (var c in g.Cells)
        {
            if (c.Ch != ch) continue;
            if (c.Fg != fg) return false;
            n++;
        }
        return n > 0;
    }

    [Fact]
    public void Style_Text_IsAlwaysSmallMode_NoSextants()
    {
        var big = Filled(200, 60);
        Configured(200, 60, "{}").Apply(big, Ctx());    // контроль: по умолчанию крупный шрифт (секстанты)
        Assert.True(AnySextant(big), "контроль: по умолчанию секстантов нет");

        var g = Filled(200, 60);
        var v = Configured(200, 60, "{\"style\":\"text\"}");
        v.Update(0.1f);
        v.Apply(g, Ctx());
        Assert.False(AnySextant(g), "style=text, а секстанты нарисованы");
        Assert.True(AnyRowContains(g, "PLAY"), "style=text: PLAY не нарисован буквами");
    }

    [Fact]
    public void Color_Green_ColorsLetters()
    {
        var g = Filled(200, 60);
        var v = Configured(200, 60, "{\"style\":\"text\",\"color\":\"green\"}");
        v.Update(0.1f);
        v.Apply(g, Ctx());
        Assert.True(AllFgOf(g, 'P', Rgba.Hex(0x3CFF3C)), "буквы PLAY не зелёные");
    }

    [Fact]
    public void ShowSec_Configurable_ControlsHowLongOsdStays()
    {
        var v10 = Configured(200, 60, "{\"style\":\"text\",\"showSec\":10}");
        var g10 = Run(v10, 200, 60, 6);
        Assert.True(AnyRowContains(g10, "SP"), "showSec=10: через 6 с OSD уже погас");

        var v4 = Configured(200, 60, "{\"style\":\"text\",\"showSec\":4}");
        var g4 = Run(v4, 200, 60, 6);
        Assert.False(AnyRowContains(g4, "SP"), "showSec=4: через 6 с OSD ещё виден");
    }

    [Fact]
    public void Demo_KeepsPlayClockAndCounterVisibleAfterSixtySeconds()
    {
        var v = Configured(200, 60, "{\"style\":\"text\"}");
        v.Demo = true;
        var g = Run(v, 200, 60, 60);
        Assert.True(AnyRowContains(g, "PLAY"), "Demo: PLAY не виден после 60 с");
        Assert.True(AnyRowContains(g, "SP "), "Demo: часы не видны после 60 с");
        Assert.True(AnyRowContains(g, "]E"), "Demo: счётчик не виден после 60 с");
        for (int f = 0; f < 60; f++)   // в течение секунды PLAY не мигает
        {
            v.Update(1f / 60f);
            var frame = Filled(200, 60);
            v.Apply(frame, Ctx());
            Assert.True(AnyRowContains(frame, "PLAY"), $"Demo: PLAY пропал на {f} кадре после 60 с");
        }
    }

    [Fact]
    public void CounterOff_NoTapeBarOrCounter()
    {
        var on = Run(Configured(200, 60, "{\"style\":\"text\"}"), 200, 60, 1);
        Assert.True(AnyRowContains(on, "["), "контроль: шкала не нарисована по умолчанию");

        var off = Run(Configured(200, 60, "{\"style\":\"text\",\"counter\":false}"), 200, 60, 1);
        Assert.False(AnyRowContains(off, "["), "counter=false, а шкала '[' нарисована");
        Assert.False(AnyRowContains(off, "]E"), "counter=false, а счётчик нарисован");
        Assert.True(AnyRowContains(off, "PLAY"), "counter=false: PLAY должен остаться");
    }

    [Fact]
    public void TextDump_Vcr_ForVisualCheck()
    {
        var sb = new StringBuilder();
        foreach (var style in new[] { "pixel", "text" })
        {
            var v = Configured(120, 30, $"{{\"style\":\"{style}\"}}");
            v.Demo = true;
            var g = Run(v, 120, 30, 3);
            sb.Append("== style=").Append(style).Append(" Demo ==\n");
            for (int y = 0; y < g.Rows; y++) sb.Append(Row(g, y)).Append('\n');
        }
        string path = Path.Combine(Path.GetTempPath(), "phosphor-dump-vcr.txt");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        Assert.True(File.Exists(path));
    }
}
