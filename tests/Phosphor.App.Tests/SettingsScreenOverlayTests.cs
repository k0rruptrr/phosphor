using System.Text.Json;
using Phosphor.App.Settings;
using Phosphor.Core;
using Phosphor.Gfx;

namespace Phosphor.App.Tests;

/// <summary>Вкладки «Оверлеи» и «Экран» окна настроек: модель (фейковые описания оверлея, без каталога).</summary>
public class SettingsScreenOverlayTests
{
    private static readonly string[] Catalog = { "matrix", "fake" };

    /// <summary>Фейковый оверлей с IOverlayParams: по одному параметру каждого вида, включая Choice accent.</summary>
    private static readonly IReadOnlyList<SceneParamSpec> FakeOverlay = new[]
    {
        new SceneParamSpec("accent", SceneParamKind.Choice, 0, 0, 3, "цвет акцента", new[] { "orange", "cyan", "green", "white" }),
        new SceneParamSpec("targets", SceneParamKind.Int, 4, 0, 4, "рамки трекинга"),
        new SceneParamSpec("stats", SceneParamKind.Bool, 1, 0, 1, "блок статистики"),
        new SceneParamSpec("stroke", SceneParamKind.Float, 2, 0, 4, "толщина"),
    };

    private static PhosphorConfig Defaults() => PhosphorConfig.Parse(PhosphorConfig.DefaultJson);

    private static SettingsModel Model(PhosphorConfig cfg) => new(cfg, Catalog,
        new SettingsSpecs(_ => null, _ => null, id => id == "hud" ? FakeOverlay : null));

    private static SceneParamSpec Spec(string name) => FakeOverlay.First(s => s.Name == name);

    // ---------- Экран ----------

    [Fact]
    public void Screen_SetAndGet_ThroughModel_ChangesConfig()
    {
        var cfg = Defaults();
        var m = Model(cfg);

        m.SetScreenParam("curvature", 0.2);
        Assert.Equal(0.2f, cfg.Screen.Curvature, 4);
        Assert.Equal(0.2, m.ScreenValue("curvature"), 4);

        m.SetScreenParam("bezel", 2); // индекс Choice: "tv"
        Assert.Equal("tv", cfg.Screen.Bezel);
        Assert.Equal(2.0, m.ScreenValue("bezel"));
    }

