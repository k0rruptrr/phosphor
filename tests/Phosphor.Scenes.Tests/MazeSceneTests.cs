using System.Diagnostics;
using System.Text.Json;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class MazeSceneTests
{
    private readonly ITestOutputHelper _out;

    public MazeSceneTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new MazeScene(), "maze");

    [Fact]
    public void NoMusicIsAlive()
    {
        var s = SceneContract.Make(() => new MazeScene(), 120, 40, 81);
        SceneContract.Run(s, 60);
        var a = SceneContract.Snap(s, 120, 40);
        SceneContract.Run(s, 180);                       // 3 с без музыки
        var b = SceneContract.Snap(s, 120, 40);
        double share = ChangedShare(a, b);
        _out.WriteLine($"без музыки за 3 с изменилось {share:P1} ячеек");
        Assert.True(share > 0.2, $"за 3 с изменилось лишь {share:P1} ячеек");
    }

    [Fact]
    public void Fast_On192x60()
    {
        var s = SceneContract.Make(() => new MazeScene(), 192, 60, 31);
        var g = new CharGrid(192, 60);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 600; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
        }
        sw.Stop();
        _out.WriteLine($"600 кадров Update+Draw 192x60: {sw.Elapsed.TotalMilliseconds:F1} мс");
        Assert.True(sw.ElapsedMilliseconds < 1500, $"600 кадров заняли {sw.ElapsedMilliseconds} мс");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryGeneratorMakesPerfectMaze(int gen)
    {
        var s = (MazeScene)SceneContract.Make(() => new MazeScene(), 120, 40, 11 + gen);
        s.RestartWith(gen);
        RunUntil(s, () => s.GenerationDone);
        Assert.True(s.GenerationDone, "генерация не закончилась за 20 с");

        int n = s.CellCount;
        var (mw, mh) = s.MazeSize;
        int open = 0;
        for (int c = 0; c < n; c++)
        {
            if (c % mw < mw - 1 && s.IsOpen(c, 1)) open++;
            if (c / mw < mh - 1 && s.IsOpen(c, 2)) open++;
        }
        _out.WriteLine($"генератор {gen}: клеток {n}, открытых рёбер {open}");
        Assert.Equal(n - 1, open);

        var seen = new bool[n];
        var stack = new Stack<int>();
        stack.Push(0);
        seen[0] = true;
        int reached = 1;
        while (stack.Count > 0)
        {
            int c = stack.Pop();
            for (int d = 0; d < 4; d++)
            {
                if (!s.IsOpen(c, d)) continue;
                int nb = Neighbour(c, d, mw);
                if (seen[nb]) continue;
                seen[nb] = true;
                reached++;
                stack.Push(nb);
            }
        }
        Assert.Equal(n, reached);
    }

    [Theory]
    [InlineData("astar")]
    [InlineData("bfs")]
    [InlineData("wall")]
    public void PathLeadsFromEntranceToExit(string solver)
    {
        var opts = Opt($"{{\"solver\":\"{solver}\"}}");
        var s = (MazeScene)SceneContract.Make(() => new MazeScene(), 120, 40, 21, opts);
        RunUntil(s, () => s.PathReady);
        Assert.True(s.PathReady, "решение не закончилось за 30 с");

        var (mw, _) = s.MazeSize;
        int len = s.PathLength;
        _out.WriteLine($"решатель {solver}: путь {len} клеток");
        Assert.True(len >= 2, "путь вырожден");
        Assert.Equal(s.CellCount - 1, s.PathAt(0));   // проступает от выхода
        Assert.Equal(0, s.PathAt(len - 1));           // и доходит до входа
        for (int i = 0; i + 1 < len; i++)
        {
            int a = s.PathAt(i), b = s.PathAt(i + 1);
            bool adjacent = false;
            for (int d = 0; d < 4 && !adjacent; d++)
                adjacent = s.IsOpen(a, d) && Neighbour(a, d, mw) == b;
            Assert.True(adjacent, $"разрыв пути между клетками {a} и {b} на шаге {i}");
        }
    }

    [Fact]
    public void JunctionsMatchNeighbours()
    {
        var s = (MazeScene)SceneContract.Make(() => new MazeScene(), 120, 40, 41);
        RunUntil(s, () => s.PathReady);
        var g = SceneContract.Snap(s, 120, 40);

        int checkedArms = 0;
        for (int y = 0; y < g.Rows; y++)
        for (int x = 0; x < g.Cols; x++)
        {
            char ch = g[x, y].Ch;
            int arms = Arms(ch);
            if (arms == 0) continue;
            checkedArms++;
            if (ch is '─' or '═' or '│' or '║')
            {
                // Прямая линия: один и тот же символ рисует и сегмент, и однобокий вылет узла, поэтому
                // требуем только, чтобы линия не висела в воздухе — хотя бы одна ось стыкуется с соседом.
                bool horizontal = (arms & (L | R)) != 0;
                bool ok = horizontal
                    ? (Matches(g, x - 1, y, R) || Matches(g, x + 1, y, L))
                    : (Matches(g, x, y - 1, D) || Matches(g, x, y + 1, U));
                Assert.True(ok, $"линия '{ch}' в ({x},{y}) висит без соседа по своей оси");
                continue;
            }
            // Каждый вылет стыка должен попасть в вылет соседа: висящих ┼, ┐ и т.п. быть не должно.
            Check(g, x, y, arms, L, -1, 0, R);
            Check(g, x, y, arms, R, 1, 0, L);
            Check(g, x, y, arms, U, 0, -1, D);
            Check(g, x, y, arms, D, 0, 1, U);
        }
        _out.WriteLine($"проверено ячеек с псевдографикой: {checkedArms}");
        Assert.True(checkedArms > 100, "на экране почти нет стен");
    }

    [Fact]
    public void EntranceAndExitMarkersAreShown()
    {
        var s = (MazeScene)SceneContract.Make(() => new MazeScene(), 120, 40, 51);
        RunUntil(s, () => s.PathReady);
        var g = SceneContract.Snap(s, 120, 40);
        int entrance = 0, exit = 0;
        foreach (var c in g.Cells)
        {
            if (c.Ch == '►') entrance++;
            if (c.Ch == '◄') exit++;
        }
        Assert.Equal(1, entrance);
        Assert.Equal(1, exit);
    }

    [Theory]
    [InlineData("{\"cellSize\":1}", "{\"cellSize\":4}")]
    [InlineData("{\"speed\":0.2}", "{\"speed\":5}")]
    [InlineData("{\"solver\":\"astar\"}", "{\"solver\":\"wall\"}")]
    public void ParamMinAndMaxDifferIn120Frames(string min, string max)
    {
        var a = SceneContract.Make(() => new MazeScene(), 120, 40, 5, Opt(min));
        var b = SceneContract.Make(() => new MazeScene(), 120, 40, 5, Opt(max));
        SceneContract.Run(a, 120);
        SceneContract.Run(b, 120);
        double share = ChangedShare(SceneContract.Snap(a, 120, 40), SceneContract.Snap(b, 120, 40));
        _out.WriteLine($"{min} против {max}: различается {share:P1} ячеек");
        Assert.True(share > 0.05, $"Min и Max почти одинаковы: {share:P1}");
    }

    private static SceneOptions Opt(string json) => new() { Params = JsonDocument.Parse(json).RootElement };

    private static void RunUntil(MazeScene s, Func<bool> done)
    {
        for (int i = 0; i < 60 * 30 && !done(); i++) s.Update(SceneContract.Dt);
    }

    // Направления: 0 — север, 1 — восток, 2 — юг, 3 — запад.
    private static int Neighbour(int c, int d, int mw) => d switch
    {
        0 => c - mw,
        1 => c + 1,
        2 => c + mw,
        _ => c - 1,
    };

    private const int L = 1, R = 2, U = 4, D = 8;

    /// <summary>Вылеты стыка по символу: маска из L, R, U, D.</summary>
    private static int Arms(char ch) => ch switch
    {
        '─' or '═' => L | R,
        '│' or '║' => U | D,
        '┌' or '╔' => R | D,
        '┐' or '╗' => L | D,
        '└' or '╚' => U | R,
        '┘' or '╝' => U | L,
        '├' or '╠' => U | D | R,
        '┤' or '╣' => U | D | L,
        '┬' or '╦' => L | R | D,
        '┴' or '╩' => L | R | U,
        '┼' => L | R | U | D,
        '╤' => L | R | D,
        '╧' => L | R | U,
        '╟' => U | D | R,
        '╢' => U | D | L,
        '╓' => D | R,
        '╙' => U | R,
        '╖' => D | L,
        '╜' => U | L,
        _ => 0,
    };

    private static bool Matches(CharGrid g, int x, int y, int back) =>
        g.InBounds(x, y) && (Arms(g[x, y].Ch) & back) != 0;

    private static void Check(CharGrid g, int x, int y, int arms, int dir, int dx, int dy, int back)
    {
        if ((arms & dir) == 0) return;
        int nx = x + dx, ny = y + dy;
        Assert.True(g.InBounds(nx, ny), $"вылет за сетку в ({x},{y})");
        int nArms = Arms(g[nx, ny].Ch);
        Assert.True((nArms & back) != 0,
            $"стык '{g[x, y].Ch}' в ({x},{y}) смотрит в соседа ({nx},{ny}) '{g[nx, ny].Ch}', а тот не отвечает");
    }

    private static double ChangedShare(CharGrid a, CharGrid b)
    {
        int n = 0;
        for (int i = 0; i < a.Cells.Length; i++)
            if (a.Cells[i].Fg != b.Cells[i].Fg || a.Cells[i].Bg != b.Cells[i].Bg) n++;
        return n / (double)a.Cells.Length;
    }
}
