using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Phosphor.Core;
using Phosphor.Overlays;
using Phosphor.Scenes;

namespace Phosphor.Overlays.Tests;

public class HudOverlayTests
{
    private static readonly Rgba Accent = Rgba.Hex(0xFF6600);
    private const float Dt = 1f / 60f;

    private static OverlayContext Ctx(string scene = "dvd", long frame = 4211, DateTime? clock = null,
        AudioFrame? audio = null, Organism? organism = null) =>
        new OverlayContext(scene, "clean", 0, 1, 59.9f, frame, clock,
            audio ?? AudioFrame.Silent, organism ?? new Organism(0f, 0f, 0f, 0f));

    private static (IScene scene, CharGrid grid, HudOverlay hud) Make(IScene scene, int cols, int rows, int seed = 1)
    {
        scene.Init(cols, rows, new Random(seed), new SceneOptions());
        var grid = new CharGrid(cols, rows);
        var hud = new HudOverlay();
        hud.Init(cols, rows, new Random(seed));
        return (scene, grid, hud);
    }

    /// <summary>Гонит сцену; если withHud — оверлей поверх каждого кадра. Возвращает сетку последнего кадра.</summary>
    private static CharGrid Run(IScene scene, int cols, int rows, float seconds, bool withHud, OverlayContext ctx,
        int seed = 1)
    {
        var (s, grid, hud) = Make(scene, cols, rows, seed);
        int frames = (int)MathF.Round(seconds / Dt);
        for (int f = 0; f < frames; f++)
        {
            s.Update(Dt);
            s.Draw(grid);
            if (withHud)
            {
                hud.Update(Dt);
                hud.Apply(grid, ctx);
            }
        }
        return grid;
    }

    private static bool SameCell(Cell a, Cell b) => a.Ch == b.Ch && a.Fg == b.Fg && a.Bg == b.Bg;

    private static string RowText(CharGrid g, int y)
    {
        var sb = new StringBuilder(g.Cols);
        for (int x = 0; x < g.Cols; x++) sb.Append(g.Cells[y * g.Cols + x].Ch);
        return sb.ToString();
    }

    private static bool Contains(CharGrid g, string needle)
    {
        for (int y = 0; y < g.Rows; y++)
            if (RowText(g, y).Contains(needle, StringComparison.Ordinal)) return true;
        return false;
    }

    [Fact]
    public void Dvd_MainFrameIsNearLogo_AndHasCornersAndTgtLabel()
    {
        var dvd = new DvdScene();
        var (s, grid, hud) = Make(dvd, 192, 60);
        var snap = new Cell[grid.Cells.Length];
        for (int f = 0; f < 180; f++)
        {
            s.Update(Dt);
            s.Draw(grid);
            Array.Copy(grid.Cells, snap, snap.Length);   // сцена без разметки этого кадра
            hud.Update(Dt);
            hud.Apply(grid, Ctx());
        }

        Assert.True(Contains(grid, "┌") && Contains(grid, "┘"), "нет уголков рамки");
        Assert.True(Contains(grid, "TGT-"), "нет подписи TGT-");

        // Измеренный bbox главной цели покрывает все непробельные ячейки логотипа (допуск ±1).
        Assert.True(hud.TryGetMainBox(out int bx0, out int by0, out int bx1, out int by1), "цель не найдена");
        int checkedCells = 0;
        for (int y = dvd.Y; y < dvd.Y + dvd.LogoHeight; y++)
        {
            for (int x = dvd.X; x < dvd.X + dvd.LogoWidth; x++)
            {
                if (snap[y * grid.Cols + x].Ch == ' ') continue;
                checkedCells++;
                Assert.True(x >= bx0 - 1 && x <= bx1 + 1 && y >= by0 - 1 && y <= by1 + 1,
                    $"ячейка логотипа ({x},{y}) вне bbox [{bx0},{by0}]-[{bx1},{by1}] ±1");
            }
        }
        Assert.True(checkedCells > 0, "логотип не найден в кадре");
    }

