using System.Text.Json;
using Phosphor.Core;

namespace Phosphor.App.Tests;

/// <summary>Оптика сцены (ISceneOptics): стадии сцены идут перед стадиями пресета.</summary>
public class SceneOpticsTests
{
    private static JsonElement Arr(string json) => JsonDocument.Parse(json).RootElement;

    private static string[] Ids(JsonElement stages) =>
        stages.EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToArray();

    [Fact]
    public void OpticsGoBeforePreset()
    {
        var merged = SceneOptics.Merge("""[{"id":"glow","strength":1.2}]""", Arr("""[{"id":"vhs"},{"id":"crt"}]"""), true);
        Assert.Equal(new[] { "glow", "vhs", "crt" }, Ids(merged));
        Assert.Equal(1.2f, merged[0].GetProperty("strength").GetSingle());
    }

    [Fact]
    public void DisabledByParam_OnlyPreset()
    {
        var merged = SceneOptics.Merge("""[{"id":"glow"}]""", Arr("""[{"id":"crt"}]"""), false);
        Assert.Equal(new[] { "crt" }, Ids(merged));
    }

    [Fact]
    public void NoPreset_OpticsAlone()
    {
        var merged = SceneOptics.Merge("""[{"id":"glow"}]""", default, true);
        Assert.Equal(new[] { "glow" }, Ids(merged));
    }

    [Fact]
    public void BrokenOptics_PresetAndWarning()
    {
        var warns = new List<string>();
        var merged = SceneOptics.Merge("{oops", Arr("""[{"id":"crt"}]"""), true, warns.Add);
        Assert.Equal(new[] { "crt" }, Ids(merged));
        Assert.Single(warns);
    }

    [Fact]
    public void NixieAndDivergenceGlow()
    {
        foreach (IScene s in new IScene[] { new Phosphor.Scenes.NixieScene(), new Phosphor.Scenes.DivergenceScene() })
        {
            var optics = Assert.IsAssignableFrom<ISceneOptics>(s);
            Assert.Equal(new[] { "glow" }, Ids(Arr(optics.OpticsStages)));
        }
    }
}
