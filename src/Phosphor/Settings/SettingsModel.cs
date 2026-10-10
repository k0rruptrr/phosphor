using System.Text.Json;
using Phosphor.Core;

namespace Phosphor.App.Settings;

/// <summary>Состояние элемента списка: выкл / вкл / любимый (любимый всегда включён).</summary>
public enum Pick { Off, On, Favorite }

/// <summary>
/// Логика окна настроек без UI. Правится загруженный PhosphorConfig целиком, поэтому поля,
/// которых в окне нет (пресеты, params, network.news и т.д.), при сохранении не теряются.
/// Инвариант: Favorites ⊆ Enabled (для сцен и для пресетов).
/// </summary>
public sealed partial class SettingsModel
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    public PhosphorConfig Config { get; }

    /// <summary>Сцены каталога (в порядке каталога). Сцены из конфига, которых в каталоге нет, сохраняются как есть.</summary>
    public IReadOnlyList<string> SceneIds { get; }

    public SettingsModel(PhosphorConfig config, IEnumerable<string> sceneCatalog, SettingsSpecs? specs = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        SceneIds = sceneCatalog.Distinct(Cmp).ToList();
        _specs = specs ?? SettingsSpecs.FromCatalog();

        // Приводим к инварианту Favorites ⊆ Enabled (конфиг мог быть написан руками)
        var sc = Config.Scenes;
        sc.Favorites = sc.Favorites.Where(f => Has(sc.Enabled, f)).ToList();
        var fl = Config.Filters;
        if (fl.Enabled is not null)
            fl.Favorites = fl.Favorites.Where(f => Has(fl.Enabled, f)).ToList();

        // Снимок пресетов «как в файле» — для метки «изменён*» и вопроса при закрытии
        foreach (var (name, el) in Config.Filters.Presets) _loadedPresets[name] = el.Clone();
    }

    public IReadOnlyList<string> PresetNames => Config.Filters.Presets.Keys.ToList();

    // ---------- Сцены ----------

    public Pick GetScene(string id)
    {
        if (!Has(Config.Scenes.Enabled, id)) return Pick.Off;
        return Has(Config.Scenes.Favorites, id) ? Pick.Favorite : Pick.On;
    }

    /// <summary>Цикл Off → On → Favorite → Off. Возвращает новое состояние.</summary>
    public Pick CycleScene(string id)
    {
        var next = Next(GetScene(id));
        SetScene(id, next);
        return next;
    }

    public void SetScene(string id, Pick pick)
    {
        var sc = Config.Scenes;
        switch (pick)
        {
            case Pick.Off:
                Remove(sc.Enabled, id);
                Remove(sc.Favorites, id);
                break;
            case Pick.On:
                Add(sc.Enabled, id);
                Remove(sc.Favorites, id);
                break;
            default:
                Add(sc.Enabled, id);
                Add(sc.Favorites, id);
                break;
        }
    }

    /// <summary>«Все вкл» (on=true): каталожные сцены включаются, любимые сохраняются. «Все выкл»: всё выключено (любимых нет).</summary>
    public void SetAllScenes(bool on)
    {
        foreach (var id in SceneIds)
        {
            if (on) Add(Config.Scenes.Enabled, id);
            else SetScene(id, Pick.Off);
        }
    }

    // ---------- Пресеты фильтров ----------

    public Pick GetPreset(string name)
    {
        if (!Config.Filters.Presets.ContainsKey(name)) return Pick.Off;
        var fl = Config.Filters;
        bool enabled = fl.Enabled is null || Has(fl.Enabled, name);
        if (!enabled) return Pick.Off;
        return Has(fl.Favorites, name) ? Pick.Favorite : Pick.On;
    }

    public Pick CyclePreset(string name)
    {
        var next = Next(GetPreset(name));
        SetPreset(name, next);
        return next;
    }

    public void SetPreset(string name, Pick pick)
    {
        var fl = Config.Filters;
        // Enabled == null означает «все пресеты»; при первом изменении фиксируем явный список
        fl.Enabled ??= Config.Filters.Presets.Keys.ToList();
        switch (pick)
        {
            case Pick.Off:
                Remove(fl.Enabled, name);
                Remove(fl.Favorites, name);
                break;
            case Pick.On:
                Add(fl.Enabled, name);
                Remove(fl.Favorites, name);
                break;
            default:
                Add(fl.Enabled, name);
                Add(fl.Favorites, name);
                break;
        }
    }

    public void SetAllPresets(bool on)
    {
        foreach (var name in PresetNames)
        {
            if (on)
            {
                Config.Filters.Enabled ??= PresetNames.ToList();
                Add(Config.Filters.Enabled, name);
            }
            else SetPreset(name, Pick.Off);
        }
    }

    // ---------- Проверка и сохранение ----------

    /// <summary>Что мешает сохранению (текст для пользователя) или null, если всё в порядке.</summary>
    public string? Problem()
    {
        if (!SceneIds.Any(id => Has(Config.Scenes.Enabled, id)))
            return "Включите хотя бы одну сцену.";
        if (PresetNames.Count == 0 || !PresetNames.Any(p => GetPreset(p) != Pick.Off))
            return "Включите хотя бы один фильтр.";
        return null;
    }

    /// <summary>Все сцены каталога «видены» (окно показало их пользователю). Новые сцены не включаются.</summary>
    public void MarkSeen()
    {
        var seen = Config.Scenes.Seen is null ? new List<string>(Config.Scenes.Enabled) : Config.Scenes.Seen;
        foreach (var id in SceneIds) Add(seen, id);
        Config.Scenes.Seen = seen;
    }

    /// <summary>Пометить сцены виденными и записать конфиг. Исключения ввода-вывода — наверх.</summary>
    public void Save(string path)
    {
        MarkSeen();
        Config.Save(path);
    }

    // ---------- Превью ----------

    /// <summary>Пресет для превью: выбранный на вкладке, либо первый включённый (для «случайного»/неизвестного).</summary>
    public string? PreviewPreset(string? chosen)
    {
        if (!string.IsNullOrEmpty(chosen)
            && !string.Equals(chosen, FiltersConfig.ShuffleActive, StringComparison.OrdinalIgnoreCase)
            && Config.Filters.Presets.ContainsKey(chosen))
            return chosen;
        return PresetNames.FirstOrDefault(p => GetPreset(p) != Pick.Off) ?? PresetNames.FirstOrDefault();
    }

    /// <summary>
    /// Аргументы дочернего процесса превью: «/p HWND --scene ID [--preset NAME] [--config PATH]» (как Windows вызывает заставку).
    /// --config — временный конфиг окна (текущие правки, не phosphor.json).
    /// </summary>
    public static string PreviewArguments(nint parentHwnd, string scene, string? preset, string? configPath = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("/p ").Append(parentHwnd.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(" --scene ").Append(Quote(scene));
        if (!string.IsNullOrEmpty(preset)) sb.Append(" --preset ").Append(Quote(preset));
        if (!string.IsNullOrEmpty(configPath)) sb.Append(" --config ").Append(Quote(configPath));
        return sb.ToString();
    }

    private static string Quote(string s) =>
        s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? s : "\"" + s.Replace("\"", "\\\"") + "\"";

    // ---------- Общее ----------

    private static Pick Next(Pick p) => p switch
    {
        Pick.Off => Pick.On,
        Pick.On => Pick.Favorite,
        _ => Pick.Off,
    };

    private static bool Has(IEnumerable<string>? list, string id) => list is not null && list.Any(x => Cmp.Equals(x, id));

    private static void Add(List<string> list, string id)
    {
        if (!Has(list, id)) list.Add(id);
    }

    private static void Remove(List<string> list, string id) => list.RemoveAll(x => Cmp.Equals(x, id));
}
