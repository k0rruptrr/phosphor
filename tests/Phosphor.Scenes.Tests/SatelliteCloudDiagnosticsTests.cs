using System.Globalization;
using System.Text;
using System.Text.Json;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

/// <summary>
/// Диагностика трекинга на реальном кеше meteosat (не входит в обычный прогон). Печатает всех кандидатов до фильтров:
/// bbox, сдвиг, MAD лучшего и при нуле, медиану MAD по окну поиска, скорость, интервал. Вывод — в %TEMP%\s2-sat-diag.txt.
/// </summary>
[Trait("Category", "Diagnostics")]
public sealed class SatelliteCloudDiagnosticsTests
{
    /// <summary>Кеш по умолчанию: .build\satcache от корня репозитория (папку с Phosphor.slnx ищем вверх от каталога сборки).</summary>
    private static string DefaultCache
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Phosphor.slnx")))
                dir = dir.Parent;
            return dir is null ? Path.Combine(".build", "satcache") : Path.Combine(dir.FullName, ".build", "satcache");
        }
    }

    private readonly ITestOutputHelper _out;

    public SatelliteCloudDiagnosticsTests(ITestOutputHelper output) => _out = output;

    private static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    [Fact]
    public void MeteosatRealCacheCandidates()
    {
        string dir = Environment.GetEnvironmentVariable("PHOSPHOR_CACHE_DIR") is { Length: > 0 } e ? e : DefaultCache;
        string file = Path.Combine(dir, "satellite.meteosat.json");
        if (!File.Exists(file))
        {
            // Нет кеша (например, на чужой машине) — диагностика пропускается, а не падает.
            _out.WriteLine($"пропуск: нет {file} (задай PHOSPHOR_CACHE_DIR)");
            return;
        }
        string text = File.ReadAllText(file);
        text = text[(text.IndexOf('\n') + 1)..];
        var feed = JsonSerializer.Deserialize<SatelliteFeed>(text, Json)!;
        var frames = feed.Frames;

        var sb = new StringBuilder();
        sb.AppendLine($"frames={frames.Count} size={frames[0].Width}x{frames[0].Height} area lon {feed.Area.West:F2}..{feed.Area.East:F2} lat {feed.Area.South:F2}..{feed.Area.North:F2}");
        double degPerPx = (feed.Area.East - feed.Area.West) / frames[0].Width;
        sb.AppendLine($"1 px = {degPerPx:F4}° lon = {degPerPx * 111 * Math.Cos(Math.PI / 180 * 55.75):F2} км на широте 55.75° (lat: {(feed.Area.North - feed.Area.South) / frames[0].Height * 111:F2} км)");

        var last = frames[^1];
        // baseline по правилу Apply: последний кадр не раньше 20 минут назад
        int[] baseIdx = { frames.Count - 3, frames.Count - 2 };
        foreach (int bi in baseIdx)
        {
            var b = frames[bi];
            var clouds = new SatClouds();
            clouds.Compute(last, b, feed.Area);
            sb.AppendLine();
            sb.AppendLine($"=== last {last.TakenAt:HH:mm} base {b.TakenAt:HH:mm} interval {(last.TakenAt - b.TakenAt).TotalMinutes:F0} мин ===");
            sb.AppendLine("bbox(gx0,gy0-gx1,gy1) area compact big edge | dx dy px | MAD best | MAD0 | median | MAD/med | km/h");
            foreach (var c in clouds.Candidates)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "({0},{1})-({2},{3}) {4,4} {5,-5} {6,-5} {7,-5} | {8,6:F2} {9,6:F2} | {10,6:F3} | {11,6:F3} | {12,6:F3} | {13,5:F2} | {14,6:F0}",
                    c.GX0, c.GY0, c.GX1, c.GY1, c.Area, c.Compact, c.Big, c.Edge, c.Mx, c.My, c.Mad, c.Mad0, c.MadMedian,
                    c.MadMedian > 0 ? c.Mad / c.MadMedian : double.NaN, c.SpeedKmh));
            }
            sb.AppendLine($"итог: {clouds.Count} отслеженных");
            for (int i = 0; i < clouds.Count; i++) sb.AppendLine("  " + clouds.Items[i].Label);
        }

        string path = Path.Combine(Path.GetTempPath(), "s2-sat-diag.txt");
        File.WriteAllText(path, sb.ToString());
        _out.WriteLine(sb.ToString());
        _out.WriteLine(path);
    }
}
