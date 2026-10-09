namespace Phosphor.Core;

/// <summary>Реестр сцен: id → фабрика.</summary>
public sealed class SceneRegistry
{
    private readonly Dictionary<string, Func<IScene>> _factories = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string id, Func<IScene> factory) => _factories[id] = factory;
    public IReadOnlyCollection<string> Ids => _factories.Keys;
    public bool Has(string id) => _factories.ContainsKey(id);

    public IScene Create(string id) =>
        _factories.TryGetValue(id, out var f) ? f()
        : throw new KeyNotFoundException($"Неизвестная сцена '{id}'. Есть: {string.Join(", ", _factories.Keys)}");
}
