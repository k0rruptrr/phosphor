using System.IO;
using Phosphor.Core;
using Phosphor.Scenes;

namespace Phosphor.Scenes.Tests;

/// <summary>
/// Эстафета (IHandoffReceiver.Seed): новая сцена прорастает из последнего кадра предыдущей.
/// Синтетический previous 96x30: чёрный фон, по центру белый блок 20x8 из '█'. Сцены — 192x60.
/// </summary>
public sealed class HandoffTests
{
    private const int PrevCols = 96, PrevRows = 30;
    private const int Cols = 192, Rows = 60;

    // Блок previous: x 38..57, y 11..18 → на сетке 192x60: колонки 76..115, строки 22..37.
    private const int BoxX0 = 76, BoxX1 = 115, BoxY0 = 22, BoxY1 = 37;

    private const string HeadingChars = "►◄▲▼\\/";

    private static readonly Func<IScene>[] Factories =
    {
        () => new GrayScottScene(),
        () => new LifeScene(),
        () => new BoidsScene(),
        () => new MatrixScene(),
    };

    [Fact] public void ContractGrayScott() => SceneContract.VerifyAll(() => new GrayScottScene(), "grayscott");
    [Fact] public void ContractLife() => SceneContract.VerifyAll(() => new LifeScene(), "life");
    [Fact] public void ContractBoids() => SceneContract.VerifyAll(() => new BoidsScene(), "boids");
    [Fact] public void ContractMatrix() => SceneContract.VerifyAll(() => new MatrixScene(), "matrix");

    [Fact]
    public void GrayScottGrowsFromSilhouette()
    {
        var s = (GrayScottScene)Seeded(new GrayScottScene(), Previous());
        s.Update(SceneContract.Dt);
        var g = SceneContract.Snap(s, Cols, Rows);

        Assert.True(s.CoverageAbove(0.5f) > 0, "после Seed яркой плесени нет");
        float inside = MeanLum(g, true), outside = MeanLum(g, false);
        Assert.True(inside > 3 * outside, $"средняя яркость внутри {inside:F3}, снаружи {outside:F3}, ожидалось в 3+ раза больше");
    }

    [Fact]
    public void LifeGrowsFromSilhouette()
    {
        var s = Seeded(new LifeScene(), Previous());
        s.Update(SceneContract.Dt);
        var g = SceneContract.Snap(s, Cols, Rows);

        float inside = FractionLit(g, true), outside = FractionLit(g, false);
        Assert.True(inside > 0.6f, $"внутри силуэта непустых {inside:P0}, ожидалось > 60%");
        Assert.True(outside < 0.2f, $"снаружи непустых {outside:P0}, ожидалось < 20%");
    }

