using System.Text.Json;
using Phosphor.Core;
using Phosphor.Gfx;

namespace Phosphor.Filters.Tests;

/// <summary>Ясность текста (#247): на текстовых сценах артефактные параметры (ParamSpec.Clean) подтягиваются к чистому концу.</summary>
public class TextClarityTests
{
    private const int W = 320, H = 180;

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, float>> Resolve(GpuHarness gpu, float scale)
    {
        var reg = new FilterRegistry();
        reg.Register("vhs", () => new VhsFilter());
        reg.Register("tv2", () => new Tv2Filter());
        using var doc = JsonDocument.Parse("""
            [ { "id": "vhs", "noise": 0.5, "badTracking": 0.35, "lumaRes": 520, "saturation": 0.85 },
              { "id": "tv2", "mode": "pal", "blanking": 1, "amount": 0.8 } ]
            """);
        using var chain = new FilterChain(gpu.Gl, reg, doc.RootElement, W, H);
        chain.Process(gpu.InputTexture, new FrameInfo(0f, 1f / 60f, 0, W, H, 0, AudioFrame.Silent, default, scale));
        return chain.Snapshot();
    }

    [Fact]
    public void TextScene_ScalesArtifactsTowardClean_KeepsStyle()
    {
        using var gpu = new GpuHarness(W, H);
        var s = Resolve(gpu, 0.4f);
        Assert.Equal(0.2f, s["vhs"]["noise"], 3);
        Assert.Equal(1920f + (520f - 1920f) * 0.4f, s["vhs"]["lumaRes"], 1);   // «чисто» у разрешения — максимум
        Assert.Equal(0f, s["vhs"]["badTracking"]);                              // TextOff: полоса стирает строку целиком
        Assert.Equal(0.85f, s["vhs"]["saturation"], 3);                         // стиль не трогаем
        Assert.Equal(0f, s["tv2"]["blanking"]);                                 // гашение сдвигало кадр и съедало низ
        Assert.Equal(0.32f, s["tv2"]["amount"], 3);
        Assert.Equal(1f, s["tv2"]["mode"]);
    }

    [Fact]
    public void OtherScenes_Untouched()
    {
        using var gpu = new GpuHarness(W, H);
        var s = Resolve(gpu, 1f);
        Assert.Equal(0.5f, s["vhs"]["noise"], 3);
        Assert.Equal(0.35f, s["vhs"]["badTracking"], 3);
        Assert.Equal(520f, s["vhs"]["lumaRes"], 1);
        Assert.Equal(1f, s["tv2"]["blanking"]);
    }

    [Fact]
    public void CleanValuesAreInsideRange()
    {
        IFilter[] filters = { new VhsFilter(), new TvFilter(), new Tv2Filter(), new CrtFilter(), new ChromaFilter(), new PhosphorFilter() };
        foreach (var f in filters)
        foreach (var p in f.Params)
        {
            if (p.Clean is float c) Assert.InRange(c, p.Min, p.Max);
            Assert.False(p.TextOff && p.Clean is null, $"{f.Id}.{p.Name}: TextOff без Clean");
        }
    }
}
