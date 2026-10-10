using System.Globalization;

namespace Phosphor.Platform;

public enum LaunchMode { Screensaver, Preview, Configure, Windowed, Shot, SettingsShot }

/// <summary>Вкладки окна настроек (допустимые значения --tab).</summary>
public static class SettingsTabs
{
    public static readonly string[] All = { "scenes", "filters", "overlays", "screen", "shuffle", "dimmer", "network", "font", "about" };
}

/// <summary>Разобранные аргументы командной строки. Формат — тот, с которым Windows вызывает .scr.</summary>
public sealed class LaunchArgs
{
    public LaunchMode Mode { get; init; }
    public nint ParentHwnd { get; init; }      // для Preview (обязателен) и Configure (может быть 0)
    public string? ShotPath { get; init; }     // для Shot и SettingsShot
    public string? Scene { get; init; }        // --scene <id>, для Windowed, Shot и Preview
    public string? Preset { get; init; }       // --preset <name>, для Windowed, Shot и Preview
    public string? SettingsTab { get; init; }  // --tab <имя>, для SettingsShot (по умолчанию scenes)
    public string? Overlay { get; init; }      // --overlay <id> (hud|vcr), для Windowed, Shot и Preview
    public int Frames { get; init; } = 120;    // --frames N, для Windowed и Shot
    public int Width { get; init; } = 1280;    // --size WxH
    public int Height { get; init; } = 720;
    public int Seed { get; init; } = 1;        // --seed N
    public string? ConfigPath { get; init; }   // --config <путь>, для всех режимов: вместо phosphor.json рядом с exe

    /// <summary>
    /// Разбор аргументов. Регистр флагов не важен. Ошибки — ArgumentException с текстом на русском.
    /// Опции --scene/--preset/--frames/--size/--seed допустимы только с /w и --shot (--scene/--preset ещё и с /p).
    /// --tab — только с --settings-shot. Неизвестная вкладка — ошибка.
    /// </summary>
    public static LaunchArgs Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
            return new LaunchArgs { Mode = LaunchMode.Configure, ParentHwnd = 0 };

        LaunchMode? mode = null;
        nint parent = 0;
        string? shotPath = null;
        string? scene = null;
        string? preset = null;
        int frames = 120;
        int width = 1280;
        int height = 720;
        int seed = 1;
        string? tab = null;
        string? configPath = null;
        string? overlay = null;
        var seenOptions = new HashSet<string>(StringComparer.Ordinal);

        void SetMode(LaunchMode m)
        {
            if (mode is not null)
                throw new ArgumentException($"Указано несколько режимов ({mode} и {m}). Нужен один.");
            mode = m;
        }

        void Option(string name)
        {
            if (!seenOptions.Add(name))
                throw new ArgumentException($"Опция {name} указана дважды.");
        }

        for (int i = 0; i < args.Length; i++)
        {
            string tok = args[i] ?? string.Empty;
            if (tok.Length == 0)
                throw new ArgumentException("Пустой аргумент командной строки.");

            string lower = tok.ToLowerInvariant();

            // Для флагов со значением, которое идёт следующим токеном.
            string NextValue(string flag)
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Опции {flag} нужно значение.");
                string v = args[++i] ?? string.Empty;
                if (v.StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Опции {flag} нужно значение, получено '{v}'.");
                return v;
            }

            switch (lower)
            {
                case "/s":
                case "-s":
                    SetMode(LaunchMode.Screensaver);
                    break;

                case "/p":
                    SetMode(LaunchMode.Preview);
                    parent = ParseHwnd(NextValue("/p"), "/p");
                    break;

                case "/c":
                    SetMode(LaunchMode.Configure);
                    // Необязательный HWND: берём следующий токен, только если это число.
                    if (i + 1 < args.Length && TryParseHwnd(args[i + 1], out nint cHwnd))
                    {
                        parent = cHwnd;
                        i++;
                    }
                    break;

                case "/w":
                    SetMode(LaunchMode.Windowed);
                    break;

                case "--shot":
                    SetMode(LaunchMode.Shot);
                    shotPath = NextValue("--shot");
                    break;

                case "--settings-shot":
                    SetMode(LaunchMode.SettingsShot);
                    shotPath = NextValue("--settings-shot");
                    break;

                case "--tab":
                    Option("--tab");
                    tab = NextValue("--tab").ToLowerInvariant();
                    if (Array.IndexOf(SettingsTabs.All, tab) < 0)
                        throw new ArgumentException($"Опция --tab: неизвестная вкладка '{tab}' (есть: {string.Join(", ", SettingsTabs.All)}).");
                    break;

                case "--scene":
                    Option("--scene");
                    scene = NextValue("--scene");
                    break;

                case "--preset":
                    Option("--preset");
                    preset = NextValue("--preset");
                    break;

                case "--overlay":
                    Option("--overlay");
                    overlay = NextValue("--overlay").ToLowerInvariant();
                    break;

                case "--frames":
                    Option("--frames");
                    frames = ParsePositiveInt(NextValue("--frames"), "--frames");
                    break;

                case "--size":
                    Option("--size");
                    ParseSize(NextValue("--size"), out width, out height);
                    break;

                case "--seed":
                    Option("--seed");
                    seed = ParseInt(NextValue("--seed"), "--seed");
                    break;

                case "--config":
                    Option("--config");
                    configPath = NextValue("--config");
                    break;

                default:
                    if (lower.StartsWith("/p:", StringComparison.Ordinal))
                    {
                        SetMode(LaunchMode.Preview);
                        parent = ParseHwnd(tok.Substring(3), "/p");
                    }
                    else if (lower.StartsWith("/c:", StringComparison.Ordinal))
                    {
                        SetMode(LaunchMode.Configure);
                        parent = ParseHwnd(tok.Substring(3), "/c");
                    }
                    else
                    {
                        throw new ArgumentException($"Неизвестный аргумент: '{tok}'.");
                    }
                    break;
            }
        }

