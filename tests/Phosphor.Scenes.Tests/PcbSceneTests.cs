using System.Diagnostics;
using System.Text.Json;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class PcbSceneTests
{
    private readonly ITestOutputHelper _out;

    public PcbSceneTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new PcbScene(), "pcb");

    [Fact]
    public void NoMusicIsAlive()
    {
        var s = SceneContract.Make(() => new PcbScene(), 120, 40, 81);
        SceneContract.Run(s, 60);
        var a = SceneContract.Snap(s, 120, 40);
        SceneContract.Run(s, 180);                       // 3 с без музыки
        var b = SceneContract.Snap(s, 120, 40);
        double share = ChangedShare(a, b);
        _out.WriteLine($"без музыки за 3 с изменилось {share:P1} ячеек");
        Assert.True(share > 0.005, $"за 3 с изменилось лишь {share:P2} ячеек");
    }

    [Fact]
    public void UpdateAndDrawAre_Fast_On192x60()
    {
        var s = SceneContract.Make(() => new PcbScene(), 192, 60, 31);
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

    [Fact]
    public void BeatSpeedsRouting()
    {
        const int cols = 192, rows = 60, beatEvery = 30;   // удар раз в 0.5 с
        var silent = SceneContract.Make(() => new PcbScene(), cols, rows, 41);
        var beaten = SceneContract.Make(() => new PcbScene(), cols, rows, 41);
        var audio = (IAudioReactive)beaten;
        for (int f = 0; f < 600; f++)                       // 10 с
        {
            silent.Update(SceneContract.Dt);
            int phase = f % beatEvery;
            float pulse = MathF.Exp(-phase * 1000f / 60f / 150f);
            audio.OnAudio(new AudioFrame(Active: true, Level: 0.5f, Bass: 0.5f * pulse, Mid: 0f, High: 0f,
                Beat: phase == 0, BeatPulse: pulse, Bpm: 120f));
            beaten.Update(SceneContract.Dt);
        }
        int a = TrackCells((PcbScene)silent), b = TrackCells((PcbScene)beaten);
        _out.WriteLine($"дорожек через 10 с: без ударов {a}, с ударами {b}");
        Assert.True(b > a * 1.15, $"удары не ускоряют трассировку: {a} против {b}");
    }

    [Theory]
    [InlineData("routeSpeed", "{\"routeSpeed\":0.2}", "{\"routeSpeed\":5}")]
    [InlineData("density", "{\"density\":0.3}", "{\"density\":1}")]
    [InlineData("layers", "{\"layers\":\"one\"}", "{\"layers\":\"two\"}")]
    public void ParamMinAndMaxDrawDifferentPictures(string name, string min, string max)
    {
        var a = SceneContract.Make(() => new PcbScene(), 120, 40, 7, Opts(min));
        var b = SceneContract.Make(() => new PcbScene(), 120, 40, 7, Opts(max));
        SceneContract.Run(a, 120);
        SceneContract.Run(b, 120);
        double share = ChangedShare(SceneContract.Snap(a, 120, 40), SceneContract.Snap(b, 120, 40));
        _out.WriteLine($"{name}: Min и Max различаются в {share:P1} ячеек");
        Assert.True(share > 0.0, $"{name}: Min и Max дают одну картинку");
    }

    [Fact]
    public void TracksStayInsideTheirOwnNet()
    {
        // Дорожка одной цепи не касается чужих клеток и не уходит в пустоту: каждая связь ведёт в клетку той же цепи.
        var s = (PcbScene)SceneContract.Make(() => new PcbScene(), 192, 60, 51);
        int w = s.BoardWidth, h = s.BoardHeight;
        int checkedCells = 0, bad = 0;
        // Проверяем завершённую плату: пока цепь проводится, её клетки уже в резерве, а маски ещё нет.
        for (int pass = 0; pass < 1; pass++)
        {
            for (int f = 0; f < 120 * 60 && !s.Pouring; f++) s.Update(SceneContract.Dt);
            for (int layer = 0; layer < 2; layer++)
            {
                var mask = layer == 0 ? s.TopMask : s.BottomMask;
                var owner = layer == 0 ? s.TopOwner : s.BottomOwner;
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int c = y * w + x;
                    if (mask[c] == 0) continue;
                    checkedCells++;
                    for (int i = 0; i < 8; i++)
                    {
                        if ((mask[c] & (1 << i)) == 0) continue;
                        int nx = x + Dx(i), ny = y + Dy(i);
                        int n = ny * w + nx;
                        bool inside = (uint)nx < (uint)w && (uint)ny < (uint)h;
                        // Связь ведёт в соседа той же цепи (угол диагонали — тоже своя цепь, без маски).
                        if (!inside || Math.Abs(owner[n]) != Math.Abs(owner[c]))
                        {
                            if (bad < 5) _out.WriteLine($"слой {layer} клетка ({x},{y}) бит {i}: маска {mask[c]:X2}, сосед маска {(inside ? mask[n] : -1):X2}, владельцы {owner[c]} / {(inside ? owner[n] : 0)}");
                            bad++;
                        }
                    }
                }
            }
        }
        _out.WriteLine($"проверено клеток дорожек: {checkedCells}, нарушений связности: {bad}, разведено цепей: {s.RoutedNets} из {s.NetCount}");
        Assert.True(checkedCells > 100, "дорожек почти нет");
        Assert.Equal(0, bad);
    }

    [Fact]
    public void RoutesOver80PercentOfNetsWithin120Seconds()
    {
        // Первая плата: доля разведённых цепей к 120 с. Плату смотрим до её смены (Generation == 1).
        var s = (PcbScene)SceneContract.Make(() => new PcbScene(), 192, 60, 61);
        double best = 0;
        int firstGen = s.Generation;
        for (int f = 0; f < 120 * 60; f++)
        {
            s.Update(SceneContract.Dt);
            if (s.Generation != firstGen || s.NetCount == 0) break;
            best = Math.Max(best, s.RoutedNets / (double)s.NetCount);
        }
        _out.WriteLine($"доля разведённых цепей на первой плате за 120 с: {best:P0}");
        Assert.True(best > 0.8, $"за 120 с разведено только {best:P0}");
    }

    [Fact]
    public void ComponentsDoNotOverlapAndStayOnBoard()
    {
        for (int seed = 1; seed <= 6; seed++)
        {
            var s = (PcbScene)SceneContract.Make(() => new PcbScene(), 192, 60, 70 + seed);
            var boxes = s.ComponentBoxes();
            int w = s.BoardWidth, h = s.BoardHeight;
            Assert.True(boxes.Length >= 4, $"seed {seed}: компонентов меньше четырёх");
            foreach (var b in boxes)
            {
                Assert.True(b.X0 >= 0 && b.Y0 >= 0 && b.X1 < w && b.Y1 < h, $"seed {seed}: компонент вне платы {b}");
            }
            for (int i = 0; i < boxes.Length; i++)
            for (int j = i + 1; j < boxes.Length; j++)
            {
                var a = boxes[i]; var c = boxes[j];
                bool overlap = a.X0 <= c.X1 && c.X0 <= a.X1 && a.Y0 <= c.Y1 && c.Y0 <= a.Y1;
                Assert.False(overlap, $"seed {seed}: компоненты {i} и {j} налезают друг на друга");
            }
        }
    }

    [Fact]
    public void StatusRowShowsRoutingProgress()
    {
        var s = SceneContract.Make(() => new PcbScene(), 120, 40, 90);
        SceneContract.Run(s, 900);
        var g = SceneContract.Snap(s, 120, 40);
        var line = new string(Enumerable.Range(0, 120).Select(x => g[x, 39].Ch).ToArray());
        _out.WriteLine($"строка статуса: {line.TrimEnd()}");
        Assert.Contains("ROUTED", line);
        Assert.Contains("VIAS", line);
    }

    [Fact]
    public void EveryRoutedNetIsAContinuousLineOnScreen()
    {
        // Проверка по нарисованной сетке: от площадки одной цепи до площадки другой есть путь по клеткам дорожки/перехода.
        var s = (PcbScene)SceneContract.Make(() => new PcbScene(), 192, 60, 52);
        for (int f = 0; f < 120 * 60 && !s.Pouring; f++) s.Update(SceneContract.Dt);
        Assert.True(s.Pouring, "разводка первой платы не закончилась за 120 с");
        var g = SceneContract.Snap(s, 192, 60);
        int w = s.BoardWidth, h = s.BoardHeight, oy = s.ScreenOffsetY;

        bool Track(int x, int y)
        {
            if (x < 1 || y < oy || x > w || y >= oy + h) return false;
            int idx = (y - oy) * w + (x - 1);
            char c = g[x, y].Ch;
            return s.IsVia(idx) || (GlyphSet.IsSextant(c) && c != GlyphSet.Sextant(0));
        }

        int routed = 0, connected = 0;
        for (int n = 0; n < s.NetCount; n++)
        {
            if (!s.IsRouted(n)) continue;
            routed++;
            var (a, b) = s.NetPads(n);
            var seen = new bool[g.Cols * g.Rows];
            var queue = new Queue<(int X, int Y)>();
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = a.X + dx, y = a.Y + dy;
                if ((dx != 0 || dy != 0) && Track(x, y) && !seen[y * g.Cols + x])
                {
                    seen[y * g.Cols + x] = true;
                    queue.Enqueue((x, y));
                }
            }
            bool reached = false;
            while (queue.Count > 0 && !reached)
            {
                var (x, y) = queue.Dequeue();
                if (Math.Max(Math.Abs(x - b.X), Math.Abs(y - b.Y)) <= 1) { reached = true; break; }
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx != 0 || dy != 0) && Track(nx, ny) && !seen[ny * g.Cols + nx])
                    {
                        seen[ny * g.Cols + nx] = true;
                        queue.Enqueue((nx, ny));
                    }
                }
            }
            if (reached) connected++;
            else _out.WriteLine($"цепь {n}: от ({a.X},{a.Y}) до ({b.X},{b.Y}) разрыв на экране");
        }
        _out.WriteLine($"разведено цепей: {routed}, связны по экрану: {connected}");
        Assert.True(routed > 0, "ни одна цепь не разведена");
        Assert.Equal(routed, connected);
    }

    [Fact]
    public void DefaultDensityGivesDenseBoard()
    {
        var s = (PcbScene)SceneContract.Make(() => new PcbScene(), 192, 60, 11);
        _out.WriteLine($"компонентов: {s.ComponentBoxes().Length}, цепей: {s.NetCount}");
        Assert.True(s.ComponentBoxes().Length >= 20, "компонентов меньше 20");
        Assert.True(s.NetCount >= 60, $"цепей меньше 60: {s.NetCount}");
    }

    [Fact]
    public void FirstBoardRoutesInAboutOneAndAHalfMinutes()
    {
        // При routeSpeed=1 первая плата разводится за 60–90 с (допуск на проход BFS и паузы).
        var s = (PcbScene)SceneContract.Make(() => new PcbScene(), 192, 60, 12);
        int frames = 0;
        while (!s.Pouring && frames < 120 * 60) { s.Update(SceneContract.Dt); frames++; }
        double sec = frames / 60.0;
        _out.WriteLine($"первая плата разведена за {sec:F1} с, цепей {s.NetCount}");
        Assert.True(s.Pouring, "не разведена за 120 с");
        Assert.InRange(sec, 60, 90);
    }

    private static int TrackCells(PcbScene s)
    {
        int n = 0;
        for (int c = 0; c < s.TopMask.Length; c++)
            if (s.TopMask[c] != 0 || s.BottomMask[c] != 0) n++;
        return n;
    }

    private static int Dx(int i) => i switch { 1 or 2 or 3 => 1, 5 or 6 or 7 => -1, _ => 0 };
    private static int Dy(int i) => i switch { 3 or 4 or 5 => 1, 7 or 0 or 1 => -1, _ => 0 };

    private static SceneOptions Opts(string json) => new() { Params = JsonDocument.Parse(json).RootElement };

    private static double ChangedShare(CharGrid a, CharGrid b)
    {
        int n = 0;
        for (int i = 0; i < a.Cells.Length; i++)
            if (a.Cells[i].Ch != b.Cells[i].Ch || a.Cells[i].Fg != b.Cells[i].Fg || a.Cells[i].Bg != b.Cells[i].Bg) n++;
        return n / (double)a.Cells.Length;
    }
}
