using Phosphor.Core;

namespace Phosphor.Core.Tests;

public sealed class SelectionTests
{
    private static PhosphorConfig Cfg(string json) => PhosphorConfig.Parse(json);

    [Fact]
    public void ScenePool_FavoriteGetsWeightThree()
    {
        var cfg = Cfg("""{ "scenes": { "enabled": ["a", "b", "c"], "favorites": ["a"] } }""");

        var pool = Selection.ScenePool(cfg, new[] { "a", "b", "c", "d" });

        Assert.Equal(3, pool.Count);
        Assert.Equal(3.0, pool.Single(p => p.Id == "a").Weight);
        Assert.Equal(1.0, pool.Single(p => p.Id == "b").Weight);
        Assert.Equal(1.0, pool.Single(p => p.Id == "c").Weight);
    }

    [Fact]
    public void ScenePool_FavoriteThatIsNotEnabled_IsNotInPool()
    {
        var cfg = Cfg("""{ "scenes": { "enabled": ["a", "b"], "favorites": ["d"] } }""");

        var pool = Selection.ScenePool(cfg, new[] { "a", "b", "d" });

        Assert.DoesNotContain(pool, p => p.Id == "d");
        Assert.All(pool, p => Assert.Equal(1.0, p.Weight));
    }

    [Fact]
    public void ScenePool_FavoritesOnly_LeavesOnlyFavorites()
    {
        var cfg = Cfg("""{ "scenes": { "enabled": ["a", "b", "c"], "favorites": ["a"], "favoritesOnly": true } }""");

        var pool = Selection.ScenePool(cfg, new[] { "a", "b", "c" });

        Assert.Single(pool);
        Assert.Equal("a", pool[0].Id);
    }

    [Fact]
    public void ScenePool_FavoritesOnly_WithoutAvailableFavorites_ReturnsAllWithWeightOne()
    {
        var cfg = Cfg("""{ "scenes": { "enabled": ["a", "b"], "favorites": ["zzz"], "favoritesOnly": true } }""");

        var pool = Selection.ScenePool(cfg, new[] { "a", "b" });

        Assert.Equal(2, pool.Count);
        Assert.All(pool, p => Assert.Equal(1.0, p.Weight));
    }

    [Fact]
    public void ScenePool_EmptyEnabled_FallsBackToFirstAvailable()
    {
        var cfg = Cfg("""{ "scenes": { "enabled": ["zzz"] } }""");

        var pool = Selection.ScenePool(cfg, new[] { "a", "b" });

        Assert.Single(pool);
        Assert.Equal("a", pool[0].Id);
    }

    [Fact]
    public void PresetPool_EnabledNullMeansAll_FavoriteWeighted()
    {
        var cfg = Cfg("""{ "filters": { "active": "clean", "presets": { "clean": [], "amber": [], "rave": [] }, "favorites": ["amber"] } }""");

        var pool = Selection.PresetPool(cfg);

        Assert.Equal(3, pool.Count);
        Assert.Equal(3.0, pool.Single(p => p.Id == "amber").Weight);
    }

    [Fact]
    public void PresetPool_EnabledList_RestrictsPresets()
    {
        var cfg = Cfg("""{ "filters": { "active": "clean", "presets": { "clean": [], "amber": [], "rave": [] }, "enabled": ["clean", "amber"] } }""");

        var pool = Selection.PresetPool(cfg);

        Assert.DoesNotContain(pool, p => p.Id == "rave");
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void Pick_FrequencyMatchesWeights()
    {
        var pool = new (string Id, double Weight)[] { ("fav", 3), ("b", 1), ("c", 1), ("d", 1) };
        int favHits = 0;
        const int N = 10_000;
        for (int k = 0; k < N; k++)
            if (Selection.Pick(pool, (k + 0.5) / N) == "fav") favHits++;

        double expected = 3.0 / (3 + 4 - 1); // доля любимой = 3 / (3 + n - 1), n = 4 сцены в пуле → 0.5
        double share = favHits / (double)N;
        Assert.InRange(share, expected - 0.02, expected + 0.02);
    }

    [Fact]
    public void Pick_RespectsExclude()
    {
        var pool = new (string Id, double Weight)[] { ("a", 1), ("b", 1) };
        var exclude = new HashSet<string> { "a" };
        for (int k = 0; k < 1000; k++)
            Assert.Equal("b", Selection.Pick(pool, k / 1000.0, exclude));
    }

    [Fact]
    public void Pick_AllExcluded_PicksFromAll()
    {
        var pool = new (string Id, double Weight)[] { ("a", 1), ("b", 1) };
        var exclude = new HashSet<string> { "a", "b" };
        var pick = Selection.Pick(pool, 0.7, exclude);
        Assert.Contains(pick, new[] { "a", "b" });
    }
}
