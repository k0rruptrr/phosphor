using System.Diagnostics;
using Phosphor.Core;
using Phosphor.Filters;
using Phosphor.Gfx;
using Silk.NET.OpenGL;

namespace Phosphor.Filters.Tests;

public class PixelsortFilterTests
{
    private static readonly string[] ParamNames = { "intensity", "threshold", "span", "rate", "dir" };

    private static Dictionary<string, float> Ov(params (string K, float V)[] kv)
    {
        var d = new Dictionary<string, float>();
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    /// <summary>Серый вход: яркость v[y,x] (строка 0 — верх), цвет серый.</summary>
    private static void SetGray(GpuHarness gpu, float[,] v) =>
        gpu.SetInput((x, y) => (v[y, x], v[y, x], v[y, x]));

    private static float[,] RandomGray(int w, int h, float lo, float hi, int seed)
    {
        var rnd = new Random(seed);
        var v = new float[h, w];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            v[y, x] = lo + (hi - lo) * (float)rnd.NextDouble();
        return v;
    }

    /// <summary>
    /// Отрезки кадра в координатах сортировки: для вертикальной (d 0/1) индекс — GL-y = H−1−row, куски по span;
    /// для горизонтальной (d 2/3) индекс — x. Значение — яркость (серый вход). Порядок — по возрастанию индекса сортировки в
    /// направлении строк (сверху вниз) или столбцов (слева направо).
    /// </summary>
    private static List<List<float>> Segments(float[,,] img, int d, int span)
    {
        int h = img.GetLength(0), w = img.GetLength(1);
        var res = new List<List<float>>();
        if (d < 2)
        {
            for (int x = 0; x < w; x++)
            {
                int cur = int.MinValue;
                List<float>? seg = null;
                for (int r = 0; r < h; r++)
                {
                    int cell = (h - 1 - r) / span;
                    if (cell != cur) { seg = new List<float>(); res.Add(seg); cur = cell; }
                    seg!.Add(GpuHarness.Luma(img, x, r));
                }
            }
        }
        else
        {
            for (int r = 0; r < h; r++)
            {
                int cur = int.MinValue;
                List<float>? seg = null;
                for (int x = 0; x < w; x++)
                {
                    int cell = x / span;
                    if (cell != cur) { seg = new List<float>(); res.Add(seg); cur = cell; }
                    seg!.Add(GpuHarness.Luma(img, x, r));
                }
            }
        }
        return res;
    }

    // ---- Контракт и статика ----

    [Fact]
    public void Id_IsPixelsort()
    {
        Assert.Equal("pixelsort", new PixelsortFilter().Id);
    }

    [Fact]
    public void Params_NamesDefaultsAndRanges()
    {
        var ps = new PixelsortFilter().Params;
        Assert.Equal(ParamNames, ps.Select(p => p.Name).ToArray());
        var def = ps.ToDictionary(p => p.Name, p => p.Default);
        Assert.Equal(0.8f, def["intensity"]);
        Assert.Equal(0.45f, def["threshold"]);
        Assert.Equal(160f, def["span"]);
        Assert.Equal(0.4f, def["rate"]);
        Assert.Equal(4f, def["dir"]);
        foreach (var p in ps)
            Assert.True(p.Min <= p.Default && p.Default <= p.Max, $"{p.Name}: Default вне диапазона");
        var dir = ps.Single(p => p.Name == "dir");
        Assert.Equal(new[] { "down", "up", "right", "left", "random" }, dir.Options);
    }

    [Fact]
    public void Params_ArtifactFlagsAndLabels()
    {
        var ps = new PixelsortFilter().Params;
        var intensity = ps.Single(p => p.Name == "intensity");
        Assert.Equal(0f, intensity.Clean);
        Assert.True(intensity.TextOff);
        foreach (var p in ps)
        {
            Assert.False(string.IsNullOrEmpty(p.Label));
            Assert.True(p.Label!.Length <= 22, $"{p.Name}: подпись длиннее 22 символов");
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
        }
    }

    [Fact]
    public void Shaders_PassStaticChecks()
    {
        foreach (var name in new[] { "pixelsort.frag", "pixelsort_reset.frag", "pixelsort_copy.frag",
                     "pixelsort_start.frag", "pixelsort_scan.frag", "pixelsort_sort.frag" })
        {
            var src = ShaderStatic.Load("Phosphor.Filters.Shaders." + name);
            ShaderStatic.CheckSyntax(src, name);
        }
        var main = ShaderStatic.Load("Phosphor.Filters.Shaders.pixelsort.frag");
        ShaderStatic.CheckParams(new PixelsortFilter(), main);
    }

    // ---- Расписание (только C#) ----

    [Fact]
    public void Schedule_RateZero_NoEpisodes()
    {
        for (int i = 0; i < 6000; i++)
            Assert.False(PixelsortFilter.Schedule(i * 0.1f, 0f).Active);
    }

    private static (int count, double activeSec) CountEpisodes(float rate)
    {
        int count = 0;
        double activeSec = 0;
        long last = -999;
        for (int i = 0; i < 6000; i++)   // 10 минут шагом 0.1 с
        {
            var ep = PixelsortFilter.Schedule(i * 0.1f, rate);
            if (!ep.Active) continue;
            activeSec += 0.1;
            if (ep.Id != last) { count++; last = ep.Id; }
        }
        return (count, activeSec);
    }

    [Fact]
    public void Schedule_DefaultRate_EpisodesExistAndAreRareAndShort()
    {
        var (count, activeSec) = CountEpisodes(0.4f);
        Assert.True(count > 0, "за 10 минут при rate 0.4 нет ни одного эпизода");
        Assert.True(activeSec < 600.0 * 0.5, $"эпизоды занимают {activeSec:F1} с из 600");
        double avg = activeSec / count;
        Assert.True(avg >= 1.8 && avg <= 6.2, $"средняя длительность {avg:F2} с вне 2..6 с");
        Assert.True(CountEpisodes(6f).count > count, "при большем rate эпизодов должно быть больше");
    }

    // ---- Картинка ----

    [Fact]
    public void Strength0_IsIdentity()
    {
        using var gpu = new GpuHarness(128, 96);
        var rnd = new Random(3);
        gpu.SetInput((x, y) => ((float)rnd.NextDouble(), (float)rnd.NextDouble(), (float)rnd.NextDouble()));
        using var f = gpu.Create<PixelsortFilter>();
        f.ForceEpisode = 0.5f;
        var ov = Ov(("intensity", 0f));
        var img = gpu.Run(f, 5, ov);
        var rnd2 = new Random(3);
        for (int y = 0; y < 96; y++)
        for (int x = 0; x < 128; x++)
        {
            var exp = new[] { (float)rnd2.NextDouble(), (float)rnd2.NextDouble(), (float)rnd2.NextDouble() };
            for (int c = 0; c < 3; c++)
                Assert.True(Math.Abs(img[y, x, c] - exp[c]) <= 2f / 255f + 1e-4f, $"пиксель {x},{y} канал {c}");
        }
    }

    [Fact]
    public void OutsideEpisode_IsIdentity()
    {
        using var gpu = new GpuHarness(128, 96);
        var v = RandomGray(128, 96, 0f, 1f, 9);
        SetGray(gpu, v);
        using var f = gpu.Create<PixelsortFilter>();
        var img = gpu.Run(f, 77, Ov(("rate", 0f)));
        for (int y = 0; y < 96; y++)
        for (int x = 0; x < 128; x++)
            Assert.True(Math.Abs(img[y, x, 0] - v[y, x]) <= 2f / 255f + 1e-4f);
    }

    [Fact]
    public void EpisodeInMiddle_ChangesPicture()
    {
        using var gpu = new GpuHarness(256, 128);
        SetGray(gpu, RandomGray(256, 128, 0.2f, 1f, 4));
        using var f = gpu.Create<PixelsortFilter>();
        f.ForceEpisode = 0.5f;
        var inp = RandomGray(256, 128, 0.2f, 1f, 4);
        var img = gpu.Run(f, 3, Ov());
        float maxTile = 0f;
        for (int ty = 0; ty < 128; ty += 16)
        for (int tx = 0; tx < 256; tx += 64)
        {
            double s = 0;
            for (int y = ty; y < ty + 16; y++)
            for (int x = tx; x < tx + 64; x++)
                for (int c = 0; c < 3; c++) s += Math.Abs(img[y, x, c] - inp[y, x]);
            maxTile = Math.Max(maxTile, (float)(s / (16 * 64 * 3)));
        }
        Assert.True(maxTile >= 0.03f, $"максимум средней |Δ| по плиткам {maxTile:F3} < 0.03");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FullEpisode_SegmentsAreMonotoneInDirection(int dir)
    {
        const int w = 64, h = 128, span = 16;
        using var gpu = new GpuHarness(w, h);
        SetGray(gpu, RandomGray(w, h, 0.5f, 1f, 11));
        using var f = gpu.Create<PixelsortFilter>();
        f.ForceEpisode = 0.5f;
        var ov = Ov(("intensity", 1f), ("span", span), ("threshold", 0.45f), ("dir", dir));
        float[,,] img = null!;
        for (int i = 0; i < 10; i++) img = gpu.Run(f, 1 + i, ov);   // 240 проходов ≥ span

        bool nonDecreasing = dir == 0 || dir == 2;   // down и right: светлое к низу/вправо
        foreach (var seg in Segments(img, dir, span))
        for (int k = 1; k < seg.Count; k++)
        {
            if (nonDecreasing) Assert.True(seg[k] >= seg[k - 1] - 1e-3f, $"dir {dir}: нарушен порядок в отрезке, {seg[k - 1]:F4} > {seg[k]:F4}");
            else Assert.True(seg[k] <= seg[k - 1] + 1e-3f, $"dir {dir}: нарушен порядок в отрезке, {seg[k - 1]:F4} < {seg[k]:F4}");
        }
    }

    [Fact]
    public void FullEpisode_KeepsValuesInsideSegment()
    {
        const int w = 64, h = 128, span = 16;
        using var gpu = new GpuHarness(w, h);
        var v = RandomGray(w, h, 0.5f, 1f, 12);
        SetGray(gpu, v);
        using var f = gpu.Create<PixelsortFilter>();
        f.ForceEpisode = 0.5f;
        var ov = Ov(("intensity", 1f), ("span", span), ("dir", 1f));
        float[,,] img = null!;
        for (int i = 0; i < 10; i++) img = gpu.Run(f, 1 + i, ov);

        var inSeg = Segments(BuildGray(v), 1, span);
        var outSeg = Segments(img, 1, span);
        Assert.Equal(inSeg.Count, outSeg.Count);
        for (int s = 0; s < inSeg.Count; s++)
        {
            var a = inSeg[s].OrderBy(x => x).ToArray();
            var b = outSeg[s].OrderBy(x => x).ToArray();
            Assert.Equal(a.Length, b.Length);
            for (int k = 0; k < a.Length; k++)
                Assert.True(Math.Abs(a[k] - b[k]) <= 1f / 255f + 1e-4f, $"отрезок {s}: значения не совпадают ({a[k]:F4} и {b[k]:F4})");
        }
    }

    private static float[,,] BuildGray(float[,] v)
    {
        int h = v.GetLength(0), w = v.GetLength(1);
        var img = new float[h, w, 3];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            img[y, x, 0] = v[y, x]; img[y, x, 1] = v[y, x]; img[y, x, 2] = v[y, x];
        }
        return img;
    }

    [Fact]
    public void BelowThreshold_PixelsStayPut()
    {
        const int w = 64, h = 64;
        using var gpu = new GpuHarness(w, h);
        var v = RandomGray(w, h, 0f, 1f, 13);
        // Половина пикселей гарантированно темнее порога, остальные ярче.
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            v[y, x] = (x + y) % 2 == 0 ? v[y, x] * 0.4f : 0.5f + v[y, x] * 0.5f;
        SetGray(gpu, v);
        using var f = gpu.Create<PixelsortFilter>();
        f.ForceEpisode = 0.5f;
        var img = gpu.Run(f, 1, Ov(("intensity", 1f), ("span", 16f), ("dir", 0f)));
        for (int i = 0; i < 6; i++) img = gpu.Run(f, 2 + i, Ov(("intensity", 1f), ("span", 16f), ("dir", 0f)));
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            if (v[y, x] < 0.45f)
                Assert.True(Math.Abs(img[y, x, 0] - v[y, x]) <= 2f / 255f + 1e-4f, $"тёмный пиксель {x},{y} сдвинулся: {v[y, x]:F3} → {img[y, x, 0]:F3}");
    }

    /// <summary>
    /// Начало отрезка — граница маски (переход из тёмного в яркое), а не кратное span: тёмные пиксели 0..36,
    /// яркие 37..127, span 16 → куски [37,53), [53,69), ... Каждый кусок монотонен и сохраняет значения.
    /// Со сеткой по кратным span кусок [32,48) пересёк бы границу и тест бы упал.
    /// </summary>
    [Fact]
    public void Segments_StartAtMaskBoundary_NotAtSpanMultiples()
    {
        const int w = 128, h = 8, span = 16, boundary = 37;
        using var gpu = new GpuHarness(w, h);
        var rnd = new Random(51);
        var v = new float[h, w];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            v[y, x] = x < boundary ? 0.1f + 0.2f * (float)rnd.NextDouble() : 0.5f + 0.5f * (float)rnd.NextDouble();
        SetGray(gpu, v);
        using var f = gpu.Create<PixelsortFilter>();
        f.ForceEpisode = 0.5f;
        var ov = Ov(("intensity", 1f), ("span", span), ("threshold", 0.45f), ("dir", 2f));   // right: по возрастанию x
        float[,,] img = null!;
        for (int i = 0; i < 10; i++) img = gpu.Run(f, 1 + i, ov);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < boundary; x++)
                Assert.True(Math.Abs(img[y, x, 0] - v[y, x]) <= 2f / 255f + 1e-4f, $"тёмный пиксель {x},{y} сдвинулся");
            for (int s = boundary; s < w; s += span)
            {
                int e = Math.Min(w, s + span);
                var inVals = new List<float>();
                var outVals = new List<float>();
                for (int x = s; x < e; x++)
                {
                    inVals.Add(v[y, x]);
                    outVals.Add(img[y, x, 0]);
                }
                for (int k = 1; k < outVals.Count; k++)
                    Assert.True(outVals[k] >= outVals[k - 1] - 1e-3f, $"строка {y}, кусок [{s},{e}): порядок нарушен");
                var a = inVals.OrderBy(q => q).ToArray();
                var b = outVals.OrderBy(q => q).ToArray();
                for (int k = 0; k < a.Length; k++)
                    Assert.True(Math.Abs(a[k] - b[k]) <= 1f / 255f + 1e-4f, $"строка {y}, кусок [{s},{e}): значения не сохранились");
            }
        }
    }

    [Fact]
    public void Deterministic_TwoRunsGiveSamePixels()
    {
        using var gpu = new GpuHarness(96, 64);
        SetGray(gpu, RandomGray(96, 64, 0f, 1f, 21));
        float[,,] Run()
        {
            using var f = gpu.Create<PixelsortFilter>();
            f.ForceEpisode = 0.5f;
            float[,,] img = null!;
            for (int i = 0; i < 8; i++) img = gpu.Run(f, 1 + i, Ov());
            return img;
        }
        var a = Run();
        var b = Run();
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 96; x++)
        for (int c = 0; c < 3; c++)
            Assert.Equal(a[y, x, c], b[y, x, c]);
    }

    [Fact]
    public void Resize_DoesNotThrowAndKeepsWorking()
    {
        using var gpu = new GpuHarness(256, 256);
        SetGray(gpu, RandomGray(256, 256, 0f, 1f, 31));
        using var f = gpu.Create<PixelsortFilter>();
        var p = new FilterParams();
        foreach (var s in f.Params) p[s.Name] = s.Default;
        f.ForceEpisode = 0.5f;
        foreach (var (w, h) in new[] { (640, 360), (1280, 720), (100, 50), (1, 1), (640, 360) })
        {
            f.Resize(w, h);
            using var target = new RenderTarget(gpu.Gl, w, h);
            var fi = new FrameInfo(1f, 1f / 60f, 1, w, h, 0, AudioFrame.Silent);
            f.Apply(gpu.InputTexture, target, fi, p);
            gpu.Gl.Finish();
        }
    }

    [Fact]
    public void Performance_1920x1080_Under4ms()
    {
        using var gpu = new GpuHarness(1920, 1080);
        SetGray(gpu, RandomGray(1920, 1080, 0f, 1f, 41));
        using var f = gpu.Create<PixelsortFilter>();
        f.ForceEpisode = 0.5f;
        using var target = new RenderTarget(gpu.Gl, 1920, 1080);
        var p = new FilterParams();
        foreach (var s in f.Params) p[s.Name] = s.Default;
        const int frames = 120;
        f.Apply(gpu.InputTexture, target, new FrameInfo(0f, 1f / 60f, 0, 1920, 1080, 0, AudioFrame.Silent), p);
        gpu.Gl.Finish();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            f.Apply(gpu.InputTexture, target, new FrameInfo(i / 60f, 1f / 60f, i, 1920, 1080, 0, AudioFrame.Silent), p);
            gpu.Gl.Finish();
        }
        sw.Stop();
        double msPerFrame = sw.Elapsed.TotalMilliseconds / frames;
        Console.WriteLine($"Pixelsort 1920x1080 (эпизод, intensity по умолчанию): {msPerFrame:F3} мс/кадр");
        Assert.True(msPerFrame <= 4.0, $"{msPerFrame:F3} мс/кадр, порог 4 мс");
    }
}
