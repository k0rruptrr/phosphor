using System.Diagnostics;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

public sealed class AquariumSceneTests
{
    private readonly ITestOutputHelper _out;

    public AquariumSceneTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new AquariumScene(), "aquarium");

    [Fact]
    public void After2sShowsFishAndBubbles()
    {
        var s = SceneContract.Make(() => new AquariumScene(), 120, 40, 41);
        SceneContract.Run(s, 120);
        var g = SceneContract.Snap(s, 120, 40);

        Assert.Contains(g.Cells, c => c.Ch == '>' || c.Ch == '<');
        Assert.True(((AquariumScene)s).BubbleCount > 0, "пузырей нет");

        // Символы пузырей в толще воды (строки 1..sandTop-1). '.' и '°' есть и у рыб/песка, поэтому
        // основную проверку делает BubbleCount выше.
        int sandTop = 40 - 2;
        bool bubbleGlyph = false;
        for (int y = 1; y < sandTop && !bubbleGlyph; y++)
            for (int x = 0; x < 120; x++)
            {
                char ch = g[x, y].Ch;
                if (ch is 'o' or 'O' or '°' or '.') { bubbleGlyph = true; break; }
            }
        Assert.True(bubbleGlyph, "в толще воды нет символов пузырей");
    }

    [Fact]
    public void WaterBackgroundIsBlue()
    {
        var s = SceneContract.Make(() => new AquariumScene(), 120, 40, 44);
        SceneContract.Run(s, 60);
        var g = SceneContract.Snap(s, 120, 40);
        int blue = g.Cells.Count(c => c.Bg.B > 10);
        Assert.True(blue > g.Cells.Length * 0.9, $"фон не синий: {blue} из {g.Cells.Length}");
    }

    [Fact]
    public void MirrorFlipsSpriteAndPairs()
    {
        Assert.Equal("<°)))><", AquariumScene.Mirror("><(((°>"));
        Assert.Equal("><>", AquariumScene.Mirror("<><"));
    }

    [Fact]
    public void FishDoNotPileIntoOneColumnNearShark()
    {
        // Раньше рыбы у акулы копились на границе 20 колонок и двигались вертикальной колонкой.
        // Проверяем с 60 по 120 с на двух seed: в одной колонке не бывает 5 и более рыб.
        foreach (int seed in new[] { 1, 2 })
        {
            var s = SceneContract.Make(() => new AquariumScene(), 192, 60, seed);
            var a = (AquariumScene)s;
            for (int sec = 1; sec <= 120; sec++)
            {
                SceneContract.Run(s, 60);
                if (sec < 60) continue;
                int max = a.FishPositions().GroupBy(p => (int)Math.Floor(p.x)).Max(g => g.Count());
                Assert.True(max < 5, $"seed {seed}, t={sec} с: в одной колонке {max} рыб");
            }
        }
    }

    [Fact]
    public void BubblesRiseOverHalfSecond()
    {
        // Сразу после удара в толще воды почти нет других пузырей: средняя Y — у дна.
        // Новые пузыри рождаются у дна, где средняя уже стоит, и почти не сдвигают её.
        var s = SceneContract.Make(() => new AquariumScene(), 120, 40, 42);
        var a = (AquariumScene)s;
        ((IAudioReactive)s).OnAudio(Beat(0f));
        s.Update(SceneContract.Dt);
        float before = a.AverageBubbleY();
        Assert.False(float.IsNaN(before), "после удара пузырей нет");

        SceneContract.Run(s, 30); // 0.5 с
        float after = a.AverageBubbleY();
        Assert.True(after < before - 1f, $"пузыри не поднялись: средняя Y {before:F2} -> {after:F2}");
    }

    [Fact]
    public void BeatAddsBubblesOnNextFrame()
    {
        var s = SceneContract.Make(() => new AquariumScene(), 120, 40, 43);
        var a = (AquariumScene)s;
        s.Update(SceneContract.Dt);
        int before = a.BubbleCount;

        ((IAudioReactive)s).OnAudio(Beat(0f));
        s.Update(SceneContract.Dt);

        Assert.True(a.BubbleCount >= before + 5, $"удар не добавил пузыри: {before} -> {a.BubbleCount}");
    }

    [Fact]
    public void UpdateAndDrawAre_Fast_On240x70()
    {
        var s = SceneContract.Make(() => new AquariumScene(), 240, 70, 31);
        var g = new CharGrid(240, 70);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 600; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
        }
        sw.Stop();
        _out.WriteLine($"600 кадров Update+Draw 240x70: {sw.Elapsed.TotalMilliseconds:F1} мс");
        Assert.True(sw.ElapsedMilliseconds < 600, $"600 кадров заняли {sw.ElapsedMilliseconds} мс");
    }

    private static AudioFrame Beat(float bass) =>
        new(Active: true, Level: 0.5f, Bass: bass, Mid: 0f, High: 0f, Beat: true, BeatPulse: 1f, Bpm: 120f);
}
