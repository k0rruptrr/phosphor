using System.Reflection;

namespace Phosphor.Core;

/// <summary>Версия сборки: берётся из атрибутов entry-assembly (InformationalVersion = «0.7.0+abc1234», BuildDate из AssemblyMetadata).</summary>
public static class AppInfo
{
    public static string Version { get; }
    public static string Commit { get; }
    public static string BuildDate { get; }
    /// <summary>Строка для лога и «о программе»: «0.7.0 (abc1234, 2026-10-10 14:05)».</summary>
    public static string Display { get; }

    static AppInfo()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? asm.GetName().Version?.ToString()
                   ?? "0.0.0";
        (Version, Commit) = ParseInformationalVersion(info);
        BuildDate = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
                        .FirstOrDefault(a => a.Key == "BuildDate")?.Value ?? "неизвестно";
        Display = $"{Version} ({Commit}, {BuildDate})";
    }

    /// <summary>
    /// Разбор InformationalVersion: «0.7.0+&lt;полный хеш&gt;» → («0.7.0», «701432d»); «0.7.0+abc1234-dirty» → («0.7.0», «abc1234-dirty»);
    /// «0.7.0» → («0.7.0», «dev»). Хеш обрезается до 7 символов, суффикс «-dirty» сохраняется.
    /// </summary>
    public static (string Version, string Commit) ParseInformationalVersion(string info)
    {
        int plus = info.IndexOf('+');
        string version = plus >= 0 ? info[..plus] : info;
        string rev = plus >= 0 ? info[(plus + 1)..].Trim() : "";
        if (rev.Length == 0) return (version, "dev");

        int dash = rev.IndexOf('-');
        string hash = dash >= 0 ? rev[..dash] : rev;
        string suffix = dash >= 0 ? rev[dash..] : "";
        if (hash.Length > 7) hash = hash[..7];
        return (version, hash.Length == 0 ? "dev" : hash + suffix);
    }
}
