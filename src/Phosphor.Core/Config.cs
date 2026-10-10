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
    public DimmerConfig Dimmer { get; set; } = new();
    public ScreenConfig Screen { get; set; } = new();
    public TextConfig Text { get; set; } = new();

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
        // "shuffle" — не ошибка: стартовый пресет выбирается случайно (см. Selection.PresetPool)
        if (!string.Equals(Filters.Active, FiltersConfig.ShuffleActive, StringComparison.OrdinalIgnoreCase)
            && !Filters.Presets.ContainsKey(Filters.Active))
        {
            var first = Filters.Presets.Keys.First();
            warnings.Add($"Пресет '{Filters.Active}' не найден, беру '{first}'");
            Filters.Active = first;
        }
        Shuffle.IntervalSec = Math.Max(5, Shuffle.IntervalSec);
        Shuffle.TransitionSec = Math.Clamp(Shuffle.TransitionSec, 0, 10);
        Shuffle.FavoriteWeight = Math.Clamp(Shuffle.FavoriteWeight, 1f, 10f);
        Scenes.Speed = Math.Clamp(Scenes.Speed, 0.05f, 10f);
        Screen.Trail = Math.Clamp(Screen.Trail, 0f, 0.9f);
        Dimmer.DelayMin = Math.Clamp(Dimmer.DelayMin, 0f, 240f);
        Dimmer.FadeSec = Math.Clamp(Dimmer.FadeSec, 0f, 600f);
        Dimmer.HardwareLevel = Math.Clamp(Dimmer.HardwareLevel, 0, 100);
        Dimmer.SoftwareLevel = Math.Clamp(Dimmer.SoftwareLevel, 0f, 1f);
        Network.FireRadiusKm = Math.Clamp(Network.FireRadiusKm, 100, 1500);
        Network.FlightRadiusNm = Math.Clamp(Network.FlightRadiusNm, 20, 250);
    }

    /// <summary>
    /// Сцены из каталога, которых нет в Scenes.Seen, — новые: включить и запомнить. Seen==null — виденными
    /// считаются Enabled. Возвращает true, если конфиг изменился (его стоит сохранить). notes — что сделано, для лога.
    /// </summary>
    public bool MigrateScenes(IEnumerable<string> catalogIds, List<string> notes)
    {
        var seenBefore = Scenes.Seen;
        var seen = new HashSet<string>(seenBefore ?? Scenes.Enabled, StringComparer.OrdinalIgnoreCase);
        var enabled = new HashSet<string>(Scenes.Enabled, StringComparer.OrdinalIgnoreCase);
        var result = Scenes.Seen is null ? new List<string>(Scenes.Enabled) : Scenes.Seen;
        bool changed = seenBefore is null;

        foreach (var id in catalogIds)
        {
            if (!seen.Add(id)) continue;
            // Новая сцена из обновления: включаем (если ещё не включена) и запоминаем
            result.Add(id);
            if (enabled.Add(id)) Scenes.Enabled.Add(id);
            notes.Add($"новая сцена '{id}' — включена");
            changed = true;
        }
        Scenes.Seen = result;
        return changed;
    }

    /// <summary>Записать конфиг в файл (camelCase, с отступами). Перед первой перезаписью файла без Seen — копия в <path>.bak.</summary>
    public void Save(string path)
    {
        if (File.Exists(path) && !File.Exists(path + ".bak") && !HasSeenKey(File.ReadAllText(path)))
            File.Copy(path, path + ".bak");
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json), new System.Text.UTF8Encoding(false));
    }

    private static bool HasSeenKey(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(p.Name, "scenes", StringComparison.OrdinalIgnoreCase) || p.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var q in p.Value.EnumerateObject())
                    if (string.Equals(q.Name, "seen", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Параметры сцены id (объект из scenes.params) или пустой объект.</summary>
    public SceneOptions SceneOptionsFor(string id, float cellAspect = 1.8f) => new()
    {
        Speed = Scenes.Speed,
        Palette = Scenes.Palette,
        CellAspect = cellAspect,
        Params = Scenes.Params.ValueKind == JsonValueKind.Object && Scenes.Params.TryGetProperty(id, out var p) ? p.Clone() : SceneOptions.Empty,
    };

    public const string DefaultJson = """
    {
      "cellHeightPx": 18,
      "text": { "font": "Consolas" },
      "scenes": {
        "enabled": ["matrix", "starfield", "pipes", "doomfire", "life", "dvd", "aquarium",
                    "teletext", "teletype", "weatherboard", "torus", "grayscott", "voxel", "boids",
                    "plasma", "rotozoom", "tunnel", "cracktro", "boing", "c64load", "mandelbrot", "satellite", "organism", "sattv", "scope", "bbs", "radar", "fires", "hifi", "clocks"],
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
          "kassette": [ { "id": "vhs", "mod": { "tracking": { "src": "fatigue", "amount": 0.6 } } },
                        { "id": "chroma", "amount": 1.5 },
                        { "id": "crt", "curvature": 0.06 } ],
          "pal":      [ { "id": "tv", "mode": "pal" }, { "id": "crt", "curvature": 0.08 } ],
          "secam":    [ { "id": "tv", "mode": "secam", "mod": { "signal": { "src": "fatigue", "amount": -0.5 } } }, { "id": "crt", "curvature": 0.08 } ],
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
      "overlay": { "enabled": true, "id": "hud", "probability": 0.4, "vcr": true },
      "audio": { "enabled": true, "sensitivity": 1.0, "maxPulse": 0.8, "minBeatIntervalMs": 250 },
      "network": { "enabled": true, "city": "Москва", "news": ["nplus1", "naked", "habr", "nasa", "esa", "hn", "tass", "bbc"], "calmNews": true, "teletext": ["svt", "nos"], "satellite": ["meteosat", "himawari", "viirs"] },
      "screen": { "trail": 0 },
      "dimmer": { "enabled": false, "delayMin": 10, "fadeSec": 60, "hardware": true, "hardwareLevel": 0, "softwareLevel": 0.25 },
      "exit": { "mouseThresholdPx": 12 }
    }
    """;
}

/// <summary>Шрифт сетки. Font — имя семейства из перечня настроек или путь к .ttf/.otf. Не найден — Consolas.</summary>
public sealed class TextConfig
{
    public string Font { get; set; } = "Consolas";
}

public sealed class ScenesConfig
{
    public List<string> Enabled { get; set; } = new() { "matrix" };
    public float Speed { get; set; } = 1f;
    public string Palette { get; set; } = "default";
    public JsonElement Params { get; set; } = SceneOptions.Empty;
    /// <summary>Любимые сцены: выпадают чаще (вес Shuffle.FavoriteWeight). Учитываются, только если сцена включена.</summary>
    public List<string> Favorites { get; set; } = new();
    /// <summary>Только любимые (если среди включённых любимых нет — все включённые).</summary>
    public bool FavoritesOnly { get; set; } = false;
    /// <summary>Сцены, о которых конфиг уже «знает». Сцена из каталога, которой здесь нет, — новая из обновления:
    /// включается сама и дописывается сюда. null (старый конфиг) — виденными считаются сцены из Enabled.</summary>
    public List<string>? Seen { get; set; }
}

public sealed class FiltersConfig
{
    public string Active { get; set; } = "clean";
    /// <summary>Имя пресета → JSON-массив стадий фильтров (формат см. FilterChain).</summary>
    public Dictionary<string, JsonElement> Presets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Пресеты, участвующие в переборе. null — все из Presets.</summary>
    public List<string>? Enabled { get; set; }
    /// <summary>Любимые пресеты: вес Shuffle.FavoriteWeight поверх «настроения» дирижёра.</summary>
    public List<string> Favorites { get; set; } = new();
    public bool FavoritesOnly { get; set; } = false;
    /// <summary>Active = "shuffle": стартовый пресет случайный из доступных (это не ошибка конфига).</summary>
    public const string ShuffleActive = "shuffle";
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
    /// <summary>Во сколько раз любимые сцены/пресеты выпадают чаще обычных (1..10).</summary>
    public float FavoriteWeight { get; set; } = 3f;
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
    /// <summary>OSD видика (VcrOverlay) поверх пресетов со стадией vhs. Не зависит от HUD.</summary>
    public bool Vcr { get; set; } = true;

    /// <summary>Нужен ли OSD видика для цепочки стадий пресета (id фильтров по порядку).</summary>
    public bool VcrFor(IEnumerable<string> filterIds) =>
        Vcr && filterIds.Any(id => string.Equals(id, "vhs", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Сетевые данные. Всё качается в фоне и кешируется; без сети сцены работают на кеше.</summary>
public sealed class NetworkConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Город для погоды — любое название (геокодер Open-Meteo), например "Москва", "Berlin".</summary>
    public string City { get; set; } = "Москва";
    /// <summary>Источники новостей по id (список — в Phosphor.Data). Неизвестные — пропускаются с предупреждением.</summary>
    public List<string> News { get; set; } = new() { "nplus1", "naked", "habr", "nasa", "esa", "hn", "tass", "bbc" };
    /// <summary>Каналы телетекста по id.</summary>
    public List<string> Teletext { get; set; } = new() { "svt", "nos" };
    /// <summary>Спутниковые ленты по id: "meteosat" (EUMETSAT, Европа/РФ, 10 мин), "himawari" (весь диск, 10 мин), "viirs" (NASA, сутки).</summary>
    public List<string> Satellite { get; set; } = new() { "meteosat", "himawari", "viirs" };
    /// <summary>Спутники для сцены «сат-ТВ» — id страниц KingOfSat (en.kingofsat.info/sat-&lt;id&gt;). Список по умолчанию задаёт Phosphor.Data (SatTvCatalog.DefaultIds), здесь пусто = он.</summary>
    public List<string> SatTv { get; set; } = new();
    /// <summary>Пожары NASA вокруг города: полуширина квадрата, км (100..1500).</summary>
    public double FireRadiusKm { get; set; } = 500;
    /// <summary>Радар самолётов: радиус, морские мили (20..250).</summary>
    public double FlightRadiusNm { get; set; } = 100;
    /// <summary>«Дальние станции» радара, если над городом пусто: "ИМЯ:широта:долгота".</summary>
    public List<string> FlightStations { get; set; } = new() { "HELSINKI-VANTAA:60.32:24.95", "RIGA:56.92:23.97", "BELGRADE:44.82:20.29", "ISTANBUL:41.27:28.75" };
    /// <summary>Координаты города (WGS84). Заданы — геокодер не нужен (например, когда его режет сеть/провайдер).
    /// null — берутся из геокодера, а при его недоступности из последнего кеша погоды.</summary>
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    /// <summary>«Спокойные новости»: отсеивать заголовки про войну и насилие (стоп-слова в Phosphor.Data), разбавлять лёгкими лентами.</summary>
    public bool CalmNews { get; set; } = true;
    /// <summary>Дополнительные стоп-слова к встроенному списку (регистр не важен, совпадение по началу слова).</summary>
    public List<string> CalmExtraWords { get; set; } = new();
}

/// <summary>
/// Диммер на время заставки: через DelayMin минут плавно (FadeSec) гасит мониторы.
/// Мониторы с DDC/CI — подсветка до HardwareLevel; без DDC (или Hardware=false) — затемнение картинки до SoftwareLevel.
/// При выходе яркость подсветки восстанавливается.
/// </summary>
public sealed class DimmerConfig
{
    public bool Enabled { get; set; } = false;
    /// <summary>Через сколько минут работы заставки начинать гасить (0..240).</summary>
    public float DelayMin { get; set; } = 10f;
    /// <summary>Длительность плавного затемнения, с (0..600).</summary>
    public float FadeSec { get; set; } = 60f;
    /// <summary>Управлять подсветкой через DDC/CI там, где монитор отвечает.</summary>
    public bool Hardware { get; set; } = true;
    /// <summary>Целевая яркость подсветки, % диапазона монитора (0 — минимум монитора), 0..100.</summary>
    public int HardwareLevel { get; set; } = 0;
    /// <summary>Множитель яркости картинки для мониторов без DDC, 0..1 (0 — чёрный экран).</summary>
    public float SoftwareLevel { get; set; } = 0.25f;
}

/// <summary>Общее для экрана поверх любой сцены и пресета.</summary>
public sealed class ScreenConfig
{
    /// <summary>Шлейф (моушн-блюр): доля предыдущего кадра в текущем, 0 = выкл, до 0.9.</summary>
    public float Trail { get; set; } = 0f;
}

public sealed class ExitConfig
{
    public int MouseThresholdPx { get; set; } = 12;
}