    [Fact]
    public void BoidsStartInsideSilhouette()
    {
        var s = Seeded(new BoidsScene(), Previous());
        s.Update(SceneContract.Dt);
        var g = SceneContract.Snap(s, Cols, Rows);

        int total = 0, inside = 0;
        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Cols; x++)
            {
                if (HeadingChars.IndexOf(g[x, y].Ch) < 0) continue;
                total++;
                if (InBox(x, y, pad: 3)) inside++;
            }
        Assert.True(total > 0, "агенты не нарисованы");
        Assert.True(inside * 5 >= total * 4, $"внутри области {inside} из {total} агентов, ожидалось >= 80%");
    }

    [Fact]
    public void MatrixHeadsRiseInBlockColumns()
    {
        var s = Seeded(new MatrixScene(), Previous());
        s.Update(SceneContract.Dt);
        var g = SceneContract.Snap(s, Cols, Rows);

        for (int x = BoxX0; x <= BoxX1; x++)
        {
            // Голова — нижняя непробельная ячейка колонки.
            int head = -1;
            for (int y = 0; y < Rows; y++)
                if (g[x, y].Ch != ' ') head = y;
            Assert.True(head >= 0, $"в колонке {x} блока нет струи");
            Assert.InRange(head, BoxY0, BoxY1);
        }
    }

    [Fact]
    public void MatrixOutsideColumnsLikeWithoutSeed()
    {
        var seeded = Seeded(new MatrixScene(), Previous());
        seeded.Update(SceneContract.Dt);
        var plain = Start(new MatrixScene());
        plain.Update(SceneContract.Dt);

        float a = FractionOutsideColumnsNonBlank(SceneContract.Snap(seeded, Cols, Rows));
        float b = FractionOutsideColumnsNonBlank(SceneContract.Snap(plain, Cols, Rows));
        Assert.True(Math.Abs(a - b) < 0.1f, $"доля непробельных вне блока: с Seed {a:P1}, без Seed {b:P1}");
    }

    [Fact]
    public void SeedTinyAndHugePreviousDoNotThrow()
    {
        foreach (var prev in new[] { Previous(1, 1), Previous(480, 140) })
            foreach (var factory in Factories)
            {
                var s = factory();
                Start(s);
                ((IHandoffReceiver)s).Seed(prev);
                SceneContract.Run(s, 120);
                SceneContract.Snap(s, Cols, Rows);
            }
    }

    [Fact]
    public void SeedIsDeterministic()
    {
        foreach (var factory in Factories)
        {
            var a = Scenario(factory(), Previous());
            var b = Scenario(factory(), Previous());
            Assert.Equal(a.Cells, b.Cells);
        }
    }

    [Fact]
    public void LifeDumpAfterSeedForEye()
    {
        var s = Seeded(new LifeScene(), Previous(), PrevCols, PrevRows);
        s.Update(SceneContract.Dt);
        var g = SceneContract.Snap(s, PrevCols, PrevRows);
        string path = SceneContract.Dump(g, "life-handoff");
        Assert.True(File.Exists(path), $"дамп не записан: {path}");

        // Dump печатает '▀' везде (Life рисует только полублоки), поэтому рядом — карта яркости полупикселей:
        // '#' — оба живы, '^' — только верх, 'v' — только низ, '.' — пусто.
        var sb = new System.Text.StringBuilder();
        for (int y = 0; y < g.Rows; y++)
        {
            for (int x = 0; x < g.Cols; x++)
            {
                var c = g[x, y];
                bool top = Lum(c.Fg) > 0.35f, bottom = Lum(c.Bg) > 0.35f;
                sb.Append(top && bottom ? '#' : top ? '^' : bottom ? 'v' : '.');
            }
            sb.Append('\n');
        }
        string lumPath = Path.Combine(Path.GetTempPath(), "phosphor-dump-life-handoff-lum.txt");
        File.WriteAllText(lumPath, sb.ToString());
        Assert.True(File.Exists(lumPath), $"карта яркости не записана: {lumPath}");
    }

    // ---------- Хелперы ----------

    private static IScene Start(IScene s, int cols = Cols, int rows = Rows)
    {
        s.Init(cols, rows, new Random(1), new SceneOptions());
        return s;
    }

    private static IScene Seeded(IScene s, CharGrid prev, int cols = Cols, int rows = Rows)
    {
        Start(s, cols, rows);
        ((IHandoffReceiver)s).Seed(prev);
        return s;
    }

    private static CharGrid Scenario(IScene s, CharGrid prev)
    {
        Seeded(s, prev);
        SceneContract.Run(s, 60);
        return SceneContract.Snap(s, Cols, Rows);
    }

    /// <summary>previous: чёрный фон, по центру белый блок из '█' (для 96x30 — x 38..57, y 11..18).</summary>
    private static CharGrid Previous(int cols = PrevCols, int rows = PrevRows)
    {
        var g = new CharGrid(cols, rows);
        int x0 = cols * 38 / PrevCols, x1 = Math.Max(x0 + 1, cols * 58 / PrevCols);
        int y0 = rows * 11 / PrevRows, y1 = Math.Max(y0 + 1, rows * 19 / PrevRows);
        for (int y = y0; y < y1 && y < rows; y++)
            for (int x = x0; x < x1 && x < cols; x++)
                g.Put(x, y, '█', Rgba.White);
        return g;
    }

    private static bool InBox(int x, int y, int pad = 0) =>
        x >= BoxX0 - pad && x <= BoxX1 + pad && y >= BoxY0 - pad && y <= BoxY1 + pad;

    private static float Lum(Rgba k) => MathF.Max(k.R, MathF.Max(k.G, k.B)) / 255f;

    /// <summary>Яркость ячейки: больше из Fg и Bg, 0..1.</summary>
    private static float CellLum(Cell c) => MathF.Max(Lum(c.Fg), Lum(c.Bg));

    private static float MeanLum(CharGrid g, bool inside)
    {
        double sum = 0;
        int n = 0;
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Cols; x++)
            {
                if (InBox(x, y) != inside) continue;
                sum += CellLum(g[x, y]);
                n++;
            }
        return (float)(sum / n);
    }

    private static float FractionLit(CharGrid g, bool inside)
    {
        int lit = 0, n = 0;
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Cols; x++)
            {
                if (InBox(x, y) != inside) continue;
                n++;
                if (CellLum(g[x, y]) > 0.2f) lit++;
            }
        return (float)lit / n;
    }

    private static float FractionOutsideColumnsNonBlank(CharGrid g)
    {
        int nb = 0, n = 0;
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Cols; x++)
            {
                if (x >= BoxX0 && x <= BoxX1) continue;
                n++;
                if (g[x, y].Ch != ' ') nb++;
            }
        return (float)nb / n;
    }
}
