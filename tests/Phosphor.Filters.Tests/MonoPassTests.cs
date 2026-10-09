using System.Diagnostics;
using System.Text.Json;
using Phosphor.Core;
using Phosphor.Filters;

namespace Phosphor.Filters.Tests;

public class MonoPassTests
{
    private const string DefaultRamp = " .:-=+*#%@";

    /// <summary>Фейковое покрытие: символы рампы — по порядку (0..1), '█' = 1, прочие — 0.5. Настоящее — в Phosphor.Text (GlyphAtlas).</summary>
    private sealed class FakeCoverage : IGlyphCoverage
    {
        // Таблица на 65536 символов: как и настоящий атлас, поиск — это просто чтение массива.
        private readonly float[] _table = new float[65536];

        public FakeCoverage()
        {
            Array.Fill(_table, 0.5f);
            for (int i = 0; i < DefaultRamp.Length; i++)
                _table[DefaultRamp[i]] = i / (float)(DefaultRamp.Length - 1);
            _table[' '] = 0f;
            _table['█'] = 1f;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public float Coverage(char c) => _table[c];
    }

    private static readonly FakeCoverage Cov = new();

    private static MonoPass Make(string json = "{}")
    {
        var p = new MonoPass();
        var w = new List<string>();
        p.Configure(JsonDocument.Parse(json).RootElement, w);
        Assert.Empty(w);
        return p;
    }

    private static CharGrid Gradient(int cols, int rows)
    {
        var g = new CharGrid(cols, rows);
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
            {
                byte v = (byte)(x * 255 / (cols - 1));
                g.Cells[y * cols + x] = new Cell(' ', Rgba.White, new Rgba(v, v, v));
            }
        return g;
    }

    [Fact]
    public void Id_IsMono()
    {
        Assert.Equal("mono", new MonoPass().Id);
    }

    [Fact]
    public void Gradient_SymbolsNonDecreasingLeftToRight_WithoutDither()
    {
        var pass = Make("{\"dither\":false}");
        var g = Gradient(64, 16);
        pass.Apply(g, Cov);

        for (int y = 0; y < g.Rows; y++)
        {
            float prev = -1f;
            for (int x = 0; x < g.Cols; x++)
            {
                float cov = Cov.Coverage(g[x, y].Ch);
                Assert.True(cov >= prev, $"({x},{y}) покрытие {cov} меньше предыдущего {prev}");
                prev = cov;
            }
        }
    }

    [Fact]
    public void Gradient_HasAtLeastSixDistinctSymbols()
    {
        var pass = Make("{\"dither\":false}");
        var g = Gradient(64, 16);
        pass.Apply(g, Cov);
        var distinct = g.Cells.Select(c => c.Ch).Distinct().Count();
        Assert.True(distinct >= 6, $"различных символов {distinct}");
    }

    [Fact]
    public void Gradient_AllColorsAreGray()
    {
        var pass = Make();
        var g = Gradient(64, 16);
        pass.Apply(g, Cov);
        foreach (var c in g.Cells)
        {
            Assert.True(c.Fg.R == c.Fg.G && c.Fg.G == c.Fg.B, $"Fg не серый: {c.Fg}");
            Assert.True(c.Bg.R == c.Bg.G && c.Bg.G == c.Bg.B, $"Bg не серый: {c.Bg}");
            Assert.Equal((byte)255, c.Fg.A);
            Assert.Equal((byte)255, c.Bg.A);
        }
    }

    [Fact]
    public void Text_IsPreservedAndFgIsGray()
    {
        const string text = "HELLO ПРИВЕТ 123";
        var g = new CharGrid(text.Length, 1);
        g.Text(0, 0, text, new Rgba(0, 255, 0), Rgba.Black);

        Make().Apply(g, Cov);

        var chars = new string(Enumerable.Range(0, text.Length).Select(x => g[x, 0].Ch).ToArray());
        Assert.Equal(text, chars);
        for (int x = 0; x < text.Length; x++)
        {
            var fg = g[x, 0].Fg;
            Assert.True(fg.R == fg.G && fg.G == fg.B, $"Fg в ({x},0) не серый: {fg}");
        }
    }

    [Fact]
    public void UpperHalfWhiteOverBlack_UsesFormSymbol()
    {
        var g = new CharGrid(1, 1);
        g.Put(0, 0, '▀', Rgba.White, Rgba.Black);

        Make().Apply(g, Cov);

        Assert.Contains(g[0, 0].Ch, "'`^\"");
    }

    [Fact]
    public void LowerHalfWhiteOverBlack_UsesLowerFormSymbol()
    {
        var g = new CharGrid(1, 1);
        g.Put(0, 0, '▀', Rgba.Black, Rgba.White);

        Make().Apply(g, Cov);

        Assert.Contains(g[0, 0].Ch, "._,");
    }

