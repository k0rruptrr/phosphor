using Phosphor.Core;

// В каждом классе сцены есть тест на время кадра. Параллельный прогон классов делит ядра между тяжёлыми
// симуляциями и делает замеры бессмысленными (флапают) — поэтому сцены тестируются последовательно.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Phosphor.Scenes.Tests;

/// <summary>
/// Общие проверки контракта IScene. Каждая сцена ОБЯЗАНА проходить SceneContract.VerifyAll.
/// Не меняй этот файл — это эталон. Сценоспецифичные тесты пиши в своём файле.
/// </summary>
public static class SceneContract
{
    public const float Dt = 1f / 60f;

    public static void VerifyAll(Func<IScene> factory, string expectedId)
    {
        IdMatches(factory, expectedId);
        LongRunNoCrash(factory);
        DrawsSomething(factory);
        OnlyKnownGlyphs(factory);
        DrawIsPure(factory);
        ZeroDtFreezes(factory);
        Deterministic(factory);
        TinyAndHugeGrids(factory);
        ReInitResets(factory);
    }

    public static IScene Make(Func<IScene> f, int cols, int rows, int seed, SceneOptions? o = null)
    {
        var s = f();
        s.Init(cols, rows, new Random(seed), o ?? new SceneOptions());
        return s;
    }

    public static void Run(IScene s, int frames, float dt = Dt)
    {
        for (int i = 0; i < frames; i++) s.Update(dt);
    }

    public static CharGrid Snap(IScene s, int cols, int rows)
    {
        var g = new CharGrid(cols, rows);
        // Мусор в сетке: сцена обязана перерисовать всё.
        for (int i = 0; i < g.Cells.Length; i++) g.Cells[i] = new Cell('#', new Rgba(1, 2, 3), new Rgba(4, 5, 6));
        s.Draw(g);
        return g;
    }

    /// <summary>
    /// Текстовый дамп сетки для «осмотра» без GPU: символы как есть, секстели → '#',
    /// пустые ячейки с нечёрным фоном → '·'. Сохраняет в %TEMP%\phosphor-dump-&lt;name&gt;.txt и возвращает путь.
    /// Открой файл инструментом Read, чтобы увидеть вёрстку.
    /// </summary>
    public static string Dump(CharGrid g, string name)
    {
        var sb = new System.Text.StringBuilder();
        for (int y = 0; y < g.Rows; y++)
        {
            for (int x = 0; x < g.Cols; x++)
            {
                var c = g[x, y];
                sb.Append(GlyphSet.IsSextant(c.Ch) ? (c.Ch == (char)0xE000 ? ' ' : '#')
                    : c.Ch == ' ' && (c.Bg.R | c.Bg.G | c.Bg.B) > 24 ? '·' : c.Ch);
            }
            sb.Append('\n');
        }
        var path = Path.Combine(Path.GetTempPath(), $"phosphor-dump-{name}.txt");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    public static void IdMatches(Func<IScene> f, string id) => Assert.Equal(id, f().Id);

    public static void LongRunNoCrash(Func<IScene> f)
    {
        var s = Make(f, 120, 40, 1);
        var g = new CharGrid(120, 40);
        for (int i = 0; i < 3000; i++)
        {
            s.Update(i % 97 == 0 ? 0.1f : Dt); // иногда максимальный dt
            if (i % 10 == 0) s.Draw(g);
        }
    }

    public static void DrawsSomething(Func<IScene> f)
    {
        var s = Make(f, 120, 40, 2);
        Run(s, 300);
        var g = Snap(s, 120, 40);
        Assert.DoesNotContain(g.Cells, c => c.Ch == '#' && c.Fg == new Rgba(1, 2, 3) && c.Bg == new Rgba(4, 5, 6));
        bool visible = g.Cells.Any(c => (c.Ch != ' ' && (c.Fg.R | c.Fg.G | c.Fg.B) != 0) || (c.Bg.R | c.Bg.G | c.Bg.B) != 0);
        Assert.True(visible, "После 5 секунд сцена не нарисовала ничего видимого");
    }

    public static void OnlyKnownGlyphs(Func<IScene> f)
    {
        var s = Make(f, 120, 40, 3);
        for (int i = 0; i < 60; i++)
        {
            Run(s, 10);
            var g = Snap(s, 120, 40);
            var bad = g.Cells.Select(c => c.Ch).Where(c => !GlyphSet.Contains(c)).Distinct().ToArray();
            Assert.True(bad.Length == 0, $"Символы вне GlyphSet: {string.Join(" ", bad.Select(c => $"U+{(int)c:X4}"))}");
        }
    }

    public static void DrawIsPure(Func<IScene> f)
    {
        var s = Make(f, 80, 25, 4);
        Run(s, 200);
        var a = Snap(s, 80, 25); var b = Snap(s, 80, 25);
        Assert.Equal(a.Cells, b.Cells);
    }

    public static void ZeroDtFreezes(Func<IScene> f)
    {
        var s = Make(f, 80, 25, 5);
        Run(s, 200);
        var a = Snap(s, 80, 25);
        Run(s, 50, 0f);
        var b = Snap(s, 80, 25);
        Assert.Equal(a.Cells, b.Cells);
    }

    public static void Deterministic(Func<IScene> f)
    {
        var s1 = Make(f, 100, 30, 77); var s2 = Make(f, 100, 30, 77);
        Run(s1, 500); Run(s2, 500);
        Assert.Equal(Snap(s1, 100, 30).Cells, Snap(s2, 100, 30).Cells);
    }

    public static void TinyAndHugeGrids(Func<IScene> f)
    {
        foreach (var (c, r) in new[] { (1, 1), (2, 1), (1, 2), (3, 2), (7, 5), (480, 140) })
        {
            var s = Make(f, c, r, 6);
            Run(s, 400);
            Snap(s, c, r);
        }
    }

    public static void ReInitResets(Func<IScene> f)
    {
        var s = Make(f, 100, 30, 8);
        Run(s, 300);
        s.Init(60, 20, new Random(8), new SceneOptions());
        Run(s, 300);
        Snap(s, 60, 20);
        // После повторного Init с тем же seed результат = свежая сцена
        var fresh = Make(f, 60, 20, 8);
        Run(fresh, 300);
        Assert.Equal(Snap(fresh, 60, 20).Cells, Snap(s, 60, 20).Cells);
    }
}
