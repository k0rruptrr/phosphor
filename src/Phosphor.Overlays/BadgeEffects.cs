using Phosphor.Core;

namespace Phosphor.Overlays;

/// <summary>
/// Эффекты бирки (часы и логотип): подложка, тень, свечение, силуэт. Заполняется в Configure, хост читает через Effects.
/// Plate: none / rounded / pill. PlateColor: black / white / grey. Tint — цвет бирки (color), для силуэта и свечения текста.
/// </summary>
public readonly record struct BadgeEffects(
    string Plate, string PlateColor, float PlateOpacity, float Shadow, float Glow, bool Silhouette, Rgba Tint)
{
    public static readonly string[] Plates = { "none", "rounded", "pill" };
    public static readonly string[] PlateColors = { "black", "white", "grey" };

    /// <summary>Читает параметры эффектов из options (имена — как в overlay.params.*). Цвет tint передаёт бирка.</summary>
    public static BadgeEffects Read(SceneOptions o, Rgba tint) => new(
        Plates[ChoiceIndex(o, "plate", Plates, 0)],
        PlateColors[ChoiceIndex(o, "plateColor", PlateColors, 0)],
        Math.Clamp(o.GetFloat("plateOpacity", 0.5f), 0f, 1f),
        Math.Clamp(o.GetFloat("shadow", 0f), 0f, 1f),
        Math.Clamp(o.GetFloat("glow", 0f), 0f, 1f),
        o.GetBool("silhouette", false),
        tint);

    private static int ChoiceIndex(SceneOptions o, string name, string[] options, int def)
    {
        int i = Array.IndexOf(options, o.GetString(name, options[def]));
        return i < 0 ? def : i;
    }
}
