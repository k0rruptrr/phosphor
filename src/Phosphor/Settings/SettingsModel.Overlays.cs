using System.Text.Json;
using System.Text.Json.Nodes;
using Phosphor.Core;

namespace Phosphor.App.Settings;

/// <summary>
/// Оверлеи (вкладка «Оверлеи»), бирки монитора и корпус монитора (вкладка «Экран»). Пишет прямо в Config.
/// Оверлеи: вкл/выкл — Overlay.Enabled (HUD) и Overlay.Vcr (OSD видика); бирки — Overlay.Clock и Overlay.Logo;
/// параметры — overlay.params.&lt;id&gt;; строки (шрифт, текст, файл) — тоже overlay.params.&lt;id&gt;.&lt;ключ&gt;.
/// </summary>
public sealed partial class SettingsModel
{
    /// <summary>Строки списка «Оверлеи»: HUD-разметка, OSD видика и бирки (часы, логотип).</summary>
    public static readonly string[] OverlayIds = { "hud", "vcr", "clock", "logo" };

    /// <summary>Текст логотипа по умолчанию (logo.text не задан).</summary>
    public const string LogoDefaultText = "PHOSPHOR";

    /// <summary>Название строки списка «Оверлеи».</summary>
    public static string OverlayTitle(string id) => id switch
    {
        "vcr" => "OSD видика",
        "clock" => "Часы",
        "logo" => "Логотип",
        _ => "HUD-разметка",
    };

    /// <summary>Описания параметров оверлея или бирки или null (не реализует IOverlayParams).</summary>
    public IReadOnlyList<SceneParamSpec>? OverlaySpecs(string overlayId) => _specs.Overlay(overlayId);

    // ---------- Вкл / выкл ----------

    public Pick GetOverlay(string overlayId) => OverlayOn(overlayId) ? Pick.On : Pick.Off;

    /// <summary>Переключить оверлей: выкл ↔ вкл (без «любимых»).</summary>
    public void ToggleOverlay(string overlayId)
    {
        switch (overlayId)
        {
            case "hud": Config.Overlay.Enabled = !Config.Overlay.Enabled; break;
            case "vcr": Config.Overlay.Vcr = !Config.Overlay.Vcr; break;
            case "clock": Config.Overlay.Clock = !Config.Overlay.Clock; break;
            case "logo": Config.Overlay.Logo = !Config.Overlay.Logo; break;
        }
    }

    private bool OverlayOn(string overlayId) => overlayId switch
    {
        "hud" => Config.Overlay.Enabled,
        "vcr" => Config.Overlay.Vcr,
        "clock" => Config.Overlay.Clock,
        "logo" => Config.Overlay.Logo,
        _ => false,
    };

    /// <summary>Вероятность HUD-оверлея (Overlay.Probability), 0..1. Для окна — проценты.</summary>
    public double OverlayProbability
    {
        get => Config.Overlay.Probability;
        set => Config.Overlay.Probability = Math.Clamp(value, 0, 1);
    }

    /// <summary>Умолчание вероятности HUD-оверлея (из OverlayConfig).</summary>
    public static double DefaultOverlayProbability => new OverlayConfig().Probability;

    // ---------- Параметры оверлеев ----------

    /// <summary>Текущее значение параметра оверлея: из overlay.params.&lt;id&gt; или умолчание.</summary>
    public double OverlayParamValue(string overlayId, SceneParamSpec spec) => KeyedValue(Config.Overlay.Params, overlayId, spec);

    /// <summary>Записать параметр оверлея в overlay.params.&lt;id&gt;. Умолчание — ключ удаляется. Choice пишется строкой (как у сцен).</summary>
    public void SetOverlayParam(string overlayId, SceneParamSpec spec, double value) =>
        Config.Overlay.Params = KeyedSet(Config.Overlay.Params, overlayId, spec, value);

    /// <summary>«По умолчанию» у одного параметра оверлея.</summary>
    public void ResetOverlayParam(string overlayId, SceneParamSpec spec) => SetOverlayParam(overlayId, spec, spec.Default);

