namespace Phosphor.Core;

/// <summary>Выбор сцен и пресетов с весами (любимые выпадают чаще). Чистая логика: без Random внутри.</summary>
public static class Selection
{
    /// <summary>Доступные сцены с весами: включённые и реализованные (available); любимые — вес FavoriteWeight;
    /// FavoritesOnly — только любимые (если среди доступных их нет — все доступные с весом 1). Пусто — первая из available.</summary>
    public static IReadOnlyList<(string Id, double Weight)> ScenePool(PhosphorConfig cfg, IEnumerable<string> available)
    {
        var avail = available.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var enabled = new HashSet<string>(cfg.Scenes.Enabled ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var pool = avail.Where(enabled.Contains).ToList();
        if (pool.Count == 0 && avail.Count > 0) pool.Add(avail[0]);
        return Weigh(pool, cfg.Scenes.Favorites, cfg.Scenes.FavoritesOnly, cfg.Shuffle.FavoriteWeight);
    }

    /// <summary>То же для пресетов: Filters.Enabled ?? все Presets.Keys; Favorites/FavoritesOnly из Filters.</summary>
    public static IReadOnlyList<(string Id, double Weight)> PresetPool(PhosphorConfig cfg)
    {
        var all = cfg.Filters.Presets.Keys.ToList();
        var candidates = cfg.Filters.Enabled is null
            ? all
            : all.Where(p => cfg.Filters.Enabled.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0 && all.Count > 0) candidates.Add(all[0]);
        return Weigh(candidates, cfg.Filters.Favorites, cfg.Filters.FavoritesOnly, cfg.Shuffle.FavoriteWeight);
    }

    /// <summary>Взвешенный выбор; exclude — кого не брать (если исключены все — без исключений). r ∈ [0,1).</summary>
    public static string Pick(IReadOnlyList<(string Id, double Weight)> pool, double r, IReadOnlySet<string>? exclude = null)
    {
        if (pool.Count == 0) throw new InvalidOperationException("Пустой пул выбора");

        var candidates = pool;
        if (exclude is not null && exclude.Count > 0)
        {
            var rest = pool.Where(p => !exclude.Contains(p.Id)).ToList();
            if (rest.Count > 0) candidates = rest;
        }

        double total = 0;
        foreach (var c in candidates) total += c.Weight;
        if (total <= 0) return candidates[Math.Clamp((int)(Math.Clamp(r, 0, 1) * candidates.Count), 0, candidates.Count - 1)].Id;

        double target = Math.Clamp(r, 0, 0.999999999) * total;
        double acc = 0;
        foreach (var c in candidates)
        {
            acc += c.Weight;
            if (target < acc) return c.Id;
        }
        return candidates[^1].Id;
    }

    /// <summary>Веса: любимые — favoriteWeight, остальные — 1. FavoritesOnly — только любимые из пула (иначе все с весом 1).</summary>
    private static IReadOnlyList<(string Id, double Weight)> Weigh(List<string> ids, List<string> favorites, bool favoritesOnly, float favoriteWeight)
    {
        var fav = new HashSet<string>(favorites ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        double w = Math.Clamp(favoriteWeight, 1f, 10f);
        var result = new List<(string Id, double Weight)>(ids.Count);
        bool anyFav = ids.Any(fav.Contains);
        foreach (var id in ids)
        {
            bool isFav = fav.Contains(id);
            if (favoritesOnly && anyFav && !isFav) continue;
            result.Add((id, isFav ? w : 1.0));
        }
        return result;
    }
}
