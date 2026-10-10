using System.Text.Json;
using System.Text.Json.Nodes;
using Phosphor.Core;

namespace Phosphor.App.Settings;

/// <summary>
/// Оверлеи (вкладка «Оверлеи») и корпус монитора (вкладка «Экран»). Пишет прямо в Config.
/// Оверлеи: вкл/выкл — Overlay.Enabled (HUD) и Overlay.Vcr (OSD видика); параметры — overlay.params.&lt;id&gt;; шрифт OSD — overlay.params.vcr.font.
/// </summary>
public sealed partial class SettingsModel
{
    /// <summary>Строки списка «Оверлеи»: HUD-разметка и OSD видика.</summary>
    public static readonly string[] OverlayIds = { "hud", "vcr" };

    /// <summary>Название строки списка «Оверлеи».</summary>
    public static string OverlayTitle(string id) => id switch
    {
        "vcr" => "OSD видика",
        _ => "HUD-разметка",
    };

    /// <summary>Описания параметров оверлея или null (оверлей не реализует IOverlayParams).</summary>
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
        }
    }

    private bool OverlayOn(string overlayId) => overlayId switch
    {
        "hud" => Config.Overlay.Enabled,
        "vcr" => Config.Overlay.Vcr,
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
    /// «Все по умолчанию» для оверлея: ключи параметров из описания, для VCR — ещё шрифт OSD; у HUD — вероятность.
    /// Неизвестные ключи остаются.
    /// </summary>
    public void ResetOverlay(string overlayId)
    {
        var specs = OverlaySpecs(overlayId);
        if (specs is { Count: > 0 }) Config.Overlay.Params = KeyedReset(Config.Overlay.Params, overlayId, specs);
        if (overlayId == "vcr") SetOverlayFont("");
        if (overlayId == "hud") OverlayProbability = DefaultOverlayProbability;
    }

    /// <summary>Шрифт OSD видика (overlay.params.vcr.font). Пусто — «как у сетки».</summary>
    public string OverlayFont
    {
        get
        {
            var root = Config.Overlay.Params;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("vcr", out var vcr) && vcr.ValueKind == JsonValueKind.Object
                && vcr.TryGetProperty("font", out var f) && f.ValueKind == JsonValueKind.String)
                return f.GetString() ?? "";
            return "";
        }
    }

    /// <summary>Записать шрифт OSD строкой. Пусто — ключ удаляется (как у сетки).</summary>
    public void SetOverlayFont(string family)
    {
        var root = ToObject(Config.Overlay.Params);
        var vcr = root["vcr"] as JsonObject;
        bool existed = vcr is not null;
        vcr ??= new JsonObject();
        if (family.Length == 0) vcr.Remove("font");
        else vcr["font"] = JsonValue.Create(family);
        if (vcr.Count == 0) root.Remove("vcr");
        else if (!existed) root["vcr"] = vcr;
        Config.Overlay.Params = ToElement(root);
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
