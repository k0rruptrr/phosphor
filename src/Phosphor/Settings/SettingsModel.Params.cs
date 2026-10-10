using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Phosphor.Core;
using Phosphor.Gfx;

namespace Phosphor.App.Settings;

/// <summary>
/// Правка параметров сцен (scenes.params.&lt;id&gt;) и стадий пресетов фильтров. Пишет прямо в Config;
/// поля, которых окно не знает (mod, неизвестные ключи), сохраняются. Значение, равное умолчанию, — ключ удаляется.
/// </summary>
public sealed partial class SettingsModel
{
    private static readonly Regex PresetNameRx = new("^[A-Za-z0-9-]{1,32}$", RegexOptions.CultureInvariant);
    private static readonly Lazy<PhosphorConfig> BuiltinConfig = new(() => PhosphorConfig.Parse(PhosphorConfig.DefaultJson));

    private readonly SettingsSpecs _specs;
    /// <summary>Пресеты, как они лежат в файле (на момент открытия окна). Метка «изменён*» и вопрос при закрытии — относительно них.</summary>
    private readonly Dictionary<string, JsonElement> _loadedPresets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Стадия пресета: индекс в массиве, id фильтра и его параметры (пусто — стадия без настроек).</summary>
    public sealed record StageView(int Index, string Id, IReadOnlyList<ParamSpec> Params);

    // ---------- Параметры сцен ----------

    /// <summary>Описание параметров сцены или null (сцена не реализует ISceneParams).</summary>
    public IReadOnlyList<SceneParamSpec>? SceneSpecs(string sceneId) => _specs.Scene(sceneId);

    /// <summary>Текущее значение параметра сцены: из конфига или умолчание. Float/Int — число, Bool — 0/1, Choice — индекс в Options.</summary>
    public double SceneParamValue(string sceneId, SceneParamSpec spec) => KeyedValue(Config.Scenes.Params, sceneId, spec);

    /// <summary>Записать значение параметра сцены (Float/Int — число, Bool — 0/1, Choice — индекс). Значение по умолчанию — ключ удаляется.</summary>
    public void SetSceneParam(string sceneId, SceneParamSpec spec, double value) =>
        Config.Scenes.Params = KeyedSet(Config.Scenes.Params, sceneId, spec, value);

    /// <summary>«По умолчанию» у одного параметра: ключ удаляется.</summary>
    public void ResetSceneParam(string sceneId, SceneParamSpec spec) => SetSceneParam(sceneId, spec, spec.Default);

    /// <summary>«Все по умолчанию»: удаляются ключи параметров сцены из её описания (неизвестные ключи остаются).</summary>
    public void ResetSceneParams(string sceneId)
    {
        var specs = SceneSpecs(sceneId);
        if (specs is null || specs.Count == 0) return;
        Config.Scenes.Params = KeyedReset(Config.Scenes.Params, sceneId, specs);
    }

