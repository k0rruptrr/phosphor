using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phosphor.Core;

/// <summary>phosphor.json. Любое отсутствующее поле — значение по умолчанию.</summary>
public sealed class PhosphorConfig
{
    public int CellHeightPx { get; set; } = 18;
    public ScenesConfig Scenes { get; set; } = new();
    public FiltersConfig Filters { get; set; } = new();
    public ShuffleConfig Shuffle { get; set; } = new();
    public AudioConfig Audio { get; set; } = new();
    public ExitConfig Exit { get; set; } = new();
    public NetworkConfig Network { get; set; } = new();
    public OverlayConfig Overlay { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static PhosphorConfig Parse(string json) =>
        JsonSerializer.Deserialize<PhosphorConfig>(json, Json) ?? new PhosphorConfig();

    /// <summary>Загрузить из файла. Нет файла или он битый — дефолт, причина в warnings.</summary>
    public static PhosphorConfig Load(string path, List<string> warnings)
    {
        PhosphorConfig cfg;
        if (!File.Exists(path))
        {
            warnings.Add($"Конфиг не найден ({path}), использую встроенный");
            cfg = Parse(DefaultJson);
        }
        else
        {
            try { cfg = Parse(File.ReadAllText(path)); }
            catch (JsonException e)
            {
                warnings.Add($"Конфиг битый: {e.Message}. Использую встроенный");
                cfg = Parse(DefaultJson);
            }
        }
        cfg.Validate(warnings);
        return cfg;
    }

    public void Validate(List<string> warnings)
    {
        if (CellHeightPx is < 8 or > 64) { warnings.Add($"cellHeightPx={CellHeightPx} вне 8..64, ставлю 18"); CellHeightPx = 18; }
        if (Scenes.Enabled.Count == 0) { warnings.Add("Нет включённых сцен, включаю matrix"); Scenes.Enabled.Add("matrix"); }
        if (Filters.Presets.Count == 0) Filters.Presets["clean"] = JsonDocument.Parse("[]").RootElement;
        if (!Filters.Presets.ContainsKey(Filters.Active))
        {
            var first = Filters.Presets.Keys.First();
            warnings.Add($"Пресет '{Filters.Active}' не найден, беру '{first}'");
            Filters.Active = first;
        }
        Shuffle.IntervalSec = Math.Max(5, Shuffle.IntervalSec);
        Shuffle.TransitionSec = Math.Clamp(Shuffle.TransitionSec, 0, 10);
        Scenes.Speed = Math.Clamp(Scenes.Speed, 0.05f, 10f);
    }

    /// <summary>Параметры сцены id (объект из scenes.params) или пустой объект.</summary>
    public SceneOptions SceneOptionsFor(string id) => new()
    {
        Speed = Scenes.Speed,
        Palette = Scenes.Palette,
        Params = Scenes.Params.ValueKind == JsonValueKind.Object && Scenes.Params.TryGetProperty(id, out var p) ? p.Clone() : SceneOptions.Empty,
    };

    public const string DefaultJson = """
    {
      "cellHeightPx": 18,
      "scenes": {
        "enabled": ["matrix", "starfield", "pipes", "doomfire", "life", "dvd", "aquarium",
                    "teletext", "teletype", "weatherboard", "torus", "grayscott", "voxel", "boids",
                    "plasma", "rotozoom", "tunnel", "cracktro", "boing", "c64load"],
        "speed": 1.0,
        "palette": "default",
        "params": {
          "matrix": { "density": 0.6 }
        }
      },
      "filters": {
        "active": "terminal",
        "presets": {
          "clean":    [ { "id": "crt", "curvature": 0.04, "scanlines": 0.2, "bloom": 0.3 } ],
          "terminal": [ { "id": "mono" }, { "id": "phosphor", "color": "green", "persistence": 0.82 },
                        { "id": "crt", "mod": { "brightness": { "src": "breath", "amount": 0.12 } } } ],
          "amber":    [ { "id": "mono", "contrast": 1.15 }, { "id": "phosphor", "color": "amber", "persistence": 0.86, "mod": { "persistence": { "src": "night", "amount": 0.08 } } },
                        { "id": "crt", "curvature": 0.1, "mod": { "brightness": { "src": "breath", "amount": 0.1 } } } ],
          "kassette": [ { "id": "vhs", "mod": { "tracking": { "src": "fatigue", "amount": 0.4 }, "noise": { "src": "fatigue", "amount": 0.15 } } },
                        { "id": "chroma", "amount": 1.5 },
                        { "id": "crt", "curvature": 0.06 } ],
          "pal":      [ { "id": "tv", "mode": "pal" }, { "id": "crt", "curvature": 0.08 } ],
          "secam":    [ { "id": "tv", "mode": "secam" }, { "id": "crt", "curvature": 0.08 } ],
          "ntsc":     [ { "id": "tv", "mode": "ntsc" }, { "id": "crt", "curvature": 0.08 } ],
          "rave":     [ { "id": "chroma", "amount": 0.6, "mod": { "amount": { "src": "beatPulse", "amount": 1.2 } } },
                        { "id": "crt", "mod": { "bloom": { "src": "bass", "amount": 0.4 } } } ]
        }
      },
      "shuffle": {
        "scenes": true,
        "presets": true,
        "intervalSec": 90,
        "transition": "mixed",
        "transitionSec": 1.5,
        "perMonitor": true,
        "presetOnDrop": true,
        "beatSync": true,
        "phraseBeats": 64
      },
      "overlay": { "enabled": true, "id": "hud", "probability": 0.4 },
      "audio": { "enabled": true, "sensitivity": 1.0, "maxPulse": 0.8, "minBeatIntervalMs": 250 },
      "network": { "enabled": true, "city": "Москва", "news": ["tass", "bbc", "reddit:worldnews"], "teletext": ["svt", "nos"] },
      "exit": { "mouseThresholdPx": 12 }
    }
    """;
}

public sealed class ScenesConfig
{
    public List<string> Enabled { get; set; } = new() { "matrix" };
    public float Speed { get; set; } = 1f;
    public string Palette { get; set; } = "default";
    public JsonElement Params { get; set; } = SceneOptions.Empty;
}

public sealed class FiltersConfig
{
    public string Active { get; set; } = "clean";
    /// <summary>Имя пресета → JSON-массив стадий фильтров (формат см. FilterChain).</summary>
    public Dictionary<string, JsonElement> Presets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ShuffleConfig
{
    public bool Scenes { get; set; } = true;
    public bool Presets { get; set; } = true;
    public float IntervalSec { get; set; } = 90;
    /// <summary>"mixed" (дирижёр выбирает сам) | "fade" | "vhs-noise" | "cut".</summary>
    public string Transition { get; set; } = "mixed";
    public float TransitionSec { get; set; } = 1.5f;
    public bool PerMonitor { get; set; } = true;
    public bool PresetOnDrop { get; set; } = true;
    /// <summary>Под музыку сцены меняются в такт: на границе фразы, каскадом по мониторам (удар за ударом).</summary>
    public bool BeatSync { get; set; } = true;
    /// <summary>Длина фразы в ударах для смены под музыку (8..256).</summary>
    public int PhraseBeats { get; set; } = 64;
    /// <summary>Полный рандом цепочки фильтров вместо пресетов (не рекомендуется).</summary>
    public bool RandomFilterChains { get; set; } = false;
}

public sealed class AudioConfig
{
    public bool Enabled { get; set; } = true;
    public float Sensitivity { get; set; } = 1f;
    /// <summary>Потолок BeatPulse — защита от слишком резких вспышек.</summary>
    public float MaxPulse { get; set; } = 0.8f;
    public float MinBeatIntervalMs { get; set; } = 250;
}

/// <summary>Оверлей поверх сцен (разметка HUD). Дирижёр вешает его на часть сцен случайно.</summary>
public sealed class OverlayConfig
{
    public bool Enabled { get; set; } = true;
    public string Id { get; set; } = "hud";
    /// <summary>Вероятность, что очередная сцена получит оверлей (0..1). Сцены со своим HUD — никогда.</summary>
    public double Probability { get; set; } = 0.4;
}

/// <summary>Сетевые данные. Всё качается в фоне и кешируется; без сети сцены работают на кеше.</summary>
public sealed class NetworkConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Город для погоды — любое название (геокодер Open-Meteo), например "Москва", "Berlin".</summary>
    public string City { get; set; } = "Москва";
    /// <summary>Источники новостей по id (список — в Phosphor.Data). Неизвестные — пропускаются с предупреждением.</summary>
    public List<string> News { get; set; } = new() { "tass", "bbc", "reddit:worldnews" };
    /// <summary>Каналы телетекста по id.</summary>
    public List<string> Teletext { get; set; } = new() { "svt", "nos" };
}

public sealed class ExitConfig
{
    public int MouseThresholdPx { get; set; } = 12;
}
