using Phosphor.App.Settings;
using Phosphor.Core;
using Phosphor.Platform;

namespace Phosphor.App.Tests;

public class SettingsModelTests
{
    private static readonly string[] Catalog = { "matrix", "starfield", "pipes" };

    private static PhosphorConfig Defaults() => PhosphorConfig.Parse(PhosphorConfig.DefaultJson);

    private static SettingsModel Model(PhosphorConfig? cfg = null) => new(cfg ?? Defaults(), Catalog);

    private static string Canon(System.Text.Json.JsonElement e) => System.Text.Json.JsonSerializer.Serialize(e);

    [Fact]
    public void SceneCycle_IsOffOnFavoriteOff()
    {
        var m = Model();
        m.SetScene("pipes", Pick.Off);
        Assert.Equal(Pick.Off, m.GetScene("pipes"));
        Assert.Equal(Pick.On, m.CycleScene("pipes"));
        Assert.Equal(Pick.Favorite, m.CycleScene("pipes"));
        Assert.Equal(Pick.Off, m.CycleScene("pipes"));
        Assert.Equal(Pick.Off, m.GetScene("pipes"));
    }

    [Fact]
    public void Favorite_IsAlwaysEnabled()
    {
        var m = Model();
        m.SetScene("pipes", Pick.Favorite);
        Assert.Contains("pipes", m.Config.Scenes.Enabled);
        Assert.Contains("pipes", m.Config.Scenes.Favorites);
        Assert.All(m.Config.Scenes.Favorites, f => Assert.Contains(f, m.Config.Scenes.Enabled));
    }

    [Fact]
    public void TurningFavoriteOff_RemovesItFromFavorites()
    {
        var m = Model();
        m.SetScene("matrix", Pick.Favorite);
        m.SetScene("matrix", Pick.Off);
        Assert.DoesNotContain("matrix", m.Config.Scenes.Enabled);
        Assert.DoesNotContain("matrix", m.Config.Scenes.Favorites);
    }

    [Fact]
    public void SettingOn_DropsFavoriteMark()
    {
        var m = Model();
        m.SetScene("matrix", Pick.Favorite);
        m.SetScene("matrix", Pick.On);
        Assert.Equal(Pick.On, m.GetScene("matrix"));
    }

    [Fact]
    public void AllScenesOff_ClearsFavorites()
    {
        var m = Model();
        m.SetScene("matrix", Pick.Favorite);
        m.SetAllScenes(false);
        Assert.Empty(m.Config.Scenes.Favorites);
        Assert.All(Catalog, id => Assert.Equal(Pick.Off, m.GetScene(id)));
    }

    [Fact]
    public void AllScenesOn_KeepsFavorites()
    {
        var m = Model();
        m.SetScene("starfield", Pick.Favorite);
        m.SetAllScenes(true);
        Assert.Equal(Pick.Favorite, m.GetScene("starfield"));
        Assert.All(Catalog, id => Assert.NotEqual(Pick.Off, m.GetScene(id)));
    }

    [Fact]
    public void LoadedFavoritesOutsideEnabled_AreNormalized()
    {
        var cfg = Defaults();
        cfg.Scenes.Enabled = new List<string> { "matrix" };
        cfg.Scenes.Favorites = new List<string> { "matrix", "pipes" };
        var m = Model(cfg);
        Assert.Equal(new[] { "matrix" }, m.Config.Scenes.Favorites);
    }

    [Fact]
    public void Presets_FirstChangeFixesExplicitEnabledList()
    {
        var m = Model();
        Assert.Null(m.Config.Filters.Enabled);
        Assert.Equal(Pick.On, m.GetPreset("clean"));
        m.SetPreset("clean", Pick.Off);
        Assert.NotNull(m.Config.Filters.Enabled);
        Assert.DoesNotContain("clean", m.Config.Filters.Enabled!);
        Assert.Equal(Pick.Off, m.GetPreset("clean"));
        Assert.Equal(Pick.Favorite, m.CyclePreset("terminal"));
    }

    [Fact]
    public void PresetFavorite_IsEnabledAndCycles()
    {
        var m = Model();
        m.SetPreset("amber", Pick.Off);
        Assert.Equal(Pick.On, m.CyclePreset("amber"));
        Assert.Equal(Pick.Favorite, m.CyclePreset("amber"));
        Assert.Contains("amber", m.Config.Filters.Favorites);
        Assert.Contains("amber", m.Config.Filters.Enabled!);
        Assert.Equal(Pick.Off, m.CyclePreset("amber"));
        Assert.DoesNotContain("amber", m.Config.Filters.Favorites);
    }

