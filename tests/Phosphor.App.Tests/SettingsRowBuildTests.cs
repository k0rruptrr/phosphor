using System.Runtime.ExceptionServices;
using System.Text.Json;
using Phosphor.App.Settings;
using Phosphor.Core;
using Phosphor.Gfx;

namespace Phosphor.App.Tests;

/// <summary>
/// Построение строк параметров окна настроек для каждой сцены и фильтра каталога: при Default, Min и Max — без исключений.
/// Окно не показывается; строки строятся тем же путём, что у окна (ProbeSceneParamRow / ProbeFilterParamRow).
/// </summary>
public class SettingsRowBuildTests
{
    private const string Preset = "probe";

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

    private static PhosphorConfig Defaults() => PhosphorConfig.Parse(PhosphorConfig.DefaultJson);

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static IEnumerable<(string Where, double Value)> Values(string name, float def, float min, float max)
    {
        yield return ($"{name}=Default", def);
        yield return ($"{name}=Min", min);
        yield return ($"{name}=Max", max);
    }

    [Fact]
    public void EveryCatalogSceneParam_BuildsRow_AtDefaultMinMax_WithoutException()
    {
        var failures = new List<string>();
        OnSta(() =>
        {
            using var log = new Phosphor.App.Log();
            var specs = SettingsSpecs.FromCatalog();
            var cfg = Defaults();
            var model = new SettingsModel(cfg, Catalog.Scenes().Ids, specs);
            using var form = SettingsForm.CreateForProbe(model, log);
            foreach (var sceneId in Catalog.Scenes().Ids.OrderBy(s => s, StringComparer.Ordinal))
            {
                var list = specs.Scene(sceneId);
                if (list is null) continue;
                foreach (var spec in list)
                    foreach (var (where, value) in Values(spec.Name, spec.Default, spec.Min, spec.Max))
                    {
                        try
                        {
                            model.SetSceneParam(sceneId, spec, value);
                            using var row = form.ProbeSceneParamRow(sceneId, spec);
                        }
                        catch (Exception e)
                        {
                            failures.Add($"сцена «{sceneId}», {where} [{spec.Kind}, {spec.Min}..{spec.Max}]: {e.GetType().Name}: {e.Message}");
                        }
                    }
            }
        });
        Assert.Empty(failures);
    }

    [Fact]
    public void EveryCatalogFilterParam_BuildsRow_AtDefaultMinMax_WithoutException()
    {
        var failures = new List<string>();
        OnSta(() =>
        {
            using var log = new Phosphor.App.Log();
            var specs = SettingsSpecs.FromCatalog();
            var cfg = Defaults();
            var model = new SettingsModel(cfg, Catalog.Scenes().Ids, specs);
            using var form = SettingsForm.CreateForProbe(model, log);
            foreach (var filterId in Catalog.Filters().Ids.OrderBy(s => s, StringComparer.Ordinal))
            {
                cfg.Filters.Presets[Preset] = Json($"[ {{ \"id\": \"{filterId}\" }} ]");
                var stages = model.PresetStages(Preset);
                if (stages.Count == 0) continue;
                var stage = stages[0];
                foreach (var spec in stage.Params)
                    foreach (var (where, value) in Values(spec.Name, spec.Default, spec.Min, spec.Max))
                    {
                        try
                        {
                            model.SetPresetParam(Preset, stage.Index, spec, value);
                            using var row = form.ProbeFilterParamRow(Preset, stage.Index, spec);
                        }
                        catch (Exception e)
                        {
                            failures.Add($"фильтр «{filterId}», {where} [{spec.Min}..{spec.Max}]: {e.GetType().Name}: {e.Message}");
                        }
                    }
            }
        });
        Assert.Empty(failures);
    }

    [Fact]
    public void EveryScreenParam_BuildsRow_AtDefaultMinMax_WithoutException()
    {
        var failures = new List<string>();
        OnSta(() =>
        {
            using var log = new Phosphor.App.Log();
            var cfg = Defaults();
            var model = new SettingsModel(cfg, Catalog.Scenes().Ids, SettingsSpecs.FromCatalog());
            using var form = SettingsForm.CreateForProbe(model, log);
            foreach (var spec in ScreenConfig.Specs)
                foreach (var (where, value) in Values(spec.Name, spec.Default, spec.Min, spec.Max))
                {
                    try
                    {
                        model.SetScreenParam(spec.Name, value);
                        using var row = form.ProbeScreenParamRow(spec);
                    }
                    catch (Exception e)
                    {
                        failures.Add($"экран «{spec.Name}», {where} [{spec.Kind}, {spec.Min}..{spec.Max}]: {e.GetType().Name}: {e.Message}");
                    }
                }
        });
        Assert.Empty(failures);
    }

