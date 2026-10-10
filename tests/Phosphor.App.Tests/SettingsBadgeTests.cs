using System.Runtime.ExceptionServices;
using System.Text.Json;
using Phosphor.App.Settings;
using Phosphor.Core;

namespace Phosphor.App.Tests;

/// <summary>
/// Бирки монитора (часы, логотип) на вкладке «Оверлеи»: модель и строки окна. Описания — фейковые (каталог бирок может быть пуст).
/// </summary>
public class SettingsBadgeTests
{
    private static readonly string[] Catalog = { "matrix", "fake" };

    /// <summary>Фейковые параметры часов: формат, позиция (индексы как у ТЗ), размер, защита от выгорания.</summary>
    private static readonly IReadOnlyList<SceneParamSpec> ClockParams = new[]
    {
        new SceneParamSpec("format", SceneParamKind.Choice, 0, 0, 2, "формат (ЧЧ:ММ / ЧЧ:ММ:СС / ДД.ММ ЧЧ:ММ)", new[] { "hm", "hms", "dhm" }),
        new SceneParamSpec("position", SceneParamKind.Choice, 8, 0, 8, "позиция", new[] { "tl", "tc", "tr", "ml", "mc", "mr", "bl", "bc", "br" }),
        new SceneParamSpec("size", SceneParamKind.Float, 0.06f, 0.02f, 0.3f, "размер (доля высоты экрана)"),
        new SceneParamSpec("drift", SceneParamKind.Bool, 1, 0, 1, "защита от выгорания"),
    };

    /// <summary>Фейковые параметры логотипа: источник (phosphor / file / text), непрозрачность, слой.</summary>
    private static readonly IReadOnlyList<SceneParamSpec> LogoParams = new[]
    {
        new SceneParamSpec("source", SceneParamKind.Choice, 0, 0, 2, "источник", new[] { "phosphor", "file", "text" }),
        new SceneParamSpec("opacity", SceneParamKind.Float, 0.5f, 0.1f, 1, "непрозрачность"),
        new SceneParamSpec("layer", SceneParamKind.Choice, 1, 0, 1, "слой", new[] { "over", "under" }),
    };

    private static PhosphorConfig Defaults() => PhosphorConfig.Parse(PhosphorConfig.DefaultJson);

    private static SettingsModel Model(PhosphorConfig cfg) => new(cfg, Catalog,
        new SettingsSpecs(_ => null, _ => null, id => id == "clock" ? ClockParams : id == "logo" ? LogoParams : null));

    private static SceneParamSpec Spec(IReadOnlyList<SceneParamSpec> list, string name) => list.First(s => s.Name == name);

    /// <summary>WinForms-контролы — на STA-потоке. Исключение пробрасывается с исходным стеком.</summary>
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { error = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    // ---------- Список и вкл / выкл ----------

    [Fact]
    public void List_HasClockAndLogo_WithHumanTitles()
    {
        Assert.Equal("Часы", SettingsModel.OverlayTitle("clock"));
        Assert.Equal("Логотип", SettingsModel.OverlayTitle("logo"));
    }

    [Fact]
    public void Toggle_ClockAndLogo_FlipOverlayClockAndLogo()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        bool clock = cfg.Overlay.Clock, logo = cfg.Overlay.Logo;

        m.ToggleOverlay("clock");
        Assert.Equal(!clock, cfg.Overlay.Clock);
        Assert.Equal(!clock ? Pick.On : Pick.Off, m.GetOverlay("clock"));
        Assert.Equal(logo, cfg.Overlay.Logo);

        m.ToggleOverlay("logo");
        Assert.Equal(!logo, cfg.Overlay.Logo);
        Assert.Equal(!logo ? Pick.On : Pick.Off, m.GetOverlay("logo"));
    }

    [Fact]
    public void PreviewArguments_ForBadge_CarriesOverlayId()
    {
        Assert.Equal("/p 5 --scene fake --overlay clock", SettingsModel.PreviewArguments(5, "fake", null, null, "clock"));
        Assert.Equal("/p 5 --scene fake --overlay logo", SettingsModel.PreviewArguments(5, "fake", null, null, "logo"));
    }

