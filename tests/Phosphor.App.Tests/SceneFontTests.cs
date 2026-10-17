using Phosphor.App.Settings;
using Phosphor.Core;

namespace Phosphor.App.Tests;

public class SceneFontTests
{
    private static bool Known(string f) => f is "Bedstead" or "Fixedsys Excelsior" or "Consolas";

    [Fact]
    public void ParamSet_WinsOverPreferred()
    {
        Assert.Equal("Fixedsys Excelsior", SceneFont.Resolve("Fixedsys Excelsior", "Bedstead", Known));
    }

    [Fact]
    public void ParamEmpty_UsesPreferred()
    {
        Assert.Equal("Bedstead", SceneFont.Resolve("", "Bedstead", Known));
        Assert.Equal("Bedstead", SceneFont.Resolve(null, "Bedstead", Known));
    }

    [Fact]
    public void NeitherSet_IsNull()
    {
        Assert.Null(SceneFont.Resolve("", null, Known));
        Assert.Null(SceneFont.Resolve(null, "  ", Known));
    }

    [Fact]
    public void UnknownFamily_IsNull_WithOneWarn()
    {
        var warns = new List<string>();
        Assert.Null(SceneFont.Resolve("Nope Font X1", null, Known, warns.Add));
        Assert.Null(SceneFont.Resolve("Nope Font X1", null, Known, warns.Add));
        Assert.Single(warns);
    }

    [Fact]
    public void Pick_TrimsValue()
    {
        Assert.Equal("Bedstead", SceneFont.Pick("  Bedstead ", null));
    }

    [Fact]
    public void SetSceneString_WritesAndRemovesKey()
    {
        var model = new SettingsModel(PhosphorConfig.Parse(PhosphorConfig.DefaultJson), new[] { "teletext" },
            new SettingsSpecs(_ => null, _ => null, null, id => id == "teletext" ? "Bedstead" : null));
        Assert.Equal("Bedstead", model.ScenePreferredFont("teletext"));
        Assert.Null(model.ScenePreferredFont("matrix"));

        model.SetSceneString("teletext", "font", "Fixedsys Excelsior");
        Assert.Equal("Fixedsys Excelsior", model.SceneString("teletext", "font"));
        Assert.Equal("Fixedsys Excelsior", model.Config.Scenes.Params.GetProperty("teletext").GetProperty("font").GetString());

        model.SetSceneString("teletext", "font", "");
        Assert.Equal("", model.SceneString("teletext", "font"));
        Assert.False(model.Config.Scenes.Params.TryGetProperty("teletext", out _));
    }
}
