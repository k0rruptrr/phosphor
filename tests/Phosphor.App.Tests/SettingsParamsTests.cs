using System.Text.Json;
using System.Text.Json.Nodes;
using Phosphor.App.Settings;
using Phosphor.Core;
using Phosphor.Gfx;

namespace Phosphor.App.Tests;

/// <summary>Правка параметров сцен и стадий пресетов в окне настроек. Описания — фейковые (ISceneParams и ParamSpec из теста).</summary>
public class SettingsParamsTests
{
    private static readonly string[] Catalog = { "matrix", "fake" };

    /// <summary>Фейковая сцена с ISceneParams: по одному параметру каждого вида.</summary>
    private sealed class FakeScene : ISceneParams
    {
        public IReadOnlyList<SceneParamSpec> Params { get; } = new[]
        {
            new SceneParamSpec("speed", SceneParamKind.Float, 1f, 0f, 3f, "скорость"),
            new SceneParamSpec("count", SceneParamKind.Int, 5f, 1f, 10f, "число"),
            new SceneParamSpec("glow", SceneParamKind.Bool, 1f, 0f, 1f, "свечение"),
            new SceneParamSpec("mode", SceneParamKind.Choice, 1f, 0f, 2f, "режим", new[] { "a", "b", "c" }),
        };
    }

    private static readonly ParamSpec Curvature = new("curvature", 0.08f, 0f, 0.5f);
    private static readonly ParamSpec Color = new("color", 0f, 0f, 2f, new[] { "green", "amber", "white" });

    private static SettingsSpecs Specs() => new(
        id => id switch
        {
            "crt" => new[] { Curvature, Color },
            _ => null,
        },
        id => id == "fake" ? new FakeScene().Params : null);

    private static PhosphorConfig Defaults() => PhosphorConfig.Parse(PhosphorConfig.DefaultJson);

    private static SettingsModel Model(PhosphorConfig cfg) => new(cfg, Catalog, Specs());

    private static string Canon(JsonElement e) => JsonNode.Parse(e.GetRawText())!.ToJsonString();

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static string Raw(PhosphorConfig cfg, string preset) => cfg.Filters.Presets.First(kv => string.Equals(kv.Key, preset, StringComparison.OrdinalIgnoreCase)).Value.GetRawText();

    // ---------- Стадии пресетов ----------

