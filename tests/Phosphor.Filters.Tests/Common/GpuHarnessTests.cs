namespace Phosphor.Filters.Tests;

public class GpuHarnessTests
{
    [Fact]
    public void Crt_RunsOnRealGpu_AndKeepsOrientation()
    {
        using var gpu = new GpuHarness(64, 48);
        // Верхняя половина белая, нижняя чёрная — после фильтра верх должен остаться светлее низа
        gpu.SetInput((x, y) => y < 24 ? (1f, 1f, 1f) : (0f, 0f, 0f));
        using var crt = gpu.Create<CrtFilter>();
        var img = gpu.Run(crt, 0);
        float top = 0, bottom = 0;
        for (int x = 16; x < 48; x++) { top += GpuHarness.Luma(img, x, 12); bottom += GpuHarness.Luma(img, x, 36); }
        Assert.True(top > bottom + 5, $"верх {top:F2}, низ {bottom:F2}");
    }
}