    // ---------- Параметры (через Params) ----------

    [Fact]
    public void ClockPosition_Zero_WritesTl_AndDefaultRemovesKey()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        var pos = Spec(ClockParams, "position");

        m.SetOverlayParam("clock", pos, 0);
        Assert.Equal("tl", cfg.Overlay.Params.GetProperty("clock").GetProperty("position").GetString());
        Assert.Equal(0.0, m.OverlayParamValue("clock", pos));

        m.ResetOverlayParam("clock", pos);
        Assert.False(cfg.Overlay.Params.TryGetProperty("clock", out _));
    }

    [Fact]
    public void LogoSource_WritesOptionName()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        m.SetOverlayParam("logo", Spec(LogoParams, "source"), 1);
        Assert.Equal("file", cfg.Overlay.Params.GetProperty("logo").GetProperty("source").GetString());
    }

    // ---------- Строки (не из Params) ----------

    [Fact]
    public void ClockFont_WritesString_EmptyRemovesKeyAndObject()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        Assert.Equal("", m.OverlayString("clock", "font"));

        m.SetOverlayString("clock", "font", "Iosevka");
        Assert.Equal("Iosevka", cfg.Overlay.Params.GetProperty("clock").GetProperty("font").GetString());
        Assert.Equal("Iosevka", m.OverlayString("clock", "font"));

        m.SetOverlayString("clock", "font", "");
        Assert.False(cfg.Overlay.Params.TryGetProperty("clock", out _));
    }

    [Fact]
    public void LogoText_And_LogoFile_WriteAndRemove()
    {
        var cfg = Defaults();
        var m = Model(cfg);

        m.SetOverlayString("logo", "text", "HELLO");
        m.SetOverlayString("logo", "file", "C:\\logos\\a.png");
        Assert.Equal("HELLO", cfg.Overlay.Params.GetProperty("logo").GetProperty("text").GetString());
        Assert.Equal("C:\\logos\\a.png", cfg.Overlay.Params.GetProperty("logo").GetProperty("file").GetString());

        m.SetOverlayString("logo", "text", "");
        m.SetOverlayString("logo", "file", "");
        Assert.False(cfg.Overlay.Params.TryGetProperty("logo", out _));
    }

    [Fact]
    public void LogoString_KeepsOtherLogoParams()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        m.SetOverlayParam("logo", Spec(LogoParams, "layer"), 0);
        m.SetOverlayString("logo", "text", "X");
        m.SetOverlayString("logo", "text", "");
        Assert.Equal("over", cfg.Overlay.Params.GetProperty("logo").GetProperty("layer").GetString());
    }

    [Fact]
    public void ResetLogo_RemovesKnownKeys_KeepsUnknown()
    {
        var cfg = Defaults();
        cfg.Overlay.Params = JsonDocument.Parse("""{ "logo": { "text": "X", "file": "a.png", "font": "Iosevka", "opacity": 0.7, "custom": 3 } }""").RootElement.Clone();
        var m = Model(cfg);

        m.ResetOverlay("logo");
        var logo = cfg.Overlay.Params.GetProperty("logo");
        Assert.False(logo.TryGetProperty("text", out _));
        Assert.False(logo.TryGetProperty("file", out _));
        Assert.False(logo.TryGetProperty("font", out _));
        Assert.False(logo.TryGetProperty("opacity", out _));
        Assert.Equal(3, logo.GetProperty("custom").GetInt32());
    }

    [Fact]
    public void ResetClock_RemovesFont()
    {
        var cfg = Defaults();
        cfg.Overlay.Params = JsonDocument.Parse("""{ "clock": { "font": "Iosevka", "drift": false } }""").RootElement.Clone();
        var m = Model(cfg);

        m.ResetOverlay("clock");
        Assert.False(cfg.Overlay.Params.TryGetProperty("clock", out _));
    }

    // ---------- Активность полей логотипа по источнику ----------

    [Fact]
    public void LogoFields_FollowSource_FileOnlyForFile_TextAndFontOnlyForText()
    {
        var cfg = Defaults();
        var m = Model(cfg);
        var src = Spec(LogoParams, "source");

        m.SetOverlayParam("logo", src, 0); // phosphor
        Assert.False(m.OverlayFieldActive("logo", "file"));
        Assert.False(m.OverlayFieldActive("logo", "text"));
        Assert.False(m.OverlayFieldActive("logo", "font"));

        m.SetOverlayParam("logo", src, 1); // file
        Assert.True(m.OverlayFieldActive("logo", "file"));
        Assert.False(m.OverlayFieldActive("logo", "text"));
        Assert.False(m.OverlayFieldActive("logo", "font"));

        m.SetOverlayParam("logo", src, 2); // text
        Assert.False(m.OverlayFieldActive("logo", "file"));
        Assert.True(m.OverlayFieldActive("logo", "text"));
        Assert.True(m.OverlayFieldActive("logo", "font"));
    }

    [Fact]
    public void ClockFields_AreAlwaysActive()
    {
        var m = Model(Defaults());
        Assert.True(m.OverlayFieldActive("clock", "font"));
    }

    // ---------- Каталог ----------

    [Fact]
    public void Catalog_UnknownBadge_HasNoParams()
    {
        var specs = SettingsSpecs.FromCatalog();
        Assert.Null(specs.Overlay("нет-такой-бирки"));
    }

    // ---------- Строки окна (WinForms, без показа) ----------

    [Fact]
    public void LogoGates_FollowSource_InWindowRows()
    {
        OnSta(() =>
        {
            using var log = new Phosphor.App.Log();
            var cfg = Defaults();
            var m = Model(cfg);
            var src = Spec(LogoParams, "source");
            using var form = SettingsForm.CreateForProbe(m, log);

            // Порядок ворот: шрифт, текст, файл
            m.SetOverlayParam("logo", src, 0);
            form.ProbeOverlayRows("logo");
            Assert.Equal(new[] { false, false, false }, form.ProbeOverlayGates().ToArray());

            m.SetOverlayParam("logo", src, 1);
            form.ProbeOverlayRows("logo");
            Assert.Equal(new[] { false, false, true }, form.ProbeOverlayGates().ToArray());

            m.SetOverlayParam("logo", src, 2);
            form.ProbeOverlayRows("logo");
            Assert.Equal(new[] { true, true, false }, form.ProbeOverlayGates().ToArray());
        });
    }

    [Fact]
    public void BadgeRows_BuildAtDefaultMinMax_AndAllSources_WithoutException()
    {
        var failures = new List<string>();
        OnSta(() =>
        {
            using var log = new Phosphor.App.Log();
            var cfg = Defaults();
            var m = Model(cfg);
            using var form = SettingsForm.CreateForProbe(m, log);

            foreach (var (id, specs) in new[] { ("clock", ClockParams), ("logo", LogoParams) })
                foreach (var spec in specs)
                    foreach (var v in new[] { spec.Default, spec.Min, spec.Max })
                    {
                        try
                        {
                            m.SetOverlayParam(id, spec, v);
                            form.ProbeOverlayRows(id);
                        }
                        catch (Exception e)
                        {
                            failures.Add($"{id}.{spec.Name}={v}: {e.GetType().Name}: {e.Message}");
                        }
                    }

            foreach (var src in new double[] { 0, 1, 2 })
                foreach (var text in new[] { "", "PHOSPHOR", "Ж" })
                {
                    try
                    {
                        m.SetOverlayParam("logo", Spec(LogoParams, "source"), src);
                        m.SetOverlayString("logo", "text", text);
                        m.SetOverlayString("logo", "file", text.Length > 0 ? "C:\\нет\\файла.png" : "");
                        m.SetOverlayString("logo", "font", text);
                        form.ProbeOverlayRows("logo");
                    }
                    catch (Exception e)
                    {
                        failures.Add($"logo source={src} text=«{text}»: {e.GetType().Name}: {e.Message}");
                    }
                }
        });
        Assert.Empty(failures);
    }
}
