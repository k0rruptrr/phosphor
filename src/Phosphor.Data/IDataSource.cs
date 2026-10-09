using Phosphor.Core;

namespace Phosphor.Data;

/// <summary>
/// Источник данных. Живёт в фоновом потоке DataHub. Один источник = один ключ потока ("news", "weather", ...).
/// Несколько источников одного ключа (TASS + BBC + Reddit) объединяет агрегатор — см. NewsAggregator.
/// </summary>
public interface IDataSource
{
    /// <summary>Ключ потока, в который пишет источник.</summary>
    string Key { get; }

    /// <summary>Уникальное имя для кеша и логов: "news.tass", "weather", "teletext.svt".</summary>
    string Name { get; }

    /// <summary>Как часто обновлять. Минимум, который хаб соблюдает, — 60 с (вежливость к серверам).</summary>
    TimeSpan Interval { get; }

    /// <summary>
    /// Скачать и разобрать. Исключения можно бросать — хаб их ловит, пишет в лог и повторяет позже
    /// (экспоненциальная задержка до Interval). Вернуть null = данных нет, кеш не трогать.
    /// </summary>
    Task<DataSnapshot?> FetchAsync(HttpClient http, CancellationToken ct);
}
