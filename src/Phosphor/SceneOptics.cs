using System.Text.Json;

namespace Phosphor.App;

/// <summary>
/// Оптика сцены (ISceneOptics): её стадии идут перед стадиями пресета. Чистая логика без GL.
/// </summary>
public static class SceneOptics
{
    /// <summary>
    /// Склеить стадии: оптика сцены + стадии пресета (массив; не массив — пусто). optics — JSON-массив или null.
    /// enabled = false (scenes.params.&lt;id&gt;.optics = false) — только пресет. Битая оптика — только пресет и сообщение в warn.
    /// </summary>
    public static JsonElement Merge(string? optics, JsonElement preset, bool enabled, Action<string>? warn = null)
    {
        var parts = new List<string>();
        if (enabled && !string.IsNullOrWhiteSpace(optics))
        {
            try
            {
                using var doc = JsonDocument.Parse(optics);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var st in doc.RootElement.EnumerateArray()) parts.Add(st.GetRawText());
                else warn?.Invoke("оптика сцены — не массив стадий, пропущена");
            }
            catch (JsonException e) { warn?.Invoke($"оптика сцены не разобрана: {e.Message}"); }
        }
        if (preset.ValueKind == JsonValueKind.Array)
            foreach (var st in preset.EnumerateArray()) parts.Add(st.GetRawText());
        return JsonDocument.Parse("[" + string.Join(",", parts) + "]").RootElement;
    }
}
