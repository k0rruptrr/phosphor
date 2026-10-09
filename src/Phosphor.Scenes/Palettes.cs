using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// Общие палитры сцен. Неизвестное имя = "default" (зелёная).
/// Вызывать только из Init: здесь есть строковые операции.
/// </summary>
internal static class Palettes
{
    /// <summary>Базовый цвет хвоста. Для "rainbow" это оттенок при hue=0; для колонки см. ColumnBase.</summary>
    public static Rgba Base(string? name) => Kind(name) switch
    {
        "amber" => Rgba.Hex(0xFFB000),
        "mono" => Rgba.Hex(0xC8C8C8),
        _ => Rgba.Hex(0x00FF41),
    };

    /// <summary>Цвет колонки: для "rainbow" — оттенок по номеру колонки, для остальных — Base.</summary>
    public static Rgba ColumnBase(string? name, int col, int cols) =>
        Kind(name) == "rainbow" ? Rgba.Hsv(col * 360f / cols, 0.8f, 1f) : Base(name);

    /// <summary>Цвет головы струи для базового цвета колонки.</summary>
    public static Rgba Head(string? name, Rgba baseColor) =>
        Kind(name) == "green" ? Rgba.Hex(0xCCFFCC) : Rgba.Lerp(baseColor, Rgba.White, 0.8f);

    private static string Kind(string? name) => name?.ToLowerInvariant() switch
    {
        "amber" => "amber",
        "mono" => "mono",
        "rainbow" => "rainbow",
        _ => "green",
    };
}
