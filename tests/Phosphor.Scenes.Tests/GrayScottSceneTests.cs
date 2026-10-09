using System.Diagnostics;
using System.IO;
using Phosphor.Core;
using Phosphor.Scenes;

namespace Phosphor.Scenes.Tests;

public sealed class GrayScottSceneTests
{
    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new GrayScottScene(), "grayscott");

    [Fact]
    public void PatternAppearsAndSurvivesTenSeconds()
    {
        var s = New(96, 30, 1);
        SceneContract.Run(s, 600);
        float cov = s.CoverageAbove(0.1f);
        Assert.True(cov >= 0.05f && cov <= 0.8f, $"через 10 с доля B>0.1 = {cov:P1}, ожидалось 5..80%");
    }

    [Fact]
    public void PatternStillPresentAfterSixtySeconds()
    {
        var s = New(96, 30, 1);
        SceneContract.Run(s, 3600);
        float cov = s.CoverageAbove(0.1f);
        Assert.InRange(cov, 0.02f, 0.8f);
    }

    [Fact]
    public void ParametersDriftOverHundredSeconds()
    {
        var s = New(96, 30, 3);
        float f0 = s.F, k0 = s.K;
        SceneContract.Run(s, 6000);
        Assert.NotEqual(f0, s.F);
        Assert.NotEqual(k0, s.K);
    }

    [Fact]
    public void BeatDropRaisesHighDensityCoverageNextFrame()
    {
        var s = New(96, 30, 2);
        SceneContract.Run(s, 600);
        float before = s.CoverageAbove(0.5f);

        s.OnAudio(new AudioFrame(true, 0.5f, 0.5f, 0.5f, 0.5f, true, 1f, 120f));
        s.Update(SceneContract.Dt);

        float after = s.CoverageAbove(0.5f);
        Assert.True(after > before, $"после удара покрытие B>0.5 не выросло: {before} → {after}");
    }

    [Fact]
    public void UpdateAndDrawAreFastOn192x60()
    {
        var s = New(192, 60, 5);
        var g = new CharGrid(192, 60);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 600; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1200, $"600 кадров заняли {sw.ElapsedMilliseconds} мс");
    }

    [Fact]
    public void DumpAfterTwentySecondsForEye()
    {
        var s = New(96, 30, 4);
        SceneContract.Run(s, 1200);
        var g = SceneContract.Snap(s, 96, 30);
        string path = SceneContract.Dump(g, "grayscott");
        Assert.True(File.Exists(path), $"дамп не записан: {path}");
    }

    /// <summary>Регресс: 40 сидов на 192×60, 10 с. Поле не должно вымирать (чёрный экран) и быть совсем тусклым.</summary>
    [Fact]
    public void NoSeedDiesWithinTenSeconds()
    {
        const int cols = 192, rows = 60, frames = 600, firstCheck = 240; // проверка черноты — с 4 с
        const int seeds = 40;
        var g = new CharGrid(cols, rows);
        var means = new float[seeds];
        var blackAt = new int[seeds];
        for (int seed = 0; seed < seeds; seed++)
        {
            var s = New(cols, rows, seed);
            blackAt[seed] = -1;
            for (int i = 1; i <= frames; i++)
            {
                s.Update(SceneContract.Dt);
                if (i < firstCheck) continue;
                s.Draw(g);
                if (blackAt[seed] < 0 && IsBlack(g)) blackAt[seed] = i;
            }
            means[seed] = MeanLuma(g); // последний кадр
        }

        float refMean = means[1];
        var failing = new List<string>();
        for (int seed = 0; seed < seeds; seed++)
        {
            bool dark = means[seed] < 0.1f * refMean;
            bool black = blackAt[seed] >= 0;
            if (!dark && !black) continue;
            string why = black ? $"чёрный кадр {blackAt[seed]} (t={blackAt[seed] / 60f:F1} с)" : "";
            if (dark) why += $"{(black ? ", " : "")}яркость {means[seed]:F4} < 10% от seed 1 ({refMean:F4})";
            failing.Add($"seed {seed}: {why}");
        }
        Assert.True(failing.Count == 0, $"провалилось {failing.Count} из {seeds} сидов: {string.Join("; ", failing)}");
    }

    /// <summary>
    /// Каталог режимов в реальной сцене (AdvanceRegime, выдержка и переход 8 с), 10 минут на 96×30, seed 1..5.
    /// Плохая секунда: std(B) &lt; 0.03 или покрытие B&gt;0.1 &gt; 0.95 или &lt; 0.02. Серия плохих секунд не длиннее 4 с.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void RegimeCatalogStaysAliveInLongRun()
    {
        const int frames = 36000; // 10 мин при dt 1/60
        var failures = new List<string>();
        for (int seed = 1; seed <= 5; seed++)
        {
            var s = New(96, 30, seed);
            int run = 0, maxRun = 0, maxRunAt = 0;
            for (int i = 1; i <= frames; i++)
            {
                s.Update(SceneContract.Dt);
                if (i % 60 != 0) continue;
                float std = s.FieldStdB(), cov = s.CoverageAbove(0.1f);
                bool bad = std < 0.03f || cov > 0.95f || cov < 0.02f;
                run = bad ? run + 1 : 0;
                if (run > maxRun) { maxRun = run; maxRunAt = i / 60; }
            }
            if (maxRun > 4) failures.Add($"seed {seed}: серия плохих секунд {maxRun} с (до t={maxRunAt} с)");
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    private static float Luma(Rgba c) => (0.299f * c.R + 0.587f * c.G + 0.114f * c.B) / 255f;

    private static float MeanLuma(CharGrid g)
    {
        float sum = 0f;
        foreach (var c in g.Cells) sum += Luma(c.Fg) + Luma(c.Bg);
        return sum / (2f * g.Cells.Length);
    }

    /// <summary>Все ячейки Fg и Bg с luma &lt; 0.02.</summary>
    private static bool IsBlack(CharGrid g)
    {
        foreach (var c in g.Cells)
            if (Luma(c.Fg) >= 0.02f || Luma(c.Bg) >= 0.02f) return false;
        return true;
    }

    private static GrayScottScene New(int cols, int rows, int seed, SceneOptions? options = null)
    {
        var s = new GrayScottScene();
        s.Init(cols, rows, new Random(seed), options ?? new SceneOptions());
        return s;
    }
}