    [Fact]
    public void GradientBackground_NoSymbols_NoTargets()
    {
        const int cols = 192, rows = 60;
        var grid = new CharGrid(cols, rows);
        var hud = new HudOverlay();
        hud.Init(cols, rows, new Random(1));
        var ctx = Ctx();
        for (int f = 0; f < 120; f++)
        {
            // Как хост: сцена перерисовывает ВСЮ сетку каждый кадр (иначе видна собственная разметка HUD).
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    byte v = (byte)(x * 229 / (cols - 1));   // luma 0..0.9, плавный градиент
                    grid.Cells[y * cols + x] = new Cell(' ', Rgba.White, new Rgba(v, v, v));
                }
            }
            hud.Update(Dt);
            hud.Apply(grid, ctx);
        }
        bool found = hud.TryGetMainBox(out int gx0, out int gy0, out int gx1, out int gy1);
        Assert.False(found, $"цель захвачена на градиенте фона: [{gx0},{gy0}]-[{gx1},{gy1}]");
        Assert.False(Contains(grid, "┌"), "уголки на градиенте фона");
    }

    [Fact]
    public void Dvd_CounterBlocksSwatches_SecStaysVisible()
    {
        const int cols = 192, rows = 60;
        var grid = Run(new DvdScene(), cols, rows, 2f, true, Ctx());
        // Счётчик «corners: N» DVD — в строке rows-2; свотчи там не рисуются.
        Assert.DoesNotContain("█", RowText(grid, rows - 2));
        // Шапка SEC свободна — рисуется.
        Assert.Contains("SEC", RowText(grid, 1));
    }

    [Theory]
    [InlineData("matrix")]
    [InlineData("boids")]
    [InlineData("aquarium")]
    public void Scenes_FiveSeconds_NoExceptions_BudgetAndGlyphs(string id)
    {
        const int cols = 192, rows = 60;
        IScene Make2() => id switch
        {
            "matrix" => new MatrixScene(),
            "boids" => new BoidsScene(),
            _ => new AquariumScene(),
        };

        var ctx = Ctx(scene: id, audio: new AudioFrame(true, 0.5f, 0.6f, 0.4f, 0.2f, false, 0f, 120f));
        var plain = Run(Make2(), cols, rows, 5f, withHud: false, ctx);
        var withHud = Run(Make2(), cols, rows, 5f, withHud: true, ctx);

        int changed = 0;
        for (int i = 0; i < plain.Cells.Length; i++)
        {
            Cell a = plain.Cells[i], b = withHud.Cells[i];
            if (a.Ch == b.Ch && a.Fg == b.Fg && a.Bg == b.Bg) continue;
            changed++;
            if (a.Ch != b.Ch)
                Assert.True(GlyphSet.Contains(b.Ch), $"символ вне GlyphSet: U+{(int)b.Ch:X4}");
        }
        Assert.True(changed <= plain.Cells.Length * 0.06, $"разметка {changed} из {plain.Cells.Length} ячеек > 6%");
    }

    [Fact]
    public void Clock_IsShownInFirstRow()
    {
        var grid = Run(new DvdScene(), 192, 60, 2f, true,
            Ctx(clock: new DateTime(2026, 10, 9, 4, 20, 15)));
        Assert.Contains("04:20:15", RowText(grid, 1));
    }

    [Fact]
    public void Clock_Null_ShowsDashes()
    {
        var grid = Run(new DvdScene(), 192, 60, 2f, true, Ctx(clock: null));
        Assert.Contains("--:--:--", RowText(grid, 1));
    }

    [Fact]
    public void SafeZone_IsNotTouched()
    {
        const int cols = 192, rows = 60;
        var scene = new DvdScene();
        var (s, grid, hud) = Make(scene, cols, rows);
        var baseCopy = new Cell[cols * rows];
        for (int f = 0; f < 120; f++)
        {
            s.Update(Dt);
            s.Draw(grid);
            Array.Copy(grid.Cells, baseCopy, baseCopy.Length);
            hud.Update(Dt);
            hud.Apply(grid, Ctx());
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    bool inSafe = x >= 2 && x <= cols - 3 && y >= 1 && y <= rows - 2;
                    if (inSafe) continue;
                    int i = y * cols + x;
                    Assert.True(SameCell(grid.Cells[i], baseCopy[i]), $"оверлей тронул ({x},{y}) вне безопасной зоны");
                }
            }
        }
    }

    [Fact]
    public void Deterministic_SameSeed_SameGrid()
    {
        var ctx = Ctx(clock: new DateTime(2026, 10, 9, 4, 20, 15));
        var a = Run(new MatrixScene(), 120, 40, 3f, true, ctx, seed: 7);
        var b = Run(new MatrixScene(), 120, 40, 3f, true, ctx, seed: 7);
        Assert.Equal(a.Cells.Length, b.Cells.Length);
        for (int i = 0; i < a.Cells.Length; i++)
            Assert.True(SameCell(a.Cells[i], b.Cells[i]), $"ячейка {i} различается");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(10, 3)]
    [InlineData(480, 140)]
    public void TinyAndHugeGrids_NoExceptions(int cols, int rows)
    {
        var ctx = Ctx(clock: new DateTime(2026, 10, 9, 4, 20, 15), audio: new AudioFrame(true, 1f, 1f, 1f, 1f, true, 1f, 90f));
        var g1 = Run(new DvdScene(), cols, rows, 1f, true, ctx);
        var g2 = Run(new MatrixScene(), cols, rows, 1f, true, ctx);
        Assert.Equal(cols, g1.Cols);
        Assert.Equal(rows, g2.Rows);
    }

    [Fact]
    public void Apply_Performance_600CallsUnder300Ms()
    {
        const int cols = 192, rows = 60;
        var scene = new MatrixScene();
        var (s, grid, hud) = Make(scene, cols, rows);
        for (int f = 0; f < 60; f++) { s.Update(Dt); s.Draw(grid); }
        var baseCells = (Cell[])grid.Cells.Clone();
        var ctx = Ctx(scene: "matrix", clock: new DateTime(2026, 10, 9, 4, 20, 15));

        // Предварительный прогрев (JIT), не в замере.
        hud.Update(Dt);
        hud.Apply(grid, ctx);

        var sw = new Stopwatch();
        for (int i = 0; i < 600; i++)
        {
            Array.Copy(baseCells, grid.Cells, baseCells.Length);
            hud.Update(Dt);
            sw.Start();
            hud.Apply(grid, ctx);
            sw.Stop();
        }
        Assert.True(sw.Elapsed.TotalMilliseconds < 300, $"600 Apply заняли {sw.Elapsed.TotalMilliseconds:F1} мс");
    }

    [Fact]
    public void Dvd_DisplayedCorners_WithinOneCellOfMeasuredBox()
    {
        const int cols = 192, rows = 60;
        var (s, grid, hud) = Make(new DvdScene(), cols, rows);
        var ctx = Ctx();
        for (int f = 0; f < 120; f++)   // 2 с: логотип движется 14 кл/с
        {
            s.Update(Dt);
            s.Draw(grid);
            hud.Update(Dt);
            hud.Apply(grid, ctx);
        }

        Assert.True(hud.TryGetMainBox(out int bx0, out int by0, out int bx1, out int by1), "цель не найдена");
        int fx0 = -1, fy0 = -1, fx1 = -1, fy1 = -1;
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                Cell c = grid.Cells[y * cols + x];
                if (c.Fg != Accent) continue;
                if (c.Ch == '┌' && fx0 < 0) { fx0 = x; fy0 = y; }
                if (c.Ch == '┘') { fx1 = x; fy1 = y; }
            }
        }
        Assert.True(fx0 >= 0 && fx1 >= 0, "акцентная рамка не нарисована");

        // Уголки стоят на bbox ±1: допуск по каждой оси — 1 клетка.
        Assert.True(Math.Abs(fx0 - (bx0 - 1)) <= 1, $"левый край: уголок {fx0}, bbox {bx0}");
        Assert.True(Math.Abs(fy0 - (by0 - 1)) <= 1, $"верхний край: уголок {fy0}, bbox {by0}");
        Assert.True(Math.Abs(fx1 - (bx1 + 1)) <= 1, $"правый край: уголок {fx1}, bbox {bx1}");
        Assert.True(Math.Abs(fy1 - (by1 + 1)) <= 1, $"нижний край: уголок {fy1}, bbox {by1}");
    }

    [Fact]
    public void Matrix_Dense_StaticElementsDrawnOnBlackPlate()
    {
        const int cols = 192, rows = 60;
        var (s, grid, hud) = Make(new MatrixScene(), cols, rows);
        var ctx = Ctx(scene: "matrix");
        for (int f = 0; f < 300; f++)   // 5 с
        {
            s.Update(Dt);
            s.Draw(grid);
            hud.Update(Dt);
            hud.Apply(grid, ctx);
        }

        Assert.True(hud.Dense, "matrix должен считаться плотной сценой");
        string row1 = RowText(grid, 1);
        int sec = row1.IndexOf("SEC", StringComparison.Ordinal);
        Assert.True(sec >= 0, "SEC не нарисован");
        for (int i = sec; i < sec + 3; i++)
            Assert.Equal(Rgba.Black, grid.Cells[1 * cols + i].Bg);

        int clock = row1.IndexOf("--:--:--", StringComparison.Ordinal);
        Assert.True(clock >= 0, "часы не нарисованы");
        for (int i = clock; i < clock + 8; i++)
            Assert.Equal(Rgba.Black, grid.Cells[1 * cols + i].Bg);
    }

    [Fact]
    public void DenseRuler_PlateOnlyUnderLabelRows()
    {
        const int cols = 192, rows = 60;
        var bg = Rgba.Hex(0x303030);
        var grid = new CharGrid(cols, rows);
        void Content(CharGrid g)
        {
            // Плотная сцена: все ячейки заняты яркими символами на сером фоне.
            for (int i = 0; i < g.Cells.Length; i++) g.Cells[i] = new Cell('█', Rgba.White, bg);
        }
        var hud = new HudOverlay();
        hud.Init(cols, rows, new Random(1));
        var ctx = Ctx();
        for (int f = 0; f < 5; f++)
        {
            Paint(grid, Content);
            hud.Update(Dt);
            hud.Apply(grid, ctx);
        }

        Assert.True(hud.Dense, "сцена должна считаться плотной");
        int labelRows = 0, black = 0, blackUnlabeled = 0, changedUnlabeled = 0;
        for (int y = 3; y <= rows - 4; y++)
        {
            bool label = y % 9 == 0;
            if (label) labelRows++;
            for (int x = 2; x <= 5; x++)   // колонка линейки: засечка и число
            {
                Rgba cellBg = grid.Cells[y * cols + x].Bg;
                if (cellBg == Rgba.Black)
                {
                    black++;
                    if (!label) blackUnlabeled++;
                }
                if (!label && cellBg != bg) changedUnlabeled++;
            }
        }
        int limit = labelRows * (2 + 2);   // число строк с подписью × (ширина подписи 2 + 2)
        Assert.True(black <= limit, $"чёрных клеток в колонке линейки: {black} (на строках без подписи: {blackUnlabeled}), лимит {limit}");
        Assert.True(changedUnlabeled == 0, $"Bg изменён в {changedUnlabeled} клетках строк без подписи");
    }

    [Fact]
    public void Aquarium_Labels_InSafeZone_AndNearCorner()
    {
        const int cols = 192, rows = 60;
        var (s, grid, hud) = Make(new AquariumScene(), cols, rows);
        var ctx = Ctx(scene: "aquarium", clock: new DateTime(2026, 10, 9, 4, 20, 15));
        var label = new Regex(@"TGT-\d{2,} · \d+\.\d{2}");
        int labels = 0;
        for (int f = 0; f < 300; f++)   // 5 с
        {
            s.Update(Dt);
            s.Draw(grid);
            hud.Update(Dt);
            hud.Apply(grid, ctx);

            for (int y = 0; y < rows; y++)
            {
                foreach (Match m in label.Matches(RowText(grid, y)))
                {
                    labels++;
                    int x0 = m.Index, x1 = m.Index + m.Length - 1;
                    Assert.True(x0 >= 2 && x1 <= cols - 3, $"подпись вне безопасной зоны: кадр {f}, y={y}, x={x0}..{x1}");

                    bool corner = false;
                    for (int yy = y - 3; yy <= y + 3 && !corner; yy++)
                    {
                        for (int xx = x0 - 3; xx <= x1 + 3 && !corner; xx++)
                        {
                            if (xx < 0 || yy < 0 || xx >= cols || yy >= rows) continue;
                            char ch = grid.Cells[yy * cols + xx].Ch;
                            if (ch == '┌' || ch == '┐' || ch == '└' || ch == '┘') corner = true;
                        }
                    }
                    Assert.True(corner, $"рядом с подписью нет уголка: кадр {f}, y={y}, x={x0}");
                }
            }
        }
        Assert.True(labels > 0, "подписи целей не появились");
    }

    /// <summary>Заполняет сетку как сцена каждого кадра: фон чёрный, затем содержимое.</summary>
    private static void Paint(CharGrid g, Action<CharGrid> content)
    {
        for (int i = 0; i < g.Cells.Length; i++) g.Cells[i] = new Cell(' ', Rgba.White, Rgba.Black);
        content(g);
    }

    [Fact]
    public void FullWidthBand_IsRejected_BlockIsTheOnlyTarget()
    {
        const int cols = 192, rows = 60;
        const int bx = cols / 2 - 3, by = rows / 2 - 1;   // блок 6x3 в центре
        var grid = new CharGrid(cols, rows);
        void Content(CharGrid g)
        {
            for (int y = rows - 4; y < rows; y++)
                for (int x = 0; x < cols; x++)
                    g.Cells[y * cols + x] = new Cell('▒', new Rgba(200, 200, 200), Rgba.Black);
            for (int y = by; y < by + 3; y++)
                for (int x = bx; x < bx + 6; x++)
                    g.Cells[y * cols + x] = new Cell('█', Rgba.White, Rgba.Black);
        }
        var hud = new HudOverlay();
        hud.Init(cols, rows, new Random(1));
        var ctx = Ctx();
        for (int f = 0; f < 30; f++)
        {
            Paint(grid, Content);
            hud.Update(Dt);
            hud.Apply(grid, ctx);
        }

        Assert.True(hud.TryGetMainBox(out int x0, out int y0, out int x1, out int y1), "блок не найден");
        Assert.Equal(bx, x0);
        Assert.Equal(by, y0);
        Assert.Equal(bx + 5, x1);
        Assert.Equal(by + 2, y1);
        int corners = 0;
        for (int i = 0; i < grid.Cells.Length; i++)
            if (grid.Cells[i].Ch == '┌' && grid.Cells[i].Fg == Accent) corners++;
        Assert.Equal(1, corners);
    }

    [Fact]
    public void EdgeRow_SceneCounter_NotOverwrittenByTarget()
    {
        const int cols = 192, rows = 60;
        const string counter = "corners: 0";
        int cx = cols - 2 - counter.Length;
        int bx = cols - 14;   // блок у правого нижнего края, рамка уходит на строку rows-2
        var grid = new CharGrid(cols, rows);
        void Content(CharGrid g)
        {
            for (int i = 0; i < counter.Length; i++)
                g.Cells[(rows - 2) * cols + cx + i] = new Cell(counter[i], Rgba.Hex(0x303030), Rgba.Black);
            for (int y = rows - 5; y < rows - 2; y++)
                for (int x = bx; x < bx + 6; x++)
                    g.Cells[y * cols + x] = new Cell('█', Rgba.White, Rgba.Black);
        }
        var hud = new HudOverlay();
        hud.Init(cols, rows, new Random(1));
        var ctx = Ctx();
        for (int f = 0; f < 30; f++)
        {
            Paint(grid, Content);
            hud.Update(Dt);
            hud.Apply(grid, ctx);
        }

        Assert.True(hud.TryGetMainBox(out _, out _, out _, out _), "блок не найден");
        for (int i = 0; i < counter.Length; i++)
            Assert.Equal(counter[i], grid.Cells[(rows - 2) * cols + cx + i].Ch);
    }

    private static SceneOptions Opts(string json) =>
        new SceneOptions { Params = JsonDocument.Parse(json).RootElement };

    /// <summary>Пустая чёрная сетка за кадр, оверлей после Configure (если json задан) и Init, как в хосте.</summary>
    private static (CharGrid grid, HudOverlay hud) RunEmpty(string? json, int cols, int rows, int frames)
    {
        var hud = new HudOverlay();
        if (json != null) hud.Configure(Opts(json));
        hud.Init(cols, rows, new Random(1));
        var grid = new CharGrid(cols, rows);
        var ctx = Ctx(clock: new DateTime(2026, 10, 9, 4, 20, 15));
        for (int f = 0; f < frames; f++)
        {
            Paint(grid, _ => { });
            hud.Update(Dt);
            hud.Apply(grid, ctx);
        }
        return (grid, hud);
    }

    private static bool AnyFg(CharGrid g, Rgba fg)
    {
        foreach (var c in g.Cells) if (c.Fg == fg) return true;
        return false;
    }

    private static bool RulerAt(CharGrid g)
    {
        for (int y = 3; y <= g.Rows - 4; y++)
        {
            char c = g.Cells[y * g.Cols + 2].Ch;
            if (c == '├' || c == '┼') return true;
        }
        return false;
    }

    [Fact]
    public void Accent_Cyan_ReplacesOrangeEverywhere()
    {
        var (grid, _) = RunEmpty("{\"accent\":\"cyan\"}", 192, 60, 120);   // 2 с: REC мигает
        Assert.True(AnyFg(grid, Rgba.Hex(0x00C8FF)), "акцент cyan не нарисован");
        Assert.False(AnyFg(grid, Accent), "оранжевый акцент остался");
    }

    [Fact]
    public void RulerOff_NoRulerMarksInFirstColumn()
    {
        var (on, _) = RunEmpty(null, 192, 60, 120);
        Assert.True(RulerAt(on), "линейка по умолчанию не нарисована (проверка теста)");

        var (off, _) = RunEmpty("{\"ruler\":false}", 192, 60, 120);
        Assert.False(RulerAt(off), "ruler=false, а линейка нарисована");
    }

    [Fact]
    public void TargetsZero_NoTargetFramesNoTracking()
    {
        const int cols = 192, rows = 60;
        Func<string?, (CharGrid, HudOverlay)> run = json =>
        {
            var dvd = new DvdScene();
            dvd.Init(cols, rows, new Random(1), new SceneOptions());
            var grid = new CharGrid(cols, rows);
            var hud = new HudOverlay();
            if (json != null) hud.Configure(Opts(json));
            hud.Init(cols, rows, new Random(1));
            for (int f = 0; f < 180; f++)
            {
                dvd.Update(Dt);
                dvd.Draw(grid);
                hud.Update(Dt);
                hud.Apply(grid, Ctx());
            }
            return (grid, hud);
        };

        var (on, hudOn) = run(null);
        Assert.True(hudOn.TryGetMainBox(out _, out _, out _, out _), "цель по умолчанию не найдена (проверка теста)");
        Assert.True(Contains(on, "TGT-"), "подпись по умолчанию не нарисована (проверка теста)");

        var (off, hudOff) = run("{\"targets\":0}");
        Assert.False(hudOff.TryGetMainBox(out _, out _, out _, out _), "targets=0, а трекинг нашёл цель");
        Assert.False(Contains(off, "TGT-"), "targets=0, а подпись цели нарисована");
        Assert.False(Contains(off, "┌"), "targets=0, а уголки цели нарисованы");
    }

    [Fact]
    public void TextDump_Dvd_ForVisualCheck()
    {
        const int cols = 192, rows = 60;
        var grid = Run(new DvdScene(), cols, rows, 3f, true,
            Ctx(clock: new DateTime(2026, 10, 9, 4, 20, 15), audio: new AudioFrame(true, 0.5f, 0.5f, 0.5f, 0.5f, false, 0f, 120f)));
        var sb = new StringBuilder();
        for (int y = 0; y < rows; y++) sb.Append(RowText(grid, y)).Append('\n');
        string path = Path.Combine(Path.GetTempPath(), "phosphor-dump-hud-dvd.txt");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        Assert.True(File.Exists(path));
    }
}
