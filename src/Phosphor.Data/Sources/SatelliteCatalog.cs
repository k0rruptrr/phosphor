using Phosphor.Core;

namespace Phosphor.Data;

/// <summary>Слияние лент спутника в один снимок для сцены.</summary>
public static class SatelliteCatalog
{
    /// <summary>Пустые ленты выкидываются; порядок — meteosat, himawari, viirs. Нет ни одной ленты — null.</summary>
    public static SatelliteSetSnapshot? Merge(IReadOnlyList<DataSnapshot> perSource)
    {
        var feeds = perSource.OfType<SatelliteFeed>()
            .Where(f => f.Frames.Count > 0)
            .OrderBy(f => Rank(f.Id))
            .ToList();
        if (feeds.Count == 0) return null;
        return new SatelliteSetSnapshot(feeds, feeds.Max(f => f.FetchedAt), feeds.All(f => f.FromCache));
    }

    private static int Rank(string id)
    {
        int i = Array.IndexOf(SatelliteSource.Ids, id);
        return i < 0 ? int.MaxValue : i;
    }
}