    [Fact]
    public void Screen_ClampsToSpecRange()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        m.SetScreenParam("trail", 5);
        Assert.Equal(0.9f, cfg.Screen.Trail, 4);
    }

    [Fact]
    public void Screen_ResetParam_And_ResetAll_ReturnDefaults()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        m.SetScreenParam("glare", 0.7);
        m.SetScreenParam("bezelColor", 1);
        m.ResetScreenParam("glare");
        Assert.Equal(ScreenConfig.Specs.First(s => s.Name == "glare").Default, cfg.Screen.Glare, 4);

        m.ResetScreen();
        foreach (var s in ScreenConfig.Specs)
            Assert.Equal(s.Default, m.ScreenValue(s.Name), 4);
        Assert.Equal("black", cfg.Screen.BezelColor);
    }

    [Fact]
    public void Screen_KindSwitch_ChangesOnlyEnabled_ValuesStay()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        m.SetScreenParam("curvature", 0.23);
        m.SetScreenParam("bezel", 2);
        m.SetScreenParam("trail", 0.4);

        m.ScreenEnabled = false; // «плоский экран»
        Assert.False(cfg.Screen.Enabled);
        Assert.Equal(0.23, m.ScreenValue("curvature"), 4);
        Assert.Equal("tv", cfg.Screen.Bezel);
        Assert.Equal(0.4, m.ScreenValue("trail"), 4);

        m.ScreenEnabled = true; // обратно — кинескоп, значения на месте
        Assert.True(cfg.Screen.Enabled);
        Assert.Equal(0.23, m.ScreenValue("curvature"), 4);
    }

    [Fact]
    public void Screen_ChangesSurviveSave()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        m.SetScreenParam("vignette", 0.5);
        var path = Path.Combine(Path.GetTempPath(), "phosphor-screen-tests-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            m.Save(path);
            var back = PhosphorConfig.Parse(File.ReadAllText(path));
            Assert.Equal(0.5f, back.Screen.Vignette, 4);
        }
        finally
        {
            File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        }
    }

    // ---------- Оверлеи: вкл/выкл и вероятность ----------

    [Fact]
    public void Overlay_Toggle_FlipsEnabledAndVcr()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        bool hud = cfg.Overlay.Enabled, vcr = cfg.Overlay.Vcr;

        m.ToggleOverlay("hud");
        Assert.Equal(!hud, cfg.Overlay.Enabled);
        Assert.Equal(vcr, cfg.Overlay.Vcr);
        Assert.Equal(!hud ? Pick.On : Pick.Off, m.GetOverlay("hud"));

        m.ToggleOverlay("vcr");
        Assert.Equal(!vcr, cfg.Overlay.Vcr);
        Assert.Equal(!vcr ? Pick.On : Pick.Off, m.GetOverlay("vcr"));
    }

    [Fact]
    public void Overlay_Probability_IsOverlayConfigField_AndResetsToDefault()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        m.OverlayProbability = 0.25;
        Assert.Equal(0.25, cfg.Overlay.Probability, 6);

        m.ResetOverlay("hud");
        Assert.Equal(SettingsModel.DefaultOverlayProbability, cfg.Overlay.Probability, 6);
    }

    // ---------- Оверлеи: параметры ----------

    [Fact]
    public void OverlayParam_Choice_WritesOptionNameToOverlayParamsHud()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        var accent = Spec("accent");

        m.SetOverlayParam("hud", accent, 1);

        Assert.Equal("cyan", cfg.Overlay.Params.GetProperty("hud").GetProperty("accent").GetString());
        Assert.Equal(1.0, m.OverlayParamValue("hud", accent));
    }

    [Fact]
    public void OverlayParam_DefaultRemovesKey_AndKeepsUnknownKeys()
    {
        var cfg = Defaults();
        cfg.Overlay.Params = JsonDocument.Parse("""{ "hud": { "accent": "green", "custom": 3 } }""").RootElement.Clone();
        var m = Model(cfg);

        m.ResetOverlayParam("hud", Spec("accent"));
        Assert.False(cfg.Overlay.Params.GetProperty("hud").TryGetProperty("accent", out _));
        Assert.Equal(3, cfg.Overlay.Params.GetProperty("hud").GetProperty("custom").GetInt32());

        m.ResetOverlay("hud"); // «все по умолчанию»: известные ключи уходят, неизвестные остаются
        Assert.False(cfg.Overlay.Params.GetProperty("hud").TryGetProperty("accent", out _));
        Assert.Equal(3, cfg.Overlay.Params.GetProperty("hud").GetProperty("custom").GetInt32());
    }

    [Fact]
    public void OverlayParam_Bool_And_Int_Stored_As_Json_Types()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        m.SetOverlayParam("hud", Spec("stats"), 0);
        Assert.False(cfg.Overlay.Params.GetProperty("hud").GetProperty("stats").GetBoolean());
        m.SetOverlayParam("hud", Spec("targets"), 2.6);
        Assert.Equal(3, cfg.Overlay.Params.GetProperty("hud").GetProperty("targets").GetInt32());
    }

    // ---------- Оверлеи: шрифт OSD ----------

    [Fact]
    public void VcrFont_Pick_WritesString_AsGridFont_RemovesKey()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        Assert.Equal("", m.OverlayFont);

        m.SetOverlayFont("Iosevka");
        Assert.Equal("Iosevka", cfg.Overlay.Params.GetProperty("vcr").GetProperty("font").GetString());
        Assert.Equal("Iosevka", m.OverlayFont);

        m.SetOverlayFont(""); // «как у сетки» — ключ удаляется
        Assert.False(cfg.Overlay.Params.TryGetProperty("vcr", out _));
        Assert.Equal("", m.OverlayFont);
    }

    [Fact]
    public void VcrFont_KeepsOtherVcrParams()
    {
        var cfg = Defaults();
        cfg.Overlay.Params = JsonDocument.Parse("""{ "vcr": { "color": "green", "font": "Iosevka" } }""").RootElement.Clone();
        var m = Model(cfg);

        m.SetOverlayFont("");
        Assert.Equal("green", cfg.Overlay.Params.GetProperty("vcr").GetProperty("color").GetString());
        Assert.False(cfg.Overlay.Params.GetProperty("vcr").TryGetProperty("font", out _));

        cfg.Overlay.Params = JsonDocument.Parse("""{ "vcr": { "color": "green", "font": "Iosevka" } }""").RootElement.Clone();
        m.ResetOverlay("vcr"); // шрифт — по умолчанию; параметры без описания (color) — не трогаются
        Assert.False(cfg.Overlay.Params.GetProperty("vcr").TryGetProperty("font", out _));
        Assert.Equal("green", cfg.Overlay.Params.GetProperty("vcr").GetProperty("color").GetString());
    }

    // ---------- Превью ----------

    [Fact]
    public void PreviewArguments_OnOverlaysTab_CarriesOverlayId()
    {
        Assert.Equal("/p 5 --scene fake --overlay vcr", SettingsModel.PreviewArguments(5, "fake", null, null, "vcr"));
        Assert.Equal("/p 5 --scene fake --preset terminal --overlay hud --config C:\\t\\p.json",
            SettingsModel.PreviewArguments(5, "fake", "terminal", "C:\\t\\p.json", "hud"));
    }

    [Fact]
    public void OverlayTitles_AreHumanNames()
    {
        Assert.Equal("HUD-разметка", SettingsModel.OverlayTitle("hud"));
        Assert.Equal("OSD видика", SettingsModel.OverlayTitle("vcr"));
        Assert.Equal(new[] { "hud", "vcr" }, SettingsModel.OverlayIds);
    }

    [Fact]
    public void OverlaySpecs_NullWhenOverlayHasNoParams()
    {
        var m = Model(Defaults());
        Assert.NotNull(m.OverlaySpecs("hud"));
        Assert.Null(m.OverlaySpecs("vcr"));
    }
}
