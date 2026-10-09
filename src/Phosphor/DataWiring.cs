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

        var hub = new DataHub(sources, online, m => log.Info(m), (k, ps) => Merge(k, ps, net));
        hub.Start();
        log.Info($"data: {(online ? "онлайн" : "только кеш")}, источники: {string.Join(", ", sources.Select(s => s.Name))}");
        return hub;
    }

    private static DataSnapshot? Merge(string key, IReadOnlyList<DataSnapshot> perSource, NetworkConfig net) => key switch
    {
        "news" => NewsCatalog.Merge(perSource, net.CalmNews, net.CalmExtraWords),
        "teletext" => TeletextCatalog.Merge(perSource),
        _ => perSource.MaxBy(s => s.FetchedAt),
    };
}
