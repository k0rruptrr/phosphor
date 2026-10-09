using System.Reflection;
using Phosphor.Core;
using Phosphor.Filters;
using Phosphor.Gfx;
using Phosphor.Scenes;

namespace Phosphor.App;

/// <summary>
/// Сцены и фильтры находятся рефлексией: любой публичный неабстрактный класс с IScene / IFilter
/// и конструктором без параметров регистрируется под своим Id. Новая сцена = новый файл, больше ничего.
/// </summary>
internal static class Catalog
{
    public static SceneRegistry Scenes()
    {
        var r = new SceneRegistry();
        foreach (var t in Discover<IScene>(typeof(MatrixScene).Assembly))
        {
            var id = ((IScene)Activator.CreateInstance(t)!).Id;
            r.Register(id, () => (IScene)Activator.CreateInstance(t)!);
        }
        return r;
    }

    public static FilterRegistry Filters()
    {
        var r = new FilterRegistry();
        foreach (var t in Discover<IFilter>(typeof(CrtFilter).Assembly))
        {
            using var probe = (IFilter)Activator.CreateInstance(t)!;
            var id = probe.Id;
            r.Register(id, () => (IFilter)Activator.CreateInstance(t)!);
        }
        return r;
    }

    /// <summary>Шаги обработки сетки (IGridPass) — живут в сборке фильтров рядом с GPU-фильтрами.</summary>
    public static IReadOnlyDictionary<string, Func<IGridPass>> GridPasses()
    {
        var r = new Dictionary<string, Func<IGridPass>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Discover<IGridPass>(typeof(CrtFilter).Assembly))
            r[((IGridPass)Activator.CreateInstance(t)!).Id] = () => (IGridPass)Activator.CreateInstance(t)!;
        return r;
    }

    /// <summary>Оверлеи из сборки Phosphor.Overlays (по имени — без жёсткой ссылки на конкретные классы).</summary>
    public static IReadOnlyDictionary<string, Func<IOverlay>> Overlays()
    {
        var r = new Dictionary<string, Func<IOverlay>>(StringComparer.OrdinalIgnoreCase);
        Assembly asm;
        try { asm = Assembly.Load("Phosphor.Overlays"); }
        catch (Exception) { return r; }
        foreach (var t in Discover<IOverlay>(asm))
        {
            var id = ((IOverlay)Activator.CreateInstance(t)!).Id;
            r[id] = () => (IOverlay)Activator.CreateInstance(t)!;
        }
        return r;
    }

    private static IEnumerable<Type> Discover<T>(Assembly asm) =>
        asm.GetTypes()
           .Where(t => t.IsPublic && !t.IsAbstract && typeof(T).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
           .OrderBy(t => t.Name, StringComparer.Ordinal);
}