        if (mode is null)
            throw new ArgumentException("Не указан режим: /s, /p, /c, /w или --shot.");

        foreach (var opt in seenOptions)
        {
            if (!OptionAllowed(mode.Value, opt))
                throw new ArgumentException(mode.Value == LaunchMode.Preview || opt is "--tab"
                    ? $"Опция {opt} здесь не допустима."
                    : "Опции --scene, --preset, --overlay, --frames, --size, --seed допустимы только с /w и --shot (--scene, --preset и --overlay ещё с /p); --tab — только с --settings-shot.");
        }

        if (mode == LaunchMode.Preview && parent == 0)
            throw new ArgumentException("/p требует ненулевой HWND родительского окна.");

        return new LaunchArgs
        {
            Mode = mode.Value,
            ParentHwnd = parent,
            ShotPath = shotPath,
            Scene = scene,
            Preset = preset,
            SettingsTab = mode == LaunchMode.SettingsShot ? tab ?? "scenes" : null,
            Frames = frames,
            Width = width,
            Height = height,
            Seed = seed,
            ConfigPath = configPath,
            Overlay = overlay,
        };
    }

    /// <summary>Какие опции допустимы в каком режиме. --config допустим везде.</summary>
    private static bool OptionAllowed(LaunchMode mode, string opt)
    {
        if (opt is "--config") return true;
        return mode switch
        {
            LaunchMode.Windowed or LaunchMode.Shot => opt is "--scene" or "--preset" or "--overlay" or "--frames" or "--size" or "--seed",
            LaunchMode.Preview => opt is "--scene" or "--preset" or "--overlay",
            LaunchMode.SettingsShot => opt is "--tab",
            _ => false,
        };
    }

    private static bool TryParseHwnd(string text, out nint hwnd)
    {
        hwnd = 0;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long v)
            || v < 0 || v > nint.MaxValue)
            return false;
        hwnd = (nint)v;
        return true;
    }

    private static nint ParseHwnd(string text, string flag)
    {
        if (!TryParseHwnd(text, out nint hwnd))
            throw new ArgumentException($"После {flag} ожидался HWND (десятичное число), получено '{text}'.");
        return hwnd;
    }

    private static int ParseInt(string text, string opt)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            throw new ArgumentException($"Опция {opt} ожидает целое число, получено '{text}'.");
        return v;
    }

    private static int ParsePositiveInt(string text, string opt)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int v) || v <= 0)
            throw new ArgumentException($"Опция {opt} ожидает положительное целое число, получено '{text}'.");
        return v;
    }

    private static void ParseSize(string text, out int width, out int height)
    {
        string[] parts = text.Split('x', 'X');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height)
            || width <= 0 || height <= 0)
        {
            width = 0;
            height = 0;
            throw new ArgumentException($"Опция --size ожидает формат WxH (например 1920x1080), получено '{text}'.");
        }
    }
}
