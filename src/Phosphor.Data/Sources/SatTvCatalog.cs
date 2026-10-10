using Phosphor.Core;

namespace Phosphor.Data;

/// <summary>Спутники сцены «сат-ТВ»: список по умолчанию и слияние их списков в один снимок.</summary>
public static class SatTvCatalog
{
    /// <summary>
    /// id страниц KingOfSat по умолчанию: Astra 1P 19.2°E, Hot Bird 13G 13.0°E, Eutelsat 36D 36.0°E,
    /// Yamal 601 49.0°E, Express AM 7 40.0°E.
    /// </summary>
    public static readonly string[] DefaultIds = { "astra1p", "hb13g", "eutelsat36d", "yamal601", "eam7" };

    /// <summary>Пустые спутники выкидываются; порядок — как пришли (хаб отдаёт источники в порядке конфига). Нет ни одного — null.</summary>
    public static SatTvSetSnapshot? Merge(IReadOnlyList<DataSnapshot> perSource)
    {
        var sats = perSource.OfType<SatTvSatellite>().Where(s => s.Channels.Count > 0).ToList();
        if (sats.Count == 0) return null;
        return new SatTvSetSnapshot(sats, sats.Max(s => s.FetchedAt), sats.All(s => s.FromCache));
    }
}
