using Phosphor.Core;
using Phosphor.Gfx;

namespace Phosphor.App.Settings;

/// <summary>
/// Источники описаний параметров для окна настроек: фильтры (IFilter.Params) и сцены (ISceneParams.Params).
/// В приложении — из каталога (рефлексия, без GL); в тестах подменяются фейками.
/// </summary>
public sealed class SettingsSpecs
{
    /// <summary>Параметры фильтра по id; null — у стадии нет настроек (например сеточный шаг mono).</summary>
    public Func<string, IReadOnlyList<ParamSpec>?> Filter { get; }

    /// <summary>Параметры сцены по id; null — сцена не реализует ISceneParams.</summary>
    public Func<string, IReadOnlyList<SceneParamSpec>?> Scene { get; }

    /// <summary>Параметры оверлея по id (IOverlayParams.Params); null — оверлей не реализует IOverlayParams.</summary>
    public Func<string, IReadOnlyList<SceneParamSpec>?> Overlay { get; }

    public SettingsSpecs(
        Func<string, IReadOnlyList<ParamSpec>?> filter,
        Func<string, IReadOnlyList<SceneParamSpec>?> scene,
        Func<string, IReadOnlyList<SceneParamSpec>?>? overlay = null)
    {
        Filter = filter;
        Scene = scene;
        Overlay = overlay ?? (_ => null);
    }

    /// <summary>Из каталога приложения. Реестры создаются при первом обращении; фильтры, сцены и оверлеи создаются без Init (GL не нужен).</summary>
    public static SettingsSpecs FromCatalog()
    {
        var filters = new Lazy<FilterRegistry>(() => Catalog.Filters());
        var scenes = new Lazy<SceneRegistry>(() => Catalog.Scenes());
        var overlays = new Lazy<IReadOnlyDictionary<string, Func<IOverlay>>>(() => Catalog.Overlays());
        return new SettingsSpecs(
            id =>
            {
                if (!filters.Value.Has(id)) return null;
                using var f = filters.Value.Create(id);
                return f.Params;
            },
            id =>
            {
                if (!scenes.Value.Has(id)) return null;
                var scene = scenes.Value.Create(id);
                try { return (scene as ISceneParams)?.Params; }
                finally { (scene as IDisposable)?.Dispose(); }
            },
            id =>
            {
                if (!overlays.Value.TryGetValue(id, out var make)) return null;
                var overlay = make();
                try { return (overlay as IOverlayParams)?.Params; }
                finally { (overlay as IDisposable)?.Dispose(); }
            });
    }
}
