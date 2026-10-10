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

    /// <summary>Параметры оверлея или бирки по id (IOverlayParams.Params); null — не реализует IOverlayParams.</summary>
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
        var badges = new Lazy<IReadOnlyDictionary<string, Func<IBadge>>>(() => Catalog.Badges());
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
                // Оверлеи и бирки (часы, логотип) ищутся в одном месте: оба реализуют IOverlayParams
                if (overlays.Value.TryGetValue(id, out var make))
                {
                    var overlay = make();
                    try { return (overlay as IOverlayParams)?.Params; }
                    finally { (overlay as IDisposable)?.Dispose(); }
                }
                if (badges.Value.TryGetValue(id, out var makeBadge))
                {
                    var badge = makeBadge();
                    try { return (badge as IOverlayParams)?.Params; }
                    finally { (badge as IDisposable)?.Dispose(); }
                }
                return null;
            });
    }
}
