namespace Phosphor.Core;

/// <summary>
/// Звук на текущий кадр. Значения нормированы и сглажены.
/// Active=false — музыки нет (тишина больше ~3 с или захват недоступен): все поля 0, реагировать не надо.
/// </summary>
public readonly record struct AudioFrame(
    bool Active,
    float Level,     // общая громкость 0..1
    float Bass,      // 20–150 Гц, 0..1
    float Mid,       // 150–2000 Гц, 0..1
    float High,      // 2–16 кГц, 0..1
    bool Beat,       // true ровно в одном кадре — в момент удара
    float BeatPulse, // 1 в момент удара, экспоненциально затухает к 0 (~150 мс)
    float Bpm)       // оценка темпа, 0 если неизвестен
{
    public static readonly AudioFrame Silent = default;

    /// <summary>Значения источников модуляции по имени (поле "src" в конфиге фильтров).</summary>
    public float Get(string source) => source switch
    {
        "level" => Level,
        "bass" => Bass,
        "mid" => Mid,
        "high" => High,
        "beat" => Beat ? 1f : 0f,
        "beatPulse" => BeatPulse,
        _ => 0f,
    };

    public static readonly string[] Sources = { "level", "bass", "mid", "high", "beat", "beatPulse" };
}