    [Fact]
    public void Save_RoundTrip_KeepsEverythingNotShownInWindow()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        m.SetScene("pipes", Pick.Favorite);
        m.SetScene("matrix", Pick.Off);
        m.SetPreset("rave", Pick.Favorite);
        cfg.Shuffle.IntervalSec = 45;
        cfg.Dimmer.SoftwareLevel = 0.5f;
        var path = Path.Combine(Path.GetTempPath(), "phosphor-app-tests-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            m.Save(path);
            var back = PhosphorConfig.Parse(File.ReadAllText(path));

            Assert.Equal(cfg.Scenes.Enabled, back.Scenes.Enabled);
            Assert.Equal(cfg.Scenes.Favorites, back.Scenes.Favorites);
            Assert.Equal(cfg.Filters.Enabled, back.Filters.Enabled);
            Assert.Equal(cfg.Filters.Favorites, back.Filters.Favorites);
            Assert.Equal(cfg.Shuffle.IntervalSec, back.Shuffle.IntervalSec);
            Assert.Equal(cfg.Dimmer.SoftwareLevel, back.Dimmer.SoftwareLevel);

            // Поля, которых в окне нет, не теряются (сравнение без учёта пробелов и переносов)
            Assert.Equal(Canon(cfg.Scenes.Params), Canon(back.Scenes.Params));
            Assert.Equal(cfg.Network.News, back.Network.News);
            Assert.Equal(cfg.Network.Satellite, back.Network.Satellite);
            Assert.Equal(cfg.Filters.Presets.Count, back.Filters.Presets.Count);
            foreach (var (name, el) in cfg.Filters.Presets)
                Assert.Equal(Canon(el), Canon(back.Filters.Presets[name]));
        }
        finally
        {
            File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        }
    }

    [Fact]
    public void Save_MarksAllCatalogScenesSeen_WithoutEnablingThem()
    {
        var cfg = Defaults();
        cfg.Scenes.Enabled = new List<string> { "matrix" };
        cfg.Scenes.Seen = new List<string> { "matrix" };
        var m = Model(cfg);
        var path = Path.Combine(Path.GetTempPath(), "phosphor-app-tests-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            m.Save(path);
            var back = PhosphorConfig.Parse(File.ReadAllText(path));
            Assert.NotNull(back.Scenes.Seen);
            Assert.All(Catalog, id => Assert.Contains(id, back.Scenes.Seen!));
            Assert.Equal(new[] { "matrix" }, back.Scenes.Enabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Problem_NoScenesOrNoPresets()
    {
        var m = Model();
        Assert.Null(m.Problem());
        m.SetAllScenes(false);
        Assert.Equal("Включите хотя бы одну сцену.", m.Problem());

        var m2 = Model();
        m2.SetAllPresets(false);
        Assert.Equal("Включите хотя бы один фильтр.", m2.Problem());
    }

    [Fact]
    public void PreviewPreset_ShuffleFallsBackToFirstEnabled()
    {
        var m = Model();
        Assert.Equal("terminal", m.PreviewPreset("terminal"));
        Assert.Equal("clean", m.PreviewPreset("shuffle"));
        m.SetPreset("clean", Pick.Off);
        Assert.Equal("terminal", m.PreviewPreset(FiltersConfig.ShuffleActive));
    }

    [Fact]
    public void PreviewArguments_ParseBackAsPreviewLaunch()
    {
        string line = SettingsModel.PreviewArguments(12345, "matrix", "terminal");
        Assert.Equal("/p 12345 --scene matrix --preset terminal", line);

        var a = LaunchArgs.Parse(line.Split(' '));
        Assert.Equal(LaunchMode.Preview, a.Mode);
        Assert.Equal((nint)12345, a.ParentHwnd);
        Assert.Equal("matrix", a.Scene);
        Assert.Equal("terminal", a.Preset);
    }

    [Fact]
    public void PreviewArguments_WithoutPresetAndWithSpaces()
    {
        Assert.Equal("/p 7 --scene pipes", SettingsModel.PreviewArguments(7, "pipes", null));
        Assert.Equal("/p 7 --scene \"my scene\" --preset \"a b\"", SettingsModel.PreviewArguments(7, "my scene", "a b"));
    }
}
