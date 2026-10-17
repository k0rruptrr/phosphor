namespace Phosphor.App;

/// <summary>
/// Шрифт сцены: scenes.params.&lt;id&gt;.font → IPreferredFont.PreferredFont → null (общий text.font).
/// Чистая логика без GL: проверку наличия шрифта передаёт вызывающий (в приложении — FontCatalog.Find).
/// </summary>
public static class SceneFont
{
    private static readonly HashSet<string> Warned = new();

    /// <summary>
    /// Семейство для сцены. paramFont — значение scenes.params.&lt;id&gt;.font (пусто — ключа нет), preferred — шрифт сцены.
    /// Выбранное семейство не найдено (exists = false) — null (общий шрифт) и одно предупреждение в warn на пару (сцена, имя).
    /// </summary>
    public static string? Resolve(string? paramFont, string? preferred, Func<string, bool> exists, Action<string>? warn = null)
    {
        string? chosen = Pick(paramFont, preferred);
        if (chosen is null) return null;
        if (exists(chosen)) return chosen;
        bool first;
        lock (Warned) first = Warned.Add(chosen);
        if (first) warn?.Invoke($"шрифт сцены '{chosen}' не найден среди моноширинных, использую общий text.font");
        return null;
    }

    /// <summary>Выбор без проверки наличия: непустой paramFont, иначе непустой preferred, иначе null.</summary>
    public static string? Pick(string? paramFont, string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(paramFont)) return paramFont.Trim();
        if (!string.IsNullOrWhiteSpace(preferred)) return preferred.Trim();
        return null;
    }
}
