using System.Text.Json;

namespace Phosphor.Core;

/// <summary>Общие опции сцены из конфига.</summary>
public sealed class SceneOptions
{
    /// <summary>Множитель скорости, 1 = нормально.</summary>
    public float Speed { get; init; } = 1f;

    /// <summary>Палитра: "default", "green", "amber", "mono", "rainbow". Неизвестное имя — как "default".</summary>
    public string Palette { get; init; } = "default";

    /// <summary>Пропорция ячейки: высота / ширина в пикселях (Consolas 10×18 → 1.8). Зависит от шрифта (text.font) — сцены, которым
    /// нужны круги и квадраты (радар, карта, циферблат), берут её отсюда, а не константой. Задаёт хост из атласа шрифта.</summary>
    public float CellAspect { get; init; } = 1.8f;

    /// <summary>Собственные параметры сцены (объект scenes.params.&lt;id&gt;). Нет поля — значение по умолчанию.</summary>
    public JsonElement Params { get; init; } = Empty;

    public static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement;

    public float GetFloat(string name, float def) =>
        TryGet(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : def;

    public int GetInt(string name, int def) =>
        TryGet(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : def;

    public bool GetBool(string name, bool def) =>
        TryGet(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;

    public string GetString(string name, string def) =>
        TryGet(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : def;

    private bool TryGet(string name, out JsonElement v)
    {
        v = default;
        return Params.ValueKind == JsonValueKind.Object && Params.TryGetProperty(name, out v);
    }
}

/// <summary>
/// Сцена (режим). Знает ТОЛЬКО сетку символов: никаких окон, GPU, фильтров.
/// Жизненный цикл: Init, затем (Update, Draw)*. Init может вызываться повторно — сцена полностью сбрасывается.
/// </summary>
public interface IScene
{
    /// <summary>Уникальный id в нижнем регистре, как в конфиге ("matrix", "doomfire", ...).</summary>
    string Id { get; }

    /// <summary>cols, rows ≥ 1. Вся случайность — только через rng (детерминизм для тестов).</summary>
    void Init(int cols, int rows, Random rng, SceneOptions options);

    /// <summary>dt — секунды с прошлого кадра, 0 ≤ dt ≤ 0.1 (хост обрезает). При dt=0 состояние не меняется.</summary>
    void Update(float dt);

    /// <summary>
    /// Нарисовать кадр в grid (размер = cols x rows из Init). Перерисовать ВСЕ ячейки,
    /// не полагаться на содержимое прошлого кадра. Draw не меняет состояние: два Draw подряд дают одно и то же.
    /// </summary>
    void Draw(CharGrid grid);
}

/// <summary>
/// Необязательно. Сцене нужна крупнее/мельче сетка, чем по умолчанию (например, телетекст — 25 строк на весь экран).
/// Хост подберёт высоту ячейки ≈ высота_экрана / PreferredRows (в пределах 8..64 px) и вызовет Init с полученной сеткой.
/// </summary>
public interface IPreferredRows
{
    int PreferredRows { get; }
}

/// <summary>
/// Необязательно. Семейство шрифта по умолчанию для сцены (телетекст — Bedstead). null — общий text.font.
/// Пользователь переопределяет ключом scenes.params.&lt;id&gt;.font (пусто — ключа нет, берётся это значение).
/// </summary>
public interface IPreferredFont
{
    string? PreferredFont { get; }
}

/// <summary>
/// Необязательно. «Оптика» сцены: стадии фильтров, которые принадлежат самому прибору (свечение газоразрядных ламп),
/// а не экрану. Хост ставит их ПЕРЕД стадиями пресета, то есть под любой пресет. JSON-массив стадий, как в filters.presets.
/// Пользователь выключает ключом scenes.params.&lt;id&gt;.optics = false.
/// </summary>
public interface ISceneOptics
{
    string OpticsStages { get; }
}

/// <summary>Необязательно. Сцена с этим интерфейсом получает звук перед каждым Update.</summary>
public interface IAudioReactive
{
    void OnAudio(in AudioFrame audio);
}

/// <summary>
/// Сцена, где главное — читаемый текст (новости, телетекст, BBS, табло). На ней помехи фильтров ослабляются
/// множителем filters.textClarity: стиль пресета остаётся, а буквы не тонут в снеге и наводках.
/// </summary>
public interface ITextScene
{
}
