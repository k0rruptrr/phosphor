using System.Text.Json;

namespace Phosphor.Core;

/// <summary>Доля закрашенных пикселей глифа в ячейке, 0..1 (пробел = 0, '█' = 1). Даёт рендерер по атласу.</summary>
public interface IGlyphCoverage
{
    float Coverage(char c);
}

/// <summary>
/// Шаг обработки сетки символов между сценой и рендером (до GPU-фильтров). Пишется в пресете рядом с фильтрами:
/// { "id": "mono", ... } — хост сам отделяет шаги сетки от GPU-фильтров по реестру и запускает их первыми,
/// в порядке из пресета. Шаг правит сетку на месте и не аллоцирует в Apply.
/// </summary>
public interface IGridPass
{
    string Id { get; }
    /// <summary>Параметры из объекта стадии пресета (поле "id" игнорировать). Неизвестное/кривое — в warnings, не исключение.</summary>
    void Configure(JsonElement stage, List<string> warnings);
    void Apply(CharGrid grid, IGlyphCoverage coverage);
}
