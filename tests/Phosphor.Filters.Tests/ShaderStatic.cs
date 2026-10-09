using System.Text.RegularExpressions;
using Phosphor.Gfx;

namespace Phosphor.Filters.Tests;

/// <summary>
/// Статическая проверка шейдеров фильтра без видеокарты. Эталон — не меняй.
/// Настоящая компиляция проверяется запуском --shot.
/// </summary>
public static class ShaderStatic
{
    public static string Load(string logicalName)
    {
        using var s = typeof(CrtFilter).Assembly.GetManifestResourceStream(logicalName);
        Assert.True(s is not null, $"Ресурс {logicalName} не найден");
        return new StreamReader(s!).ReadToEnd();
    }

    /// <summary>Общие проверки текста шейдера.</summary>
    public static void CheckSyntax(string src, string name)
    {
        Assert.True(src.TrimStart().StartsWith("#version 330 core"), $"{name}: первая строка должна быть #version 330 core");
        Assert.True(src.Count(c => c == '{') == src.Count(c => c == '}'), $"{name}: не сбалансированы {{}}");
        Assert.True(src.Count(c => c == '(') == src.Count(c => c == ')'), $"{name}: не сбалансированы ()");
        Assert.DoesNotContain("texture2D(", src);
        Assert.DoesNotContain("gl_FragColor", src);
        Assert.Contains("out vec4", src);
    }

    /// <summary>Главный шейдер: каждый ParamSpec объявлен как uniform float p_X, и нет лишних p_.</summary>
    public static void CheckParams(IFilter f, string mainShaderSrc)
    {
        var declared = Regex.Matches(mainShaderSrc, @"uniform\s+float\s+p_(\w+)\s*;").Select(m => m.Groups[1].Value).ToHashSet();
        var specs = f.Params.Select(p => p.Name).ToHashSet();
        foreach (var s in specs) Assert.True(declared.Contains(s), $"{f.Id}: параметр {s} не объявлен в шейдере как uniform float p_{s};");
        foreach (var d in declared) Assert.True(specs.Contains(d), $"{f.Id}: в шейдере лишний uniform p_{d}");
        foreach (var p in f.Params)
        {
            Assert.True(p.Min <= p.Default && p.Default <= p.Max, $"{f.Id}.{p.Name}: Default вне [Min, Max]");
            if (p.Options is not null) Assert.Equal(p.Options.Length - 1, (int)p.Max);
        }
        Assert.Equal(specs.Count, f.Params.Count); // нет дублей имён
    }
}
