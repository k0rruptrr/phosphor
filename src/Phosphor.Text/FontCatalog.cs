namespace Phosphor.Text;

/// <summary>Шрифт из перечня: имя семейства, файл (Regular, первый face для TTC) и встроенный ли.</summary>
public sealed record FontEntry(string Family, string Path, bool Builtin);

/// <summary>
/// Перечень моноширинных шрифтов: встроенные (fonts\ рядом с exe), пользовательские (%LOCALAPPDATA%) и системные.
/// Моноширинность — равные advance у 'i', 'M', 'W', '0'. Имя семейства — из name (nameID 1), у семейства берём Regular.
/// </summary>
public static class FontCatalog
{
    /// <summary>Шрифт по умолчанию и фолбэк, если имя из конфига не нашлось.</summary>
    public const string DefaultFamily = "Consolas";

    /// <summary>Каталоги перечня по порядку приоритета: при одинаковом имени побеждает первый.</summary>
    public static IEnumerable<(string Dir, bool Builtin)> DefaultDirs()
    {
        yield return (Path.Combine(AppContext.BaseDirectory, "fonts"), true);
        yield return (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"), false);
        yield return (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts"), false);
    }

    /// <summary>Перечень для окна настроек: встроенные, затем пользовательские, затем системные.</summary>
    public static IReadOnlyList<FontEntry> List() => Enumerate(DefaultDirs());

    /// <summary>Перечень по заданным каталогам (для тестов и своих путей).</summary>
    public static IReadOnlyList<FontEntry> Enumerate(IEnumerable<(string Dir, bool Builtin)> dirs)
    {
        var result = new List<FontEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, builtin) in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (!IsFontFile(file)) continue;
                var entry = Describe(file, builtin);
                if (entry is null || !seen.Add(entry.Family)) continue;
                result.Add(entry);
            }
        }
        return result.OrderByDescending(e => e.Builtin).ThenBy(e => e.Family, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Шрифт по значению конфига: путь к .ttf/.otf/.ttc (относительный — от папки exe) или имя семейства из перечня.
    /// null — не найден (или не моноширинный, если имя из перечня).
    /// </summary>
    public static FontEntry? Find(string spec)
    {
        spec = spec.Trim();
        if (spec.Length == 0) return null;
        if (LooksLikePath(spec))
        {
            string path = Path.IsPathRooted(spec) ? spec : Path.Combine(AppContext.BaseDirectory, spec);
            if (!File.Exists(path)) return null;
            try
            {
                using var f = FontFile.Load(path);
                return new FontEntry(f.Name(1) ?? Path.GetFileNameWithoutExtension(path), path, false);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
        }
        return List().FirstOrDefault(e => string.Equals(e.Family, spec, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Моноширинный ли шрифт файла: advance 'i', 'M', 'W', '0' равны.</summary>
    public static bool IsMonospace(string path)
    {
        try
        {
            using var f = FontFile.Load(path);
            return IsMonospace(f);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsMonospace(FontFile f)
    {
        int w = f.Advance('i');
        if (w <= 0) return false;
        foreach (char c in "iMW0")
            if (!f.Has(c) || f.Advance(c) != w) return false;
        return true;
    }

    private static FontEntry? Describe(string file, bool builtin)
    {
        try
        {
            using var f = FontFile.Load(file);
            if (!IsMonospace(f)) return null;
            // Только Regular: Bold/Italic/Light и т.п. — отдельные файлы того же семейства
            string? sub = f.Name(2);
            bool regular = sub is not null
                ? string.Equals(sub, "Regular", StringComparison.OrdinalIgnoreCase)
                : !ContainsStyle(Path.GetFileNameWithoutExtension(file));
            if (!regular) return null;
            string family = f.Name(1) ?? Path.GetFileNameWithoutExtension(file);
            return new FontEntry(family, file, builtin);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    private static bool ContainsStyle(string name) =>
        name.Contains("Bold", StringComparison.OrdinalIgnoreCase) || name.Contains("Italic", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase);

    private static bool IsFontFile(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) || ext.Equals(".otf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePath(string spec) =>
        spec.Contains('\\') || spec.Contains('/') || spec.Contains(':') || IsFontFile(spec);
}
