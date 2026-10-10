using Phosphor.Filters;
using Phosphor.Gfx;

namespace Phosphor.Filters.Tests;

/// <summary>Короткие подписи параметров фильтров для окна настроек: есть у каждого параметра и не длиннее 22 символов.</summary>
public class FilterLabelTests
{
    private const int MaxLabelLength = 22;

    /// <summary>Все фильтры каталога — той же рефлексией, что и Catalog.Filters() в приложении.</summary>
    public static IEnumerable<object[]> FilterTypes() =>
        typeof(CrtFilter).Assembly.GetTypes()
            .Where(t => t.IsPublic && !t.IsAbstract && typeof(IFilter).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new object[] { t });

    [Fact]
    public void CatalogHasFilters()
    {
        Assert.NotEmpty(FilterTypes());
    }

    [Theory]
    [MemberData(nameof(FilterTypes))]
    public void EveryParamHasShortLabel(Type type)
    {
        using var filter = (IFilter)Activator.CreateInstance(type)!;
        Assert.NotEmpty(filter.Params);
        foreach (var p in filter.Params)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Label), $"{type.Name}.{p.Name}: нет Label");
            Assert.True(p.Label!.Length <= MaxLabelLength, $"{type.Name}.{p.Name}: Label «{p.Label}» длиннее {MaxLabelLength} символов");
        }
    }
}