    /// <summary>Фейковые описания оверлея: по одному параметру каждого вида (каталог оверлеев пока может не отдавать параметров).</summary>
    private static readonly IReadOnlyList<SceneParamSpec> FakeOverlayParams = new[]
    {
        new SceneParamSpec("accent", SceneParamKind.Choice, 0, 0, 3, "цвет акцента", new[] { "orange", "cyan", "green", "white" }),
        new SceneParamSpec("targets", SceneParamKind.Int, 4, 0, 4, "рамки трекинга"),
        new SceneParamSpec("stats", SceneParamKind.Bool, 1, 0, 1, "блок статистики"),
        new SceneParamSpec("stroke", SceneParamKind.Float, 2, 0, 4, "толщина"),
    };

    [Fact]
    public void EveryOverlayParam_AndOverlayRows_BuildAtDefaultMinMax_WithoutException()
    {
        var failures = new List<string>();
        OnSta(() =>
        {
            using var log = new Phosphor.App.Log();
            var cfg = Defaults();
            var specs = new SettingsSpecs(_ => null, _ => null, id => id == "hud" ? FakeOverlayParams : null);
            var model = new SettingsModel(cfg, Catalog.Scenes().Ids, specs);
            using var form = SettingsForm.CreateForProbe(model, log);
            foreach (var spec in FakeOverlayParams)
                foreach (var (where, value) in Values(spec.Name, spec.Default, spec.Min, spec.Max))
                {
                    try
                    {
                        model.SetOverlayParam("hud", spec, value);
                        using var row = form.ProbeOverlayParamRow("hud", spec);
                    }
                    catch (Exception e)
                    {
                        failures.Add($"оверлей «hud», {where} [{spec.Kind}, {spec.Min}..{spec.Max}]: {e.GetType().Name}: {e.Message}");
                    }
                }
            // Строки HUD (вероятность) и VCR (шрифт OSD) — при крайних значениях
            foreach (var p in new[] { 0.0, 0.4, 1.0 })
            {
                try
                {
                    model.OverlayProbability = p;
                    using var row = form.ProbeOverlayProbabilityRow();
                }
                catch (Exception e) { failures.Add($"вероятность {p}: {e.GetType().Name}: {e.Message}"); }
            }
            foreach (var font in new[] { "", "НеСуществующий шрифт" })
            {
                try
                {
                    model.SetOverlayFont(font);
                    using var row = form.ProbeOsdFontRow();
                }
                catch (Exception e) { failures.Add($"шрифт OSD «{font}»: {e.GetType().Name}: {e.Message}"); }
            }
        });
        Assert.Empty(failures);
    }

    [Fact]
    public void SliderBox_RangeChanges_DoNotThrow_ValueClampsToNewRange()
    {
        OnSta(() =>
        {
            using var s = new SliderBox();
            s.Minimum = 50f;
            s.Maximum = 1000f;
            s.Value = 300f;
            Assert.Equal(300f, s.Value);

            s.Minimum = 0f;
            s.Maximum = 1f; // значение 300 клэмпится к новому максимуму
            Assert.Equal(1f, s.Value);

            s.Minimum = -30f;
            s.Maximum = 30f;
            s.Value = -100f;
            Assert.Equal(-30f, s.Value);
            s.Value = 100f;
            Assert.Equal(30f, s.Value);
        });
    }

    [Fact]
    public void SliderBox_SetRange_IsAtomic_AndSwapsReversedBounds()
    {
        OnSta(() =>
        {
            using var s = new SliderBox();
            s.SetRange(50f, 1000f, 300f);
            Assert.Equal(50f, s.Minimum);
            Assert.Equal(1000f, s.Maximum);
            Assert.Equal(300f, s.Value);

            s.SetRange(0f, 1f, 300f); // значение клэмпится к новому диапазону
            Assert.Equal(1f, s.Value);

            s.SetRange(30f, -30f, 0f); // min > max: меняем местами
            Assert.Equal(-30f, s.Minimum);
            Assert.Equal(30f, s.Maximum);
            Assert.Equal(0f, s.Value);
        });
    }
}