    /// <summary>Значение параметра из root[key] (общее для сцен и оверлеев): из конфига или умолчание.</summary>
    private static double KeyedValue(JsonElement root, string key, SceneParamSpec spec)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(key, out var sc) && sc.ValueKind == JsonValueKind.Object
            && sc.TryGetProperty(spec.Name, out var v))
        {
            var n = ToNumber(spec, v);
            if (n is not null) return n.Value;
        }
        return spec.Default;
    }

    /// <summary>Записать root[key][spec.Name]. Умолчание — ключ удаляется; пустой объект key удаляется. Возвращает новый root.</summary>
    private static JsonElement KeyedSet(JsonElement root, string key, SceneParamSpec spec, double value)
    {
        value = NormalizeSceneValue(spec, value);
        var obj = ToObject(root);
        var scene = obj[key] as JsonObject;
        if (IsSceneDefault(spec, value))
        {
            scene?.Remove(spec.Name);
        }
        else
        {
            if (scene is null)
            {
                scene = new JsonObject();
                obj[key] = scene;
            }
            scene[spec.Name] = ToSceneNode(spec, value);
        }
        if (scene is not null && scene.Count == 0) obj.Remove(key);
        return ToElement(obj);
    }

    /// <summary>Удалить ключи параметров из описания у root[key] (неизвестные ключи остаются). Возвращает новый root.</summary>
    private static JsonElement KeyedReset(JsonElement root, string key, IReadOnlyList<SceneParamSpec> specs)
    {
        var obj = ToObject(root);
        if (obj[key] is JsonObject scene)
        {
            foreach (var s in specs) scene.Remove(s.Name);
            if (scene.Count == 0) obj.Remove(key);
        }
        return ToElement(obj);
    }

    // ---------- Стадии пресетов фильтров ----------

    /// <summary>Встроенный пресет (из PhosphorConfig.DefaultJson). Такие нельзя удалить, «Вернуть встроенный» — только для них.</summary>
    public bool IsBuiltinPreset(string name) => FindPreset(BuiltinConfig.Value.Filters.Presets, name) is not null;

    /// <summary>Ключ пресета без учёта регистра или null.</summary>
    public string? PresetKey(string name) => Config.Filters.Presets.Keys.FirstOrDefault(k => Cmp.Equals(k, name));

    /// <summary>Стадии пресета по порядку. Параметры — из IFilter.Params; у стадий без настроек (mono) список пуст.</summary>
    public IReadOnlyList<StageView> PresetStages(string name)
    {
        var key = PresetKey(name);
        var list = new List<StageView>();
        if (key is null) return list;
        var arr = PresetArray(key);
        for (int i = 0; i < arr.Count; i++)
        {
            string id = arr[i] is JsonObject st && st["id"] is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : "";
            list.Add(new StageView(i, id, _specs.Filter(id) ?? Array.Empty<ParamSpec>()));
        }
        return list;
    }

    /// <summary>Текущее значение параметра стадии: из конфига или умолчание (Options — индекс строки).</summary>
    public double PresetParamValue(string name, int stageIndex, ParamSpec spec)
    {
        var key = PresetKey(name);
        if (key is not null)
        {
            var arr = Config.Filters.Presets[key];
            if (arr.ValueKind == JsonValueKind.Array && stageIndex >= 0 && stageIndex < arr.GetArrayLength()
                && arr[stageIndex].ValueKind == JsonValueKind.Object
                && arr[stageIndex].TryGetProperty(spec.Name, out var v))
            {
                var n = ToNumber(spec, v);
                if (n is not null) return n.Value;
            }
        }
        return spec.Default;
    }

    /// <summary>Записать значение параметра стадии. Значение, равное умолчанию, — ключ удаляется. mod и прочие поля не трогаются.</summary>
    public void SetPresetParam(string name, int stageIndex, ParamSpec spec, double value)
    {
        var key = PresetKey(name) ?? throw new KeyNotFoundException($"Нет пресета '{name}'");
        var arr = PresetArray(key);
        if (stageIndex < 0 || stageIndex >= arr.Count || arr[stageIndex] is not JsonObject st) return;

        value = Math.Clamp(value, spec.Min, spec.Max);
        if (spec.Options is { Length: > 0 } opts)
        {
            int idx = Math.Clamp((int)Math.Round(value), 0, opts.Length - 1);
            if (idx == (int)Math.Round(spec.Default)) st.Remove(spec.Name);
            else st[spec.Name] = JsonValue.Create(opts[idx]);
        }
        else
        {
            value = Math.Round(value, 3);
            if (Math.Abs(value - spec.Default) < 1e-6) st.Remove(spec.Name);
            else st[spec.Name] = JsonValue.Create(value);
        }
        Config.Filters.Presets[key] = ToElement(arr);
    }

    /// <summary>«Вернуть встроенный»: значения пресета — как во встроенном конфиге. Только для встроенных имён.</summary>
    public bool RestoreBuiltinPreset(string name)
    {
        var key = PresetKey(name);
        if (key is null) return false;
        var builtin = FindPreset(BuiltinConfig.Value.Filters.Presets, key);
        if (builtin is null) return false;
        Config.Filters.Presets[key] = builtin.Value.Clone();
        return true;
    }

    /// <summary>Пресет отличается от файла на диске (метка «изменён*»). Снимается после общей «Сохранить» окна.</summary>
    public bool IsPresetModified(string name)
    {
        var key = PresetKey(name);
        if (key is null) return false;
        return !_loadedPresets.TryGetValue(key, out var file) || Canon(file) != Canon(Config.Filters.Presets[key]);
    }

    /// <summary>Есть ли изменения конфига, которых нет в файле (пресеты: правки, новые, удалённые) — для вопроса при закрытии окна.</summary>
    public bool HasUnwrittenPresetChanges()
    {
        foreach (var (name, el) in Config.Filters.Presets)
            if (!_loadedPresets.TryGetValue(name, out var file) || Canon(file) != Canon(el)) return true;
        return _loadedPresets.Keys.Any(k => PresetKey(k) is null);
    }

    /// <summary>Проверка имени нового пресета: null — годится, иначе текст для пользователя.</summary>
    public string? ValidatePresetName(string name)
    {
        name = name.Trim();
        if (!PresetNameRx.IsMatch(name)) return "Имя: латиница, цифры и дефис, до 32 знаков.";
        if (Cmp.Equals(name, FiltersConfig.ShuffleActive) || PresetKey(name) is not null) return "Такое имя уже занято.";
        return null;
    }

    /// <summary>
    /// «Сохранить как…»: новый пресет — копия текущих значений исходного. Исходный не меняется.
    /// Null — успех, иначе текст ошибки.
    /// </summary>
    public string? SaveAsPreset(string sourceName, string newName)
    {
        var problem = ValidatePresetName(newName);
        if (problem is not null) return problem;
        var srcKey = PresetKey(sourceName);
        if (srcKey is null) return "Нет исходного пресета.";
        newName = newName.Trim();

        Config.Filters.Presets[newName] = Config.Filters.Presets[srcKey].Clone();
        if (Config.Filters.Enabled is not null) Add(Config.Filters.Enabled, newName);
        return null;
    }

    /// <summary>Удалить свой пресет. Встроенные и последний пресет удалить нельзя (false).</summary>
    public bool DeletePreset(string name)
    {
        var key = PresetKey(name);
        if (key is null || IsBuiltinPreset(key) || Config.Filters.Presets.Count <= 1) return false;
        Config.Filters.Presets.Remove(key);
        if (Config.Filters.Enabled is not null) Remove(Config.Filters.Enabled, key);
        Remove(Config.Filters.Favorites, key);
        if (Cmp.Equals(Config.Filters.Active, key)) Config.Filters.Active = Config.Filters.Presets.Keys.First();
        return true;
    }

    // ---------- JSON-помощники ----------

    private JsonArray PresetArray(string key) =>
        JsonNode.Parse(Config.Filters.Presets[key].GetRawText()) as JsonArray ?? new JsonArray();

    private static JsonElement? FindPreset(IReadOnlyDictionary<string, JsonElement> presets, string name) =>
        presets.Where(kv => Cmp.Equals(kv.Key, name)).Select(kv => (JsonElement?)kv.Value).FirstOrDefault();

    private static JsonObject ToObject(JsonElement e) =>
        JsonNode.Parse(e.GetRawText()) as JsonObject ?? new JsonObject();

    private static JsonElement ToElement(JsonNode node)
    {
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }

    /// <summary>Сравнение без учёта пробелов и переносов.</summary>
    private static string Canon(JsonElement e) => JsonNode.Parse(e.GetRawText())?.ToJsonString() ?? "";

    private static double? ToNumber(ParamSpec spec, JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Number: return v.GetDouble();
            case JsonValueKind.True: return 1;
            case JsonValueKind.False: return 0;
            case JsonValueKind.String when spec.Options is not null:
                int i = Array.FindIndex(spec.Options, o => o.Equals(v.GetString(), StringComparison.OrdinalIgnoreCase));
                return i >= 0 ? i : null;
            default: return null;
        }
    }

    private static double? ToNumber(SceneParamSpec spec, JsonElement v)
    {
        switch (spec.Kind)
        {
            case SceneParamKind.Bool:
                return v.ValueKind switch
                {
                    JsonValueKind.True => 1,
                    JsonValueKind.False => 0,
                    JsonValueKind.Number => v.GetDouble() >= 0.5 ? 1 : 0,
                    _ => null,
                };
            case SceneParamKind.Choice:
                if (v.ValueKind != JsonValueKind.String || spec.Options is null) return null;
                int i = Array.FindIndex(spec.Options, o => o.Equals(v.GetString(), StringComparison.OrdinalIgnoreCase));
                return i >= 0 ? i : null;
            default:
                return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        }
    }

    private static double NormalizeSceneValue(SceneParamSpec spec, double value) => spec.Kind switch
    {
        SceneParamKind.Bool => value >= 0.5 ? 1 : 0,
        SceneParamKind.Choice => Math.Clamp(Math.Round(value), 0, Math.Max(0, (spec.Options?.Length ?? 1) - 1)),
        SceneParamKind.Int => Math.Clamp(Math.Round(value), spec.Min, spec.Max),
        _ => Math.Clamp(value, spec.Min, spec.Max),
    };

    private static bool IsSceneDefault(SceneParamSpec spec, double value) => spec.Kind switch
    {
        SceneParamKind.Bool => (value >= 0.5) == (spec.Default >= 0.5),
        SceneParamKind.Choice => (int)Math.Round(value) == (int)Math.Round(spec.Default),
        SceneParamKind.Int => Math.Round(value) == Math.Round(spec.Default),
        _ => Math.Abs(value - spec.Default) < 1e-6,
    };

    private static JsonNode ToSceneNode(SceneParamSpec spec, double value) => spec.Kind switch
    {
        SceneParamKind.Bool => JsonValue.Create(value >= 0.5),
        SceneParamKind.Choice => JsonValue.Create(spec.Options![(int)value]),
        SceneParamKind.Int => JsonValue.Create((int)Math.Round(value)),
        _ => JsonValue.Create(Math.Round(value, 4)),
    };
}
