using Phosphor.Core;
using Phosphor.Data;

namespace Phosphor.App;

/// <summary>Сборка DataHub из конфига: какие источники включены и как сливать снимки одного ключа.</summary>
internal static class DataWiring
{
    public static DataHub CreateHub(PhosphorConfig config, bool online, Log log)
    {
        var net = config.Network;
        var sources = new List<IDataSource>();
        DataHub? hub = null;
        // Точка для спутника: координаты из конфига, иначе город из погоды хаба (уже геокодирован).
        Func<(double Lat, double Lon, string Name)?> where = () =>
        {
            if (net.Lat is double la && net.Lon is double lo) return (la, lo, net.City.ToUpperInvariant());
            if (hub is not null && hub.TryGet("weather", out var snap, out _)
                && snap is WeatherSnapshot w && double.IsFinite(w.Lat) && double.IsFinite(w.Lon))
                return (w.Lat, w.Lon, w.City.ToUpperInvariant());
            return null;
        };

        foreach (var id in net.News)
        {
            if (NewsCatalog.Create(id) is { } s) sources.Add(s);
            else log.Warn($"data: неизвестный источник новостей '{id}'. Есть: {string.Join(", ", NewsCatalog.Ids)}, reddit:<sub>");
        }
        if (!string.IsNullOrWhiteSpace(net.City)) sources.Add(new WeatherSource(net.City, net.Lat, net.Lon));
        foreach (var ch in net.Teletext)
        {
            if (TeletextSource.Create(ch) is { } s) sources.Add(s);
            else log.Warn($"data: неизвестный канал телетекста '{ch}' (есть: nos, svt)");
        }
        foreach (var id in net.Satellite)
        {
            if (SatelliteSource.Create(id, where) is { } s) sources.Add(s);
            else log.Warn($"data: неизвестный источник спутника '{id}'. Есть: {string.Join(", ", SatelliteSource.Ids)}");
        }

        hub = new DataHub(sources, online, m => log.Info(m), (k, ps) => Merge(k, ps, net));
        hub.Start();
        log.Info($"data: {(online ? "онлайн" : "только кеш")}, источники: {string.Join(", ", sources.Select(s => s.Name))}");
        return hub;
    }

    private static DataSnapshot? Merge(string key, IReadOnlyList<DataSnapshot> perSource, NetworkConfig net) => key switch
    {
        "news" => NewsCatalog.Merge(perSource, net.CalmNews, net.CalmExtraWords),
        "teletext" => TeletextCatalog.Merge(perSource),
        "satellite" => SatelliteCatalog.Merge(perSource),
        _ => perSource.MaxBy(s => s.FetchedAt),
    };
}
