using System.Text.Json;
using System.Text.Json.Nodes;
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
        Screen.Validate(warnings);
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

    /// <summary>
    /// Убрать из стадий crt всех пресетов ключи curvature, cornerRadius, vignette, mask (и такие же ключи внутри "mod"):
    /// геометрию кинескопа и маску теперь задаёт экран (screen). Идемпотентно. true — что-то удалено (конфиг стоит сохранить);
    /// notes — по строке на пресет. Стадии не-crt не трогаются.
    /// </summary>
    public bool MigrateScreen(List<string> notes)
    {
        string[] moved = { "curvature", "cornerRadius", "vignette", "mask" };
        bool changed = false;
        foreach (var name in Filters.Presets.Keys.ToList())
        {
            var stages = Filters.Presets[name];
            if (stages.ValueKind != JsonValueKind.Array) continue;
            if (JsonNode.Parse(stages.GetRawText()) is not JsonArray arr) continue;
            int removed = 0;
            foreach (var node in arr)
            {
                if (node is not JsonObject stage) continue;
                if (stage["id"] is not JsonValue idNode || !idNode.TryGetValue<string>(out var id)
                    || !string.Equals(id, "crt", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var key in moved)
                {
                    if (stage.Remove(key)) removed++;
                    if (stage["mod"] is JsonObject mod && mod.Remove(key)) removed++;
                }
            }
            if (removed == 0) continue;
            Filters.Presets[name] = JsonDocument.Parse(arr.ToJsonString()).RootElement.Clone();
            notes.Add($"пресет '{name}': из crt убрано {removed} ключ(ей) curvature/cornerRadius/vignette/mask — геометрию и маску теперь задаёт экран");
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Шкала SECAM «качество приёма» (signal, 1 = идеально) стала «помехами» (interference, 0 = чисто):
    /// в стадиях tv пресетов signal: v → interference: 1 − v; в "mod" signal: {src, amount: a} → interference: {src, amount: −a}.
    /// Идемпотентно. true — что-то переписано (конфиг стоит сохранить); notes — по строке на пресет.
    /// </summary>
    /// <summary>Новые встроенные пресеты (DefaultJson) добавить в конфиг, если пользователь их ещё не видел (см. FiltersConfig.Seen).</summary>
    public bool MigratePresets(List<string> notes)
    {
        var builtin = Parse(DefaultJson).Filters.Presets;
        var seen = new HashSet<string>(Filters.Seen ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        bool changed = Filters.Seen is null;
        foreach (var (name, stages) in builtin)
        {
            if (!seen.Add(name)) continue;
            changed = true;
            if (Filters.Presets.ContainsKey(name)) continue;
            Filters.Presets[name] = stages.Clone();
            notes.Add($"новый пресет '{name}' — добавлен");
        }
        Filters.Seen = seen.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        return changed;
    }

    public bool MigrateTvSignal(List<string> notes)
    {
        bool changed = false;
        foreach (var name in Filters.Presets.Keys.ToList())
        {
            var stages = Filters.Presets[name];
            if (stages.ValueKind != JsonValueKind.Array) continue;
            if (JsonNode.Parse(stages.GetRawText()) is not JsonArray arr) continue;
            int moved = 0;
            foreach (var node in arr)
            {
                if (node is not JsonObject stage) continue;
                if (stage["id"] is not JsonValue idNode || !idNode.TryGetValue<string>(out var id)
                    || !string.Equals(id, "tv", StringComparison.OrdinalIgnoreCase)) continue;
                if (stage["signal"] is JsonValue sv && sv.TryGetValue<double>(out var v))
                {
                    stage.Remove("signal");
                    stage["interference"] = 1.0 - v;
                    moved++;
                }
                if (stage["mod"] is JsonObject mod && mod["signal"] is JsonObject sig)
                {
                    var src = sig["src"]?.DeepClone();
                    var ns = new JsonObject { ["src"] = src };
                    if (sig["amount"] is JsonValue av && av.TryGetValue<double>(out var a)) ns["amount"] = -a;
                    mod.Remove("signal");
                    mod["interference"] = ns;
                    moved++;
                }
            }
            if (moved == 0) continue;
            Filters.Presets[name] = JsonDocument.Parse(arr.ToJsonString()).RootElement.Clone();
            notes.Add($"пресет '{name}': tv.signal переведён в tv.interference (шкала инвертирована: помехи = 1 − качество приёма)");
            changed = true;
        }
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

    /// <summary>Параметры оверлея id (объект из overlay.params) или пустой объект. Speed/Palette/CellAspect оверлею не нужны.</summary>
    public SceneOptions OverlayOptionsFor(string id) => new()
    {
        Params = Overlay.Params.ValueKind == JsonValueKind.Object && Overlay.Params.TryGetProperty(id, out var p) ? p.Clone() : SceneOptions.Empty,
    };

    public const string DefaultJson = """
    {
      "cellHeightPx": 18,
      "text": { "font": "Consolas" },
      "scenes": {
        "enabled": ["matrix", "starfield", "pipes", "doomfire", "life", "dvd", "aquarium",
                    "teletext", "teletype", "weatherboard", "torus", "grayscott", "voxel", "boids",
                    "plasma", "rotozoom", "tunnel", "cracktro", "boing", "c64load", "mandelbrot", "satellite", "organism", "sattv", "scope", "bbs", "radar", "fires", "hifi", "clocks", "drone"],
        "speed": 1.0,
        "palette": "default",
        "params": {
          "matrix": { "density": 0.6 }
        }
      },
      "filters": {
        "active": "terminal",
        "presets": {
          "clean":    [ { "id": "crt", "scanlines": 0.2, "bloom": 0.3 } ],
          "terminal": [ { "id": "mono" }, { "id": "phosphor", "color": "green", "persistence": 0.82 },
                        { "id": "crt", "mod": { "brightness": { "src": "breath", "amount": 0.12 } } } ],
          "amber":    [ { "id": "mono", "contrast": 1.15 }, { "id": "phosphor", "color": "amber", "persistence": 0.86, "mod": { "persistence": { "src": "night", "amount": 0.08 } } },
                        { "id": "crt", "mod": { "brightness": { "src": "breath", "amount": 0.1 } } } ],
          "kassette": [ { "id": "vhs", "badTracking": 0.35, "lumaRes": 520, "chromaRes": 60, "mod": { "tracking": { "src": "fatigue", "amount": 0.6 } } },
                        { "id": "chroma", "amount": 1.5 },
                        { "id": "crt" } ],
          "pal":      [ { "id": "tv", "mode": "pal" }, { "id": "crt" } ],
          "secam":    [ { "id": "tv", "mode": "secam", "mod": { "interference": { "src": "fatigue", "amount": 0.5 } } }, { "id": "crt" } ],
          "ntsc":     [ { "id": "tv", "mode": "ntsc" }, { "id": "crt" } ],
          "hi8":      [ { "id": "vhs", "tbc": 1, "jitter": 0.7, "wobble": 0.6, "noise": 0.04, "dropouts": 0.02, "badTracking": 0, "bottomSkew": 0.3, "tracking": 0, "saturation": 1.15, "bleed": 0.3, "lumaRes": 420, "chromaRes": 70, "iqShift": 0.5, "chromaClip": 0.15 },
                        { "id": "crt" } ],
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
    /// <summary>Встроенные пресеты, о которых конфиг уже «знает». Встроенный пресет, которого здесь нет, — новый из обновления:
    /// добавляется в Presets и сюда. null (старый конфиг) — добавляются все недостающие встроенные. Удалённый пользователем пресет
    /// после этого не возвращается (он есть в Seen).</summary>
    public List<string>? Seen { get; set; }
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
    /// <summary>Бирка «часы» на каждом мониторе (IBadge "clock"), параметры — overlay.params.clock.</summary>
    public bool Clock { get; set; } = false;
    /// <summary>Бирка «логотип» на каждом мониторе (IBadge "logo"), параметры — overlay.params.logo.</summary>
    public bool Logo { get; set; } = false;
    /// <summary>Собственные параметры оверлеев: объект id → { имя: значение } (см. IOverlayParams). Нет поля — по умолчанию.</summary>
    public JsonElement Params { get; set; } = SceneOptions.Empty;

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

/// <summary>
/// «Корпус монитора» поверх любой сцены и пресета: шлейф, геометрия кинескопа, блик на стекле, рамка.
/// Пресеты отвечают за сигнал (скан-линии, маска, свечение, PAL/VHS), экран — за железо; он один на все пресеты.
/// Порядок кадра: выход слота → шлейф (Trail) → ScreenPass → вывод на монитор.
/// Specs — описание для окна настроек; Get/Set — доступ по имени (Choice хранится строкой, наружу — индекс).
/// </summary>
public sealed class ScreenConfig
{
    /// <summary>Шлейф (моушн-блюр): доля предыдущего кадра в текущем, 0 = выкл, до 0.9.</summary>
    public float Trail { get; set; } = 0f;
    /// <summary>false — «плоский экран»: корпус (геометрия, стекло, рамка) выключен целиком, ScreenPass ничего не рисует;
    /// значения полей сохраняются. Шлейф от этого не зависит.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Бочкообразная кривизна кинескопа, 0 = плоский.</summary>
    public float Curvature { get; set; } = 0.06f;
    /// <summary>Скругление углов картинки, доля высоты.</summary>
    public float CornerRadius { get; set; } = 0.03f;
    /// <summary>Затемнение к краям.</summary>
    public float Vignette { get; set; } = 0.35f;
    /// <summary>Картинка шире стекла: края уходят под рамку, доля размера.</summary>
    public float Overscan { get; set; } = 0f;
    /// <summary>Неподвижный блик на стекле (отражение комнаты), 0 = нет.</summary>
    public float Glare { get; set; } = 0f;
    /// <summary>Рамка корпуса: "none" | "thin" | "tv" | "monitor".</summary>
    public string Bezel { get; set; } = "none";
    /// <summary>Цвет пластика: "black" | "graphite" | "beige".</summary>
    public string BezelColor { get; set; } = "black";
    /// <summary>Толщина рамки, доля высоты экрана.</summary>
    public float BezelSize { get; set; } = 0.06f;
    /// <summary>Отсвет картинки на внутренней фаске рамки.</summary>
    public float BezelGlow { get; set; } = 0.5f;
    /// <summary>Сила маски кинескопа (раньше — параметр mask стадии crt).</summary>
    public float Mask { get; set; } = 0.3f;
    /// <summary>Тип маски: "shadow" (триады по вертикали), "trinitron" (апертурная решётка + две нити поперёк), "diagonal" (триады со сдвигом по диагонали).</summary>
    public string MaskType { get; set; } = "shadow";
    /// <summary>Чересстрочность: поля через строку (гребёнка на движении), 0 = выкл.</summary>
    public float Interlace { get; set; } = 0f;

    public static readonly string[] BezelStyles = { "none", "thin", "tv", "monitor" };
    public static readonly string[] BezelColors = { "black", "graphite", "beige" };
    public static readonly string[] MaskTypes = { "shadow", "trinitron", "diagonal" };

    public static readonly IReadOnlyList<SceneParamSpec> Specs = new SceneParamSpec[]
    {
        new("curvature", SceneParamKind.Float, 0.06f, 0f, 0.5f, "кривизна кинескопа"),
        new("cornerRadius", SceneParamKind.Float, 0.03f, 0f, 0.2f, "скругление углов"),
        new("vignette", SceneParamKind.Float, 0.35f, 0f, 1f, "затемнение краёв"),
        new("overscan", SceneParamKind.Float, 0f, 0f, 0.2f, "картинка под рамку"),
        new("glare", SceneParamKind.Float, 0f, 0f, 4f, "блик на стекле"),
        new("bezel", SceneParamKind.Choice, 0, 0, 3, "рамка корпуса", BezelStyles),
        new("bezelColor", SceneParamKind.Choice, 0, 0, 2, "цвет пластика", BezelColors),
        new("bezelSize", SceneParamKind.Float, 0.06f, 0.02f, 0.15f, "толщина рамки"),
        new("bezelGlow", SceneParamKind.Float, 0.5f, 0f, 1f, "отсвет на рамке"),
        new("mask", SceneParamKind.Float, 0.3f, 0f, 1f, "маска кинескопа"),
        new("maskType", SceneParamKind.Choice, 0, 0, 2, "тип маски", MaskTypes),
        new("interlace", SceneParamKind.Float, 0f, 0f, 1f, "чересстрочность"),
        new("trail", SceneParamKind.Float, 0f, 0f, 0.9f, "шлейф (моушн-блюр)"),
    };

    /// <summary>Значение по имени из Specs (Choice — индекс в Options). Неизвестное имя — исключение.</summary>
    public double Get(string name) => name switch
    {
        "curvature" => Curvature,
        "cornerRadius" => CornerRadius,
        "vignette" => Vignette,
        "overscan" => Overscan,
        "glare" => Glare,
        "bezel" => Math.Max(0, Array.IndexOf(BezelStyles, Bezel)),
        "bezelColor" => Math.Max(0, Array.IndexOf(BezelColors, BezelColor)),
        "bezelSize" => BezelSize,
        "bezelGlow" => BezelGlow,
        "mask" => Mask,
        "maskType" => Math.Max(0, Array.IndexOf(MaskTypes, MaskType)),
        "interlace" => Interlace,
        "trail" => Trail,
        _ => throw new ArgumentException($"нет параметра экрана '{name}'", nameof(name)),
    };

    /// <summary>Записать значение по имени из Specs с клампом в Min..Max (Choice — индекс).</summary>
    public void Set(string name, double value)
    {
        var spec = Specs.FirstOrDefault(s => s.Name == name) ?? throw new ArgumentException($"нет параметра экрана '{name}'", nameof(name));
        float v = (float)Math.Clamp(value, spec.Min, spec.Max);
        switch (name)
        {
            case "curvature": Curvature = v; break;
            case "cornerRadius": CornerRadius = v; break;
            case "vignette": Vignette = v; break;
            case "overscan": Overscan = v; break;
            case "glare": Glare = v; break;
            case "bezel": Bezel = BezelStyles[(int)Math.Round(v)]; break;
            case "bezelColor": BezelColor = BezelColors[(int)Math.Round(v)]; break;
            case "bezelSize": BezelSize = v; break;
            case "bezelGlow": BezelGlow = v; break;
            case "mask": Mask = v; break;
            case "maskType": MaskType = MaskTypes[(int)Math.Round(v)]; break;
            case "interlace": Interlace = v; break;
            case "trail": Trail = v; break;
        }
    }

    /// <summary>Кламп всех полей; неизвестные Bezel/BezelColor — по умолчанию с предупреждением.</summary>
    public void Validate(List<string> warnings)
    {
        if (Array.IndexOf(BezelStyles, Bezel) < 0) { warnings.Add($"screen.bezel='{Bezel}' неизвестна, ставлю none"); Bezel = "none"; }
        if (Array.IndexOf(BezelColors, BezelColor) < 0) { warnings.Add($"screen.bezelColor='{BezelColor}' неизвестен, ставлю black"); BezelColor = "black"; }
        if (Array.IndexOf(MaskTypes, MaskType) < 0) { warnings.Add($"screen.maskType='{MaskType}' неизвестен, ставлю shadow"); MaskType = "shadow"; }
        foreach (var s in Specs)
            if (s.Kind == SceneParamKind.Float) Set(s.Name, Get(s.Name));
    }
}

public sealed class ExitConfig
{
    public int MouseThresholdPx { get; set; } = 12;
}
