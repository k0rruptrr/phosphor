using System.Text.RegularExpressions;

namespace Phosphor.Filters.Tests;

/// <summary>
/// Зарезервированные слова GLSL 3.30 (half, fixed, input, filter, …) в шейдерах. NVIDIA их прощает, драйвер AMD — нет:
/// 'half' в screen.frag ронял заставку на ноуте с Radeon Vega 8 (PHOS-241). Проверка по исходникам: .frag/.vert
/// и GLSL внутри C# (сырые строки с #version 330).
/// </summary>
public class GlslReservedWordsTests
{
    private static readonly string[] Reserved =
    {
        "common", "partition", "active", "asm", "class", "union", "enum", "typedef", "template", "this", "packed",
        "resource", "goto", "inline", "noinline", "volatile", "public", "static", "extern", "external", "interface",
        "long", "short", "double", "half", "fixed", "unsigned", "superp", "input", "output",
        "hvec2", "hvec3", "hvec4", "dvec2", "dvec3", "dvec4", "fvec2", "fvec3", "fvec4",
        "sampler3DRect", "filter", "sizeof", "cast", "namespace", "using", "sample", "precise",
    };

    private static readonly Regex Word = new(@"\b(" + string.Join("|", Reserved) + @")\b", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Directory.Build.props"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("корень репозитория (Directory.Build.props) не найден");
    }

    /// <summary>Строки GLSL: файлы шейдеров целиком, из C# — от строки с #version 330 до закрывающих кавычек.</summary>
    private static IEnumerable<(string File, int Line, string Text)> GlslLines()
    {
        string src = Path.Combine(RepoRoot(), "src");
        foreach (var f in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var lines = File.ReadAllLines(f);
            if (f.EndsWith(".frag") || f.EndsWith(".vert"))
            {
                for (int i = 0; i < lines.Length; i++) yield return (f, i + 1, lines[i]);
            }
            else if (f.EndsWith(".cs"))
            {
                bool inGlsl = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("#version 330") && !lines[i].TrimStart().StartsWith("//")) inGlsl = true;
                    else if (inGlsl && lines[i].Contains("\"\"\"")) inGlsl = false;
                    if (inGlsl) yield return (f, i + 1, lines[i]);
                }
            }
        }
    }

    [Fact]
    public void ShadersDoNotUseReservedWords()
    {
        var bad = new List<string>();
        int n = 0;
        foreach (var (file, line, text) in GlslLines())
        {
            n++;
            string code = text.Split("//")[0];
            var m = Word.Match(code);
            if (m.Success) bad.Add($"{Path.GetFileName(file)}:{line}: '{m.Value}' — {text.Trim()}");
        }
        Assert.True(n > 500, $"нашлось лишь {n} строк GLSL — сканер не видит шейдеры");
        Assert.True(bad.Count == 0, "Зарезервированные слова GLSL (AMD не скомпилирует):\n" + string.Join("\n", bad));
    }
}
