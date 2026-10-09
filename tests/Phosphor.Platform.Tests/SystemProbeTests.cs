using Phosphor.Core;
using Phosphor.Platform;
using Xunit;
using Xunit.Abstractions;

namespace Phosphor.Platform.Tests;

public class SystemProbeTests
{
    private readonly ITestOutputHelper _out;

    public SystemProbeTests(ITestOutputHelper output) => _out = output;

    private static SystemSnapshot Snap(SystemProbe p)
    {
        var live = p.TryGetLive();
        Assert.NotNull(live);
        return Assert.IsType<SystemSnapshot>(live!.Value.Snap);
    }

    [Fact]
    public void NoSnapshot_BeforeStart()
    {
        using var p = new SystemProbe();
        Assert.Null(p.TryGetLive());
    }

    [Fact]
    public void StartStop_NoExceptions_DisposeTwiceIsSafe()
    {
        var p = new SystemProbe();
        p.Start();
        p.Start(); // повторный Start — no-op
        p.Dispose();
        p.Dispose();
    }

    [Fact]
    public void Snapshot_AvailableAfterOneAndHalfSeconds_WithSaneRanges()
    {
        using var p = new SystemProbe();
        p.Start();
        Thread.Sleep(1200);
        var s = Snap(p);

        Assert.Equal(Environment.ProcessorCount, s.CorePct.Length);
        Assert.InRange(s.CpuPct, 0f, 100f);
        foreach (var c in s.CorePct) Assert.InRange(c, 0f, 100f);
        Assert.True(s.RamTotalGb > 0, "RamTotalGb > 0");
        Assert.InRange(s.RamUsedGb, 0, s.RamTotalGb);
        Assert.True(float.IsNaN(s.GpuPct) || (s.GpuPct >= 0f && s.GpuPct <= 100f), $"GpuPct {s.GpuPct}");
        Assert.True(s.NetDownBps >= 0 && s.NetUpBps >= 0);
        Assert.True(s.DiskReadBps >= 0 && s.DiskWriteBps >= 0);
        Assert.Equal(Environment.MachineName, s.Host);
        Assert.StartsWith("WINDOWS ", s.Os);
        Assert.True(s.Uptime > TimeSpan.Zero);
        Assert.True(s.Processes > 0);
    }

    [Fact]
    public void Version_GrowsWithEachSample()
    {
        using var p = new SystemProbe();
        p.SampleNow();
        long v1 = p.TryGetLive()!.Value.Version;
        p.SampleNow();
        long v2 = p.TryGetLive()!.Value.Version;
        Assert.True(v2 > v1);
    }

    [Fact]
    public void Sample_AfterDispose_IsNoOp()
    {
        var p = new SystemProbe();
        p.SampleNow();
        long v = p.TryGetLive()!.Value.Version;
        p.Dispose();
        p.SampleNow();
        Assert.Equal(v, p.TryGetLive()!.Value.Version);
    }

    [Fact]
    [Trait("Category", "Diagnostics")]
    public void Diagnostics_PrintRealNumbers()
    {
        using var p = new SystemProbe(m => _out.WriteLine("LOG: " + m));
        p.Start();
        Thread.Sleep(1200);
        for (int i = 0; i < 3; i++)
        {
            Thread.Sleep(500);
            var s = Snap(p);
            _out.WriteLine($"{s.At:HH:mm:ss.fff} host={s.Host} os={s.Os} uptime={s.Uptime:hh\\:mm\\:ss} procs={s.Processes}");
            _out.WriteLine($"CPU total={s.CpuPct:F1}% cores=[{string.Join(", ", s.CorePct.Select(c => c.ToString("F1")))}]");
            _out.WriteLine($"RAM used={s.RamUsedGb:F2} GB / total={s.RamTotalGb:F2} GB");
            _out.WriteLine($"GPU {s.GpuName ?? "?"} = {s.GpuPct:F1}%");
            _out.WriteLine($"NET down={s.NetDownBps:F0} B/s up={s.NetUpBps:F0} B/s");
            _out.WriteLine($"DISK read={s.DiskReadBps:F0} B/s write={s.DiskWriteBps:F0} B/s");
        }
    }
}
