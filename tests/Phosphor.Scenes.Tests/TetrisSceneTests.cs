using System.Diagnostics;
using System.Text.Json;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class TetrisSceneTests
{
    private readonly ITestOutputHelper _out;

    public TetrisSceneTests(ITestOutputHelper output) => _out = output;

    private static SceneOptions Opts(string json) => new() { Params = JsonDocument.Parse(json).RootElement };

    private static TetrisScene Make(int cols, int rows, int seed, string json = "{}") =>
        (TetrisScene)SceneContract.Make(() => new TetrisScene(), cols, rows, seed, Opts(json));

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new TetrisScene(), "tetris");

    [Fact]
    public void NoMusicIsAlive()
    {
        var s = SceneContract.Make(() => new TetrisScene(), 120, 40, 81);
        SceneContract.Run(s, 60);
        var a = SceneContract.Snap(s, 120, 40);
        SceneContract.Run(s, 180);                       // 3 с без музыки
        var b = SceneContract.Snap(s, 120, 40);
        double share = ChangedShare(a, b);
        _out.WriteLine($"без музыки за 3 с изменилось {share:P2} ячеек");
        Assert.True(share > 0.005, $"за 3 с изменилось лишь {share:P2} ячеек");
    }

    [Fact]
    public void UpdateAndDrawAre_Fast_On192x60()
    {
        var s = SceneContract.Make(() => new TetrisScene(), 192, 60, 31);
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
    public void AiWithoutMistakesClearsOver30LinesIn3Minutes()
    {
        var s = Make(120, 40, 11, "{\"mistakes\":0}");
        for (int i = 0; i < 180 * 60; i++) s.Update(SceneContract.Dt);
        _out.WriteLine($"за 3 мин без ошибок ИИ: линий {s.Lines}, очков {s.Score}, фигур {s.Locks}");
        Assert.False(s.GameOver, "за 3 минуты ИИ без ошибок проиграл");
        Assert.True(s.Lines > 30, $"снято лишь {s.Lines} линий за 3 минуты");
    }

    [Fact]
    public void EveryBagOfSevenHoldsAllSevenKinds()
    {
        var s = Make(80, 25, 12);
        s.ResetBagForTest();
        for (int bag = 0; bag < 200; bag++)
        {
            var seen = new HashSet<int>();
            for (int i = 0; i < 7; i++) seen.Add(s.NextPieceForTest());
            Assert.True(seen.SetEquals(new[] { 1, 2, 3, 4, 5, 6, 7 }), $"в мешке {bag} нет полного набора: {string.Join(",", seen)}");
        }
    }

    [Fact]
    public void PiecesStayInWell_AndFullRowsDisappear()
    {
        const int cols = 120, rows = 40;
        var s = Make(cols, rows, 13, "{\"mistakes\":0}");
        var g = new CharGrid(cols, rows);
        int wx = s.WellX, wy = s.WellY;
        int wellH = 20;
        int whiteRowsChecked = 0;

        for (int f = 0; f < 60 * 60; f++)
        {
            s.Update(SceneContract.Dt);
            if (f % 3 != 0) continue;
            s.Draw(g);

            // Блоки не заходят в стенки: левее и правее стакана ничего не нарисовано.
            for (int y = wy; y < wy + wellH; y++)
            for (int x = wx - 4; x <= wx; x++)
                Assert.False(IsBlock(g[x, y].Ch), $"блок у левой стенки в ({x},{y}) на кадре {f}");
            for (int y = wy; y < wy + wellH; y++)
            for (int x = wx + 21; x <= wx + 24; x++)
                Assert.False(IsBlock(g[x, y].Ch), $"блок у правой стенки в ({x},{y}) на кадре {f}");

            // Полная строка на поле существует только во время мигания, и тогда она нарисована белой.
            if (s.Clearing)
            {
                int white = 0;
                for (int y = wy; y < wy + wellH; y++)
                {
                    int cnt = 0;
                    for (int x = wx + 1; x <= wx + 20; x++)
                        if (g[x, y].Ch == '█' && g[x, y].Fg == Rgba.White) cnt++;
                    if (cnt == 20) white++;
                }
                Assert.Equal(s.FullRowsOnBoard, white);
                whiteRowsChecked++;
            }
            else
            {
                Assert.Equal(0, s.FullRowsOnBoard);
            }
        }

        _out.WriteLine($"за 60 с снято линий {s.Lines}, кадров с миганием проверено {whiteRowsChecked}");
        Assert.True(s.Lines > 0, "за 60 с ни одной линии не снято");
    }

    [Fact]
    public void BeatDropsAlignedPiece()
    {
        // Две одинаковые игры: в одной фигуру ударяем, в другой нет. Фигура ≥ 2 строк над целью.
        var a = Make(120, 40, 14, "{\"mistakes\":0}");
        var b = Make(120, 40, 14, "{\"mistakes\":0}");
        for (int f = 0; f < 600 * 60 && !(a.PieceAligned && a.RowsToLanding >= 2); f++)
        {
            a.Update(SceneContract.Dt);
            b.Update(SceneContract.Dt);
        }
        Assert.True(a.PieceAligned && a.RowsToLanding >= 2, "не дождались фигуры над целью");
        Assert.True(b.PieceAligned && b.RowsToLanding >= 2, "игры разошлись");

        int la = a.Locks, lb = b.Locks;
        a.OnAudio(new AudioFrame(Active: true, Level: 0.5f, Bass: 0.5f, Mid: 0f, High: 0f,
            Beat: true, BeatPulse: 1f, Bpm: 120f));
        a.Update(SceneContract.Dt);
        b.Update(SceneContract.Dt);
        _out.WriteLine($"после удара: локов с ударом {a.Locks - la}, без удара {b.Locks - lb}");
        Assert.Equal(la + 1, a.Locks);
        Assert.Equal(lb, b.Locks);
    }

    [Fact]
    public void Param_Level_MinAndMaxDiffer() =>
        AssertParamDiffers("{\"level\":1}", "{\"level\":15}", "level");

    [Fact]
    public void Param_Mistakes_MinAndMaxDiffer() =>
        AssertParamDiffers("{\"mistakes\":0}", "{\"mistakes\":0.5}", "mistakes");

    [Fact]
    public void Param_Ghost_MinAndMaxDiffer() =>
        AssertParamDiffers("{\"ghost\":false}", "{\"ghost\":true}", "ghost");

    private void AssertParamDiffers(string lo, string hi, string name)
    {
        var a = Make(120, 40, 21, lo);
        var b = Make(120, 40, 21, hi);
        SceneContract.Run(a, 120);
        SceneContract.Run(b, 120);
        var ga = SceneContract.Snap(a, 120, 40);
        var gb = SceneContract.Snap(b, 120, 40);
        int changed = 0;
        for (int i = 0; i < ga.Cells.Length; i++)
            if (ga.Cells[i].Ch != gb.Cells[i].Ch || ga.Cells[i].Fg != gb.Cells[i].Fg || ga.Cells[i].Bg != gb.Cells[i].Bg) changed++;
        _out.WriteLine($"параметр {name}: через 120 кадров отличается {changed} ячеек");
        Assert.True(changed > 0, $"параметр {name}: Min и Max дают одну картинку через 120 кадров");
    }

    private static bool IsBlock(char ch) => ch == '█' || ch == '░';

    private static double ChangedShare(CharGrid a, CharGrid b)
    {
        int n = 0;
        for (int i = 0; i < a.Cells.Length; i++)
            if (a.Cells[i].Fg != b.Cells[i].Fg || a.Cells[i].Bg != b.Cells[i].Bg) n++;
        return n / (double)a.Cells.Length;
    }
}
