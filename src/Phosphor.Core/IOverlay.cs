namespace Phosphor.Core;

/// <summary>Что оверлей знает о кадре (кроме самой сетки).</summary>
public readonly record struct OverlayContext(
    string SceneId,
    string PresetName,
    int MonitorIndex,
    int MonitorCount,
    float Fps,
    long Frame,
    DateTime? Clock,      // реальное время, если хост его дал (null в тестах — рисуй «--:--:--»)
    AudioFrame Audio,
    Organism Organism);

/// <summary>
/// Слой поверх ЛЮБОЙ сцены: получает уже нарисованную сцену в CharGrid и дорисовывает разметку
/// (рамки захвата, подписи, линейки, свотчи). Ничего не знает о сцене, кроме её Id и сетки.
/// Порядок кадра: scene.Update → scene.Draw(grid) → overlay.Update(dt) → overlay.Apply(grid, ctx).
/// Apply может анализировать сетку и менять своё состояние (трекинг целей), но не вызывает rng.
/// </summary>
public interface IOverlay
{
    string Id { get; }
    void Init(int cols, int rows, Random rng);
    void Update(float dt);
    void Apply(CharGrid grid, in OverlayContext ctx);
}