    /// <summary>
    /// «Все по умолчанию» для оверлея: ключи параметров из описания и строки (шрифт, текст, файл); у HUD — вероятность.
    /// Неизвестные ключи остаются.
    /// </summary>
    public void ResetOverlay(string overlayId)
    {
        var specs = OverlaySpecs(overlayId);
        if (specs is { Count: > 0 }) Config.Overlay.Params = KeyedReset(Config.Overlay.Params, overlayId, specs);
        switch (overlayId)
        {
            case "vcr": SetOverlayFont(""); break;
            case "clock": SetOverlayString("clock", "font", ""); break;
            case "logo":
                SetOverlayString("logo", "font", "");
                SetOverlayString("logo", "text", "");
                SetOverlayString("logo", "file", "");
                break;
            case "hud": OverlayProbability = DefaultOverlayProbability; break;
        }
    }

    /// <summary>Строковое значение overlay.params.&lt;id&gt;.&lt;key&gt; (шрифт, текст, файл); нет ключа — "".</summary>
    public string OverlayString(string overlayId, string key)
    {
        var root = Config.Overlay.Params;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(overlayId, out var o) && o.ValueKind == JsonValueKind.Object
            && o.TryGetProperty(key, out var f) && f.ValueKind == JsonValueKind.String)
            return f.GetString() ?? "";
        return "";
    }

    /// <summary>Записать строку в overlay.params.&lt;id&gt;.&lt;key&gt;. Пусто — ключ удаляется (пустой объект id тоже).</summary>
    public void SetOverlayString(string overlayId, string key, string value)
    {
        var root = ToObject(Config.Overlay.Params);
        var node = root[overlayId] as JsonObject;
        bool existed = node is not null;
        node ??= new JsonObject();
        if (value.Length == 0) node.Remove(key);
        else node[key] = JsonValue.Create(value);
        if (node.Count == 0) root.Remove(overlayId);
        else if (!existed) root[overlayId] = node;
        Config.Overlay.Params = ToElement(root);
    }

    /// <summary>Шрифт OSD видика (overlay.params.vcr.font). Пусто — «как у сетки».</summary>
    public string OverlayFont => OverlayString("vcr", "font");

    /// <summary>Записать шрифт OSD строкой. Пусто — ключ удаляется (как у сетки).</summary>
    public void SetOverlayFont(string family) => SetOverlayString("vcr", "font", family);

    /// <summary>
    /// Активна ли строка бирки (не из Params): logo.file — только при source=file; logo.text и logo.font — только при source=text.
    /// Остальные строки всегда активны. Нет описания source — активны все.
    /// </summary>
    public bool OverlayFieldActive(string overlayId, string key)
    {
        if (overlayId != "logo" || key is not ("file" or "text" or "font")) return true;
        var source = OverlaySpecs("logo")?.FirstOrDefault(s => s.Name == "source");
        if (source?.Options is not { Length: > 0 } opts) return true;
        int v = Math.Clamp((int)Math.Round(OverlayParamValue("logo", source)), 0, opts.Length - 1);
        return key == "file" ? opts[v] == "file" : opts[v] == "text";
    }

    // ---------- Корпус монитора (ScreenConfig) ----------

    /// <summary>true — кинескоп (корпус включён), false — «плоский экран». Значения полей при этом не меняются.</summary>
    public bool ScreenEnabled
    {
        get => Config.Screen.Enabled;
        set => Config.Screen.Enabled = value;
    }

    /// <summary>Значение параметра экрана по имени из ScreenConfig.Specs (Choice — индекс).</summary>
    public double ScreenValue(string name) => Config.Screen.Get(name);

    /// <summary>Записать параметр экрана (клампится в Min..Max).</summary>
    public void SetScreenParam(string name, double value) => Config.Screen.Set(name, value);

    /// <summary>«По умолчанию» у одного параметра экрана.</summary>
    public void ResetScreenParam(string name)
    {
        var spec = ScreenConfig.Specs.First(s => s.Name == name);
        Config.Screen.Set(name, spec.Default);
    }

    /// <summary>«Вернуть всё»: все параметры экрана — по умолчанию.</summary>
    public void ResetScreen()
    {
        foreach (var s in ScreenConfig.Specs) ResetScreenParam(s.Name);
    }
}