    [Fact]
    public void EditingStageParam_ChangesOnlyThatValue_KeepsModAndUnknownFields()
    {
        var cfg = Defaults();
        cfg.Filters.Presets["mine"] = Json("""[ {"id":"crt","curvature":0.04,"unknownField":5,"mod":{"bloom":{"src":"bass","amount":0.4}}}, {"id":"mono"} ]""");
        var m = Model(cfg);

        m.SetPresetParam("mine", 0, Curvature, 0.12);

        var stage = JsonNode.Parse(Raw(cfg, "mine"))!.AsArray()[0]!.AsObject();
        Assert.Equal(0.12, stage["curvature"]!.GetValue<double>(), 6);
        Assert.Equal(5, stage["unknownField"]!.GetValue<int>());
        Assert.Equal("bass", stage["mod"]!["bloom"]!["src"]!.GetValue<string>());
        Assert.Equal(0.4, stage["mod"]!["bloom"]!["amount"]!.GetValue<double>(), 6);
        Assert.Equal("mono", JsonNode.Parse(Raw(cfg, "mine"))!.AsArray()[1]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void StageParam_EqualToDefault_RemovesKey()
    {
        var cfg = Defaults();
        cfg.Filters.Presets["mine"] = Json("""[ {"id":"crt","curvature":0.04} ]""");
        var m = Model(cfg);
        m.SetPresetParam("mine", 0, Curvature, 0.08);
        var stage = JsonNode.Parse(Raw(cfg, "mine"))!.AsArray()[0]!.AsObject();
        Assert.False(stage.ContainsKey("curvature"));
        Assert.Equal(0.08f, (float)m.PresetParamValue("mine", 0, Curvature), 4);
    }

    [Fact]
    public void EnumStageParam_WritesOptionName()
    {
        var cfg = Defaults();
        cfg.Filters.Presets["mine"] = Json("""[ {"id":"crt"} ]""");
        var m = Model(cfg);
        m.SetPresetParam("mine", 0, Color, 1);
        Assert.Equal("amber", JsonNode.Parse(Raw(cfg, "mine"))!.AsArray()[0]!["color"]!.GetValue<string>());
        Assert.Equal(1.0, m.PresetParamValue("mine", 0, Color));
    }

    [Fact]
    public void PresetStages_ReturnParamsOrEmptyForStagesWithoutSettings()
    {
        var m = Model(Defaults());
        var stages = m.PresetStages("terminal"); // mono, phosphor, crt
        Assert.Equal(new[] { "mono", "phosphor", "crt" }, stages.Select(s => s.Id).ToArray());
        Assert.Empty(stages[0].Params);
        Assert.Contains(stages[2].Params, p => p.Name == "curvature");
    }

    [Fact]
    public void RestoreBuiltin_GivesBuiltinJson_ForBuiltinOnly()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        m.SetPresetParam("clean", 0, Curvature, 0.25);
        Assert.NotEqual(Canon(Defaults().Filters.Presets["clean"]), Canon(cfg.Filters.Presets["clean"]));

        Assert.True(m.RestoreBuiltinPreset("clean"));
        Assert.Equal(Canon(Defaults().Filters.Presets["clean"]), Canon(cfg.Filters.Presets["clean"]));

        cfg.Filters.Presets["mine"] = Json("""[ {"id":"crt"} ]""");
        Assert.False(m.RestoreBuiltinPreset("mine"));
    }

    [Fact]
    public void SaveAs_CreatesCopy_SourceUnchanged()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        m.SetPresetParam("terminal", 2, Curvature, 0.3);
        string before = Canon(cfg.Filters.Presets["terminal"]);

        Assert.Null(m.SaveAsPreset("terminal", "my-term"));

        Assert.True(cfg.Filters.Presets.ContainsKey("my-term"));
        Assert.Equal(0.3, JsonNode.Parse(Raw(cfg, "my-term"))!.AsArray()[2]!["curvature"]!.GetValue<double>(), 6);
        Assert.Equal(before, Canon(cfg.Filters.Presets["terminal"]));
        Assert.True(m.IsPresetModified("my-term")); // новый пресет в файле ещё нет
        // Enabled == null означает «все пресеты» — новый тоже участвует; явный список дописывается
        Assert.True(cfg.Filters.Enabled is null || cfg.Filters.Enabled.Contains("my-term"));
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("")]
    [InlineData("x/y")]
    [InlineData("привет")]
    [InlineData("CLEAN")]
    [InlineData("shuffle")]
    public void SaveAs_RejectsBadOrTakenNames(string name)
    {
        var m = Model(Defaults());
        Assert.NotNull(m.ValidatePresetName(name));
        Assert.NotNull(m.SaveAsPreset("terminal", name));
    }

    [Fact]
    public void Delete_BuiltinRefused_CustomRemovedWithLists()
    {
        var cfg = Defaults();
        cfg.Filters.Presets["mine"] = Json("""[ {"id":"crt"} ]""");
        var m = Model(cfg);
        m.SetPreset("mine", Pick.Favorite);

        Assert.False(m.DeletePreset("clean"));
        Assert.True(cfg.Filters.Presets.ContainsKey("clean"));

        Assert.True(m.DeletePreset("mine"));
        Assert.False(cfg.Filters.Presets.ContainsKey("mine"));
        Assert.DoesNotContain("mine", cfg.Filters.Enabled!);
        Assert.DoesNotContain("mine", cfg.Filters.Favorites);
    }

    [Fact]
    public void Delete_LastPresetRefused()
    {
        var cfg = Defaults();
        cfg.Filters.Presets.Clear();
        cfg.Filters.Presets["solo"] = Json("""[ {"id":"crt"} ]""");
        cfg.Filters.Active = "solo";
        var m = Model(cfg);
        Assert.False(m.DeletePreset("solo"));
    }

    [Fact]
    public void Modified_AndUnwritten_Markers()
    {
        var cfg = Defaults();
        // Свой пресет в файле: кривизна теперь задаёт экран (g2), поэтому у «clean» из встроенного конфига её нет
        cfg.Filters.Presets["clean"] = Json("""[ {"id":"crt","curvature":0.04} ]""");
        var m = Model(cfg);
        Assert.False(m.IsPresetModified("clean"));
        Assert.False(m.HasUnwrittenPresetChanges());

        m.SetPresetParam("clean", 0, Curvature, 0.2);
        Assert.True(m.IsPresetModified("clean"));
        Assert.True(m.HasUnwrittenPresetChanges());

        // Возврат к значению файла снимает метку (сравнение с файлом, а не с «сохранённым в окне»)
        m.SetPresetParam("clean", 0, Curvature, 0.04);
        Assert.False(m.IsPresetModified("clean"));
        Assert.False(m.HasUnwrittenPresetChanges());
    }

    // ---------- Параметры сцен ----------