    [Fact]
    public void Deterministic_SameInputSameOutput()
    {
        var a = Gradient(120, 40);
        var b = Gradient(120, 40);
        var pass1 = Make();
        var pass2 = Make();
        pass1.Apply(a, Cov);
        pass2.Apply(b, Cov);
        Assert.Equal(a.Cells, b.Cells);

        // Повторный Apply на том же результате тоже даёт тот же вывод для одинакового входа.
        var c = Gradient(120, 40);
        pass1.Apply(c, Cov);
        Assert.Equal(a.Cells, c.Cells);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(480, 140)]
    public void NoExceptionsOnEdgeSizes(int cols, int rows)
    {
        var pass = Make();
        var g = new CharGrid(cols, rows);
        var glyphs = GlyphSet.All;
        for (int i = 0; i < g.Cells.Length; i++)
            g.Cells[i] = new Cell(glyphs[i % glyphs.Length], new Rgba((byte)(i * 7), (byte)(i * 13), (byte)(i * 29)),
                new Rgba((byte)(i * 3), (byte)(i * 5), (byte)(i * 11)));

        pass.Apply(g, Cov);
        pass.Apply(g, Cov);
    }

    [Fact]
    public void Apply1000TimesOn192x60IsFast()
    {
        var pass = Make();
        var g = Gradient(192, 60);
        pass.Apply(g, Cov); // прогрев

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) pass.Apply(g, Cov);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 300, $"1000 вызовов Apply заняли {sw.ElapsedMilliseconds} мс");
    }

    [Fact]
    public void Configure_UnknownKeyAndBadValuesWarnAndFallBack()
    {
        var pass = new MonoPass();
        var w = new List<string>();
        pass.Configure(JsonDocument.Parse("{\"id\":\"mono\",\"foo\":1,\"contrast\":\"x\",\"ramp\":\"ab\\u0001\",\"dither\":3}").RootElement, w);
        Assert.Equal(4, w.Count);

        // После кривой конфигурации поведение совпадает с значениями по умолчанию.
        var defaults = Make();
        var a = Gradient(64, 16);
        var b = Gradient(64, 16);
        pass.Apply(a, Cov);
        defaults.Apply(b, Cov);
        Assert.Equal(b.Cells, a.Cells);
    }

    [Fact]
    public void Configure_ValidRampIsUsed()
    {
        var pass = Make("{\"ramp\":\" #\",\"dither\":false}");
        var g = Gradient(64, 16);
        pass.Apply(g, Cov);
        Assert.All(g.Cells, c => Assert.Contains(c.Ch, " #"));
    }

    [Fact]
    public void TextOnDarkBlue_BgBecomesBlack_AndWaterNextToItIsSpace()
    {
        // Рыба на тёмно-синей воде: текст не должен оставлять плашку, соседняя вода — чистый пробел.
        var water = new Rgba(0x0A, 0x1E, 0x3C);
        const string fish = "><(((°>";
        var g = new CharGrid(fish.Length + 4, 1);
        for (int x = 0; x < g.Cols; x++) g.Put(x, 0, ' ', Rgba.Black, water);
        g.Text(2, 0, fish, new Rgba(0, 255, 0), water);

        Make().Apply(g, Cov);

        for (int i = 0; i < fish.Length; i++)
        {
            var c = g[2 + i, 0];
            Assert.Equal(fish[i], c.Ch);
            Assert.Equal(Rgba.Black, c.Bg);
        }
        for (int x = 0; x < g.Cols; x++)
        {
            if (x >= 2 && x < 2 + fish.Length) continue;
            Assert.Equal(' ', g[x, 0].Ch);
        }
    }

    [Fact]
    public void TextOnBrightBg_KeepsGrayBg()
    {
        var bright = new Rgba(0xC0, 0xC0, 0xC0);
        var g = new CharGrid(4, 1);
        g.Text(0, 0, "HI", Rgba.White, bright);

        Make().Apply(g, Cov);

        var bg = g[0, 0].Bg;
        Assert.True(bg.R == bg.G && bg.G == bg.B, $"Bg не серый: {bg}");
        Assert.InRange(bg.R, (byte)190, (byte)194);
    }

    [Fact]
    public void Configure_BlackParameterIsValidated()
    {
        var ok = new MonoPass();
        var w1 = new List<string>();
        ok.Configure(JsonDocument.Parse("{\"black\":0.5}").RootElement, w1);
        Assert.Empty(w1);

        foreach (var json in new[] { "{\"black\":1.0}", "{\"black\":-0.1}", "{\"black\":\"x\"}" })
        {
            var w = new List<string>();
            new MonoPass().Configure(JsonDocument.Parse(json).RootElement, w);
            Assert.Single(w);
        }
    }
}
