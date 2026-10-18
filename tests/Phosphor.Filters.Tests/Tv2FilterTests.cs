using Phosphor.Filters;
using Phosphor.Gfx;

namespace Phosphor.Filters.Tests;

/// <summary>
/// tv2: испытательные строки I–IV (VITS) в кадровом гашении. Высота 288 = строки поля один в один:
/// при roll = 0 и blanking = 1 строка изображения y — это строка поля f = y (КГИ — 0..23, VITS — 17..20).
/// Время на строке t = 10,5 + 52·x мкс (x — доля ширины).
/// </summary>
public class Tv2FilterTests
{
    private const int W = 1440;
    private const int H = 288;

    private static Dictionary<string, float> P(float mode, float vits, float amount = 1f, float blanking = 1f) =>
        new() { ["mode"] = mode, ["vits"] = vits, ["amount"] = amount, ["blanking"] = blanking, ["noise"] = 0f, ["roll"] = 0f };

    private static int X(double tUs) => (int)Math.Round((tUs - 10.5) / 52.0 * W);

    private static double MeanLuma(float[,,] img, int row, double t0, double t1)
    {
        double s = 0; int n = 0;
        for (int x = X(t0); x < X(t1); x++) { s += GpuHarness.Luma(img, x, row); n++; }
        return s / n;
    }

    private static double StdLuma(float[,,] img, int row, double t0, double t1)
    {
        double m = MeanLuma(img, row, t0, t1), s = 0; int n = 0;
        for (int x = X(t0); x < X(t1); x++) { double d = GpuHarness.Luma(img, x, row) - m; s += d * d; n++; }
        return Math.Sqrt(s / n);
    }

    private static (double r, double g, double b) MeanRgb(float[,,] img, int row, double t0, double t1)
    {
        double r = 0, g = 0, b = 0; int n = 0;
        for (int x = X(t0); x < X(t1); x++) { r += img[row, x, 0]; g += img[row, x, 1]; b += img[row, x, 2]; n++; }
        return (r / n, g / n, b / n);
    }

    private static void Gray(GpuHarness gpu) => gpu.SetInput((x, y) => (0.4f, 0.4f, 0.4f));

    [Fact]
    public void Params_VitsIsAToggleOnByDefault()
    {
        var p = new Tv2Filter().Params.Single(s => s.Name == "vits");
        Assert.Equal(1f, p.Default);
        Assert.Equal(0f, p.Min);
        Assert.Equal(1f, p.Max);
    }

    [Fact]
    public void Shader_PassesStaticChecks()
    {
        var src = ShaderStatic.Load("Phosphor.Filters.Shaders.tv2.frag");
        ShaderStatic.CheckSyntax(src, "tv2.frag");
        ShaderStatic.CheckParams(new Tv2Filter(), src);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    public void Vits_ChangesOnlyBlankingRows17To22(float mode)
    {
        using var gpu = new GpuHarness(W, H);
        Gray(gpu);
        using var f = gpu.Create<Tv2Filter>();
        var off = gpu.Run(f, 5, P(mode, 0f));
        var on = gpu.Run(f, 5, P(mode, 1f));
        double inside = 0;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        for (int c = 0; c < 3; c++)
        {
            double d = Math.Abs(off[y, x, c] - on[y, x, c]);
            if (y >= 17 && y <= 22) inside = Math.Max(inside, d);
            else Assert.True(d <= 2.0 / 255, $"строка {y}, x {x}: VITS меняет строку вне 17–22 (Δ {d * 255:F1}/255)");
        }
        Assert.True(inside > 0.3, $"VITS не виден в строках 17–22 (max Δ {inside:F3})");
    }

    [Fact]
    public void Vits_InvisibleWithoutBlanking()
    {
        using var gpu = new GpuHarness(W, H);
        Gray(gpu);
        using var f = gpu.Create<Tv2Filter>();
        var off = gpu.Run(f, 5, P(1f, 0f, blanking: 0f));
        var on = gpu.Run(f, 5, P(1f, 1f, blanking: 0f));
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        for (int c = 0; c < 3; c++)
            Assert.True(Math.Abs(off[y, x, c] - on[y, x, c]) <= 2.0 / 255, $"строка {y}: без КГИ VITS не должен ничего менять");
    }

    [Fact]
    public void LineI_WhiteBarThenStaircaseRises()
    {
        using var gpu = new GpuHarness(W, H);
        Gray(gpu);
        using var f = gpu.Create<Tv2Filter>();
        var img = gpu.Run(f, 0, P(1f, 1f, amount: 0f));
        const int row = 17;
        double black = MeanLuma(img, row, 36.5, 39.5);
        double bar = MeanLuma(img, row, 14, 20);
        Assert.True(bar - black > 0.6, $"B2: белая площадка {bar:F3} не выше чёрного {black:F3}");
        double prev = black;
        foreach (double t in new[] { 41.0, 45.0, 49.0, 53.0, 57.0 })
        {
            double v = MeanLuma(img, row, t, t + 2);
            Assert.True(v - prev > 0.08, $"D1: ступень в {t} мкс ({v:F3}) не выше предыдущей ({prev:F3})");
            prev = v;
        }
        Assert.True(Math.Abs(prev - bar) < 0.08, $"D1: верх лесенки {prev:F3} не на уровне белого {bar:F3}");
    }

    [Fact]
    public void LineII_HighFrequencyPacketsAreAttenuated()
    {
        using var gpu = new GpuHarness(W, H);
        Gray(gpu);
        using var f = gpu.Create<Tv2Filter>();
        var img = gpu.Run(f, 0, P(1f, 1f, amount: 1f));
        const int row = 19; // сигнал II
        double low = StdLuma(img, row, 25, 29);     // 0,5 МГц
        double high = StdLuma(img, row, 55, 59);    // 5,8 МГц
        Assert.True(low > 0.12, $"пакет 0,5 МГц не виден: σ {low:F3}");
        Assert.True(high < 0.6 * low, $"пакет 5,8 МГц не ослаблен ФНЧ: σ {high:F3} при 0,5 МГц {low:F3}");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    public void LineIII_SubcarrierOnStaircaseDecodesToColor(float mode)
    {
        using var gpu = new GpuHarness(W, H);
        Gray(gpu);
        using var f = gpu.Create<Tv2Filter>();
        var img = gpu.Run(f, 0, P(mode, 1f));
        var (r, _, b) = MeanRgb(img, 18, 45, 51); // сигнал III, строка Db у SECAM: синий
        Assert.True(b - r > 0.1, $"mode {mode}: поднесущая на лесенке не даёт цвета (r {r:F3}, b {b:F3})");
    }

    [Fact]
    public void Secam_4MHzPacketFoolsTheDiscriminator()
    {
        using var gpu = new GpuHarness(W, H);
        Gray(gpu);
        using var f = gpu.Create<Tv2Filter>();
        var on = gpu.Run(f, 0, P(0f, 1f));
        // Сигнал II — строка Dr: 4,0 МГц ниже покоя 4,406 → R−Y > 0, красный; пакет 1 МГц вне полосы — свой Dr нулевой
        // (Db в обоих местах приходит с линии задержки — лесенка III с поднесущей)
        var (r4, g4, _) = MeanRgb(on, 19, 44, 47);
        var (r1, g1, _) = MeanRgb(on, 19, 31, 35);
        Assert.True((r4 - g4) - (r1 - g1) > 0.15, $"пакет 4 МГц не красный: r−g {r4 - g4:F3}, у пакета 1 МГц {r1 - g1:F3}");
    }
}