    [Fact]
    public void SceneParam_SetAndDefault_KeyAppearsAndDisappears()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        var speed = FakeSceneParams().First(p => p.Name == "speed");

        Assert.Equal(1.0, m.SceneParamValue("fake", speed));
        m.SetSceneParam("fake", speed, 2.5);
        Assert.Equal(2.5, cfg.Scenes.Params.GetProperty("fake").GetProperty("speed").GetDouble());
        Assert.Equal(2.5, m.SceneParamValue("fake", speed));

        m.ResetSceneParam("fake", speed);
        Assert.False(cfg.Scenes.Params.TryGetProperty("fake", out _));
    }

    [Fact]
    public void SceneParam_IntRoundsAndClamps_BoolAndChoiceStored()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        var specs = FakeSceneParams();
        var count = specs.First(p => p.Name == "count");
        var glow = specs.First(p => p.Name == "glow");
        var mode = specs.First(p => p.Name == "mode");

        m.SetSceneParam("fake", count, 7.6);
        Assert.Equal(8, cfg.Scenes.Params.GetProperty("fake").GetProperty("count").GetInt32());
        m.SetSceneParam("fake", count, 99);
        Assert.Equal(10.0, m.SceneParamValue("fake", count));

        m.SetSceneParam("fake", glow, 0);
        Assert.False(cfg.Scenes.Params.GetProperty("fake").GetProperty("glow").GetBoolean());
        Assert.Equal(0.0, m.SceneParamValue("fake", glow));

        m.SetSceneParam("fake", mode, 2);
        Assert.Equal("c", cfg.Scenes.Params.GetProperty("fake").GetProperty("mode").GetString());
        Assert.Equal(2.0, m.SceneParamValue("fake", mode));
        m.SetSceneParam("fake", mode, 1); // умолчание
        Assert.False(cfg.Scenes.Params.GetProperty("fake").TryGetProperty("mode", out _));
    }

    [Fact]
    public void SceneParams_AllDefault_RemovesOnlyKnownKeys()
    {
        var cfg = Defaults();
        cfg.Scenes.Params = Json("""{ "fake": { "speed": 2.5, "count": 7, "unknown": "x" }, "matrix": { "density": 0.3 } }""");
        var m = Model(cfg);

        m.ResetSceneParams("fake");

        var fake = cfg.Scenes.Params.GetProperty("fake");
        Assert.False(fake.TryGetProperty("speed", out _));
        Assert.False(fake.TryGetProperty("count", out _));
        Assert.Equal("x", fake.GetProperty("unknown").GetString());
        Assert.Equal(0.3, cfg.Scenes.Params.GetProperty("matrix").GetProperty("density").GetDouble());
    }

    [Fact]
    public void SceneParams_SurviveSaveAndLoad()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        m.SetSceneParam("fake", FakeSceneParams().First(p => p.Name == "speed"), 1.75);
        m.SetPresetParam("terminal", 2, Curvature, 0.2);
        cfg.Screen.Trail = 0.5f;
        var path = Path.Combine(Path.GetTempPath(), "phosphor-params-tests-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            m.Save(path);
            var back = PhosphorConfig.Parse(File.ReadAllText(path));
            Assert.Equal(1.75, back.Scenes.Params.GetProperty("fake").GetProperty("speed").GetDouble());
            Assert.Equal(Canon(cfg.Filters.Presets["terminal"]), Canon(back.Filters.Presets["terminal"]));
            Assert.Equal(0.5f, back.Screen.Trail);
        }
        finally
        {
            File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        }
    }

    [Fact]
    public void SettingsJson_RoundTripsConfig()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        m.SetSceneParam("fake", FakeSceneParams().First(p => p.Name == "count"), 4);
        var back = PhosphorConfig.Parse(SettingsJson.Serialize(cfg));
        Assert.Equal(4, back.Scenes.Params.GetProperty("fake").GetProperty("count").GetInt32());
        Assert.Equal(Canon(cfg.Filters.Presets["rave"]), Canon(back.Filters.Presets["rave"]));
    }

    [Fact]
    public void PreviewArguments_WithConfig_ParsesBack()
    {
        string line = SettingsModel.PreviewArguments(99, "fake", "terminal", "C:\\tmp\\phosphor-preview-1.json");
        Assert.Equal("/p 99 --scene fake --preset terminal --config C:\\tmp\\phosphor-preview-1.json", line);
        Assert.Equal("/p 99 --scene fake --preset terminal", SettingsModel.PreviewArguments(99, "fake", "terminal"));
    }

    private static List<SceneParamSpec> FakeSceneParams() => new FakeScene().Params.ToList();
}
