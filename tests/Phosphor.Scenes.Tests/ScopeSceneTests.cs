using System.Text.Json;
using Phosphor.Core;

namespace Phosphor.Scenes.Tests;

public sealed class ScopeSceneTests
{
    private const int Cols = 120, Rows = 40;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new ScopeScene(), "scope");

    [Fact]
    public void DrawsLitCellsOnGrid()
    {
        var s = SceneContract.Make(() => new ScopeScene(), Cols, Rows, 1);
        SceneContract.Run(s, 300);
        var g = SceneContract.Snap(s, Cols, Rows);
        int lit = g.Cells.Count(IsTrace);
        Assert.True(lit > 100, $"на сетке мало светящихся ячеек луча: {lit}");
    }

    [Fact]
    public void TraceIsConnected()
    {
        // Соседние светящиеся клетки луча не расходятся больше чем на 2 клетки.
        var s = SceneContract.Make(() => new ScopeScene(), Cols, Rows, 2);
        SceneContract.Run(s, 300);
        var g = SceneContract.Snap(s, Cols, Rows);

        var lit = new List<(int X, int Y)>();
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Cols; x++)
                if (IsTrace(g[x, y])) lit.Add((x, y));
        Assert.NotEmpty(lit);

        foreach (var (x, y) in lit)
        {
            bool near = lit.Any(p => (p.X != x || p.Y != y) && Math.Max(Math.Abs(p.X - x), Math.Abs(p.Y - y)) <= 2);
            Assert.True(near, $"светящаяся клетка ({x},{y}) оторвана от луча больше чем на 2 клетки");
        }
    }

    [Fact]
    public void BassWidensFigure()
    {
        var quiet = WidthWithBass(new SceneOptions(), null);
        var loud = WidthWithBass(new SceneOptions(), 1f);
        Assert.True(loud > quiet, $"с басом ширина фигуры {loud} не больше, чем без звука {quiet}");
    }

    [Fact]
    public void FiguresOffNeverShowsFigureIn60Seconds()
    {
        var o = new SceneOptions { Params = JsonDocument.Parse("{\"figures\":false}").RootElement };
        var s = (ScopeScene)SceneContract.Make(() => new ScopeScene(), Cols, Rows, 3, o);
        for (int i = 0; i < 60 * 60; i++)
        {
            s.Update(SceneContract.Dt);
            Assert.False(s.ShowingFigure, $"фигура появилась при figures=false на кадре {i}");
        }
    }

    [Fact]
    public void FiguresOnShowsFigureWithin40Seconds()
    {
        var s = (ScopeScene)SceneContract.Make(() => new ScopeScene(), Cols, Rows, 4);
        bool seen = false;
        for (int i = 0; i < 40 * 60 && !seen; i++)
        {
            s.Update(SceneContract.Dt);
            seen = s.ShowingFigure;
        }
        Assert.True(seen, "за 40 с фигура ни разу не появилась при figures=true");
    }

    [Theory]
    [InlineData(1.8f)]
    [InlineData(1.0f)]
    [InlineData(2.2f)]
    public void DivisionsArePhysicallySquare(float aspect)
    {
        // Физически: горизонтальный подпиксель — w/2, вертикальный — aspect·w/3 (w — ширина ячейки).
        var o = new SceneOptions { CellAspect = aspect };
        var s = (ScopeScene)SceneContract.Make(() => new ScopeScene(), Cols, Rows, 6, o);
        var (u, v) = s.Division;
        double hPhys = u / 2.0;          // в ячейках по ширине
        double vPhys = v / 3.0 * aspect; // в ячейках по ширине (высота ячейки = aspect·w)
        Assert.InRange(hPhys / vPhys, 0.98, 1.02);
    }

    /// <summary>Ширина светящейся области (в клетках) через 4 с симуляции с заданным басом (null — без звука).</summary>
    private static int WidthWithBass(SceneOptions o, float? bass)
    {
        var s = SceneContract.Make(() => new ScopeScene(), Cols, Rows, 5, o);
        for (int i = 0; i < 240; i++)
        {
            if (bass is float b)
                ((IAudioReactive)s).OnAudio(new AudioFrame(true, 0.5f, b, 0.5f, 0.5f, false, 0f, 120f));
            s.Update(SceneContract.Dt);
        }
        var g = SceneContract.Snap(s, Cols, Rows);
        int min = int.MaxValue, max = int.MinValue;
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Cols; x++)
                if (IsTrace(g[x, y])) { min = Math.Min(min, x); max = Math.Max(max, x); }
        return max - min;
    }

    /// <summary>Светящаяся ячейка луча: секстель (не сетка) с заметной яркостью.</summary>
    private static bool IsTrace(Cell c) => GlyphSet.IsSextant(c.Ch) && c.Fg.G > 40;
}
