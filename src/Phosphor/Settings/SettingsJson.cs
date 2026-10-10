using System.Text.Json;
using Phosphor.Core;

namespace Phosphor.App.Settings;

/// <summary>Текущий конфиг окна как JSON того же вида, что phosphor.json. Нужен превью: на диск не пишет (в отличие от PhosphorConfig.Save).</summary>
internal static class SettingsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string Serialize(PhosphorConfig config) => JsonSerializer.Serialize(config, Options);
}
