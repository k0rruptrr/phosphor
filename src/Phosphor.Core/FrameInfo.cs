namespace Phosphor.Core;

/// <summary>Информация о кадре для фильтров.</summary>
public readonly record struct FrameInfo(
    float Time,       // секунды с запуска
    float Dt,         // секунды с прошлого кадра
    long Frame,       // номер кадра
    int Width,        // размер картинки в пикселях
    int Height,
    int MonitorIndex, // 0..N-1
    AudioFrame Audio,
    Organism Organism = default,
    float ArtifactScale = 1f) // текстовая сцена: артефактные параметры фильтров (ParamSpec.Clean) × это, 1 — как в пресете
{
    /// <summary>Значение источника модуляции: звук ("bass", "beatPulse"...) или организм ("breath", "night"...).</summary>
    public float Get(string source) => Array.IndexOf(Organism.Sources, source) >= 0
        ? Organism.Get(source)
        : Audio.Active ? Audio.Get(source) : 0f;

    public static bool IsSource(string source) =>
        Array.IndexOf(AudioFrame.Sources, source) >= 0 || Array.IndexOf(Organism.Sources, source) >= 0;
}
