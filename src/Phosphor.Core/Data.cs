namespace Phosphor.Core;

/// <summary>
/// Необязательно. Сцена, которой нужны внешние данные (новости, погода, телетекст, спутник, часы).
/// Сцена НЕ ходит в сеть и не читает файлы — хост сам доставляет снимки данных в главном потоке:
/// сразу после Init (если в кеше что-то есть) и при каждом обновлении. Снимки неизменяемые.
/// Без сети сцена обязана красиво работать с тем, что есть (кеш или процедурная заглушка).
/// </summary>
public interface IDataScene
{
    /// <summary>Ключи потоков, на которые сцена подписана: "clock", "weather", "news", "teletext", "satellite".</summary>
    IReadOnlyList<string> DataKeys { get; }

    /// <summary>Новый снимок по ключу. Вызывается до Update. Тип снимка — по ключу (см. ниже).</summary>
    void OnData(string key, DataSnapshot snapshot);
}

/// <summary>База снимка: когда получены данные и откуда (сеть/кеш).</summary>
public abstract record DataSnapshot(DateTimeOffset FetchedAt, bool FromCache);

/// <summary>"clock" — раз в секунду. Единственный разрешённый для сцен источник реального времени.</summary>
public sealed record ClockSnapshot(DateTime Local) : DataSnapshot(DateTimeOffset.Now, false);

/// <summary>"news" — заголовки из нескольких источников, свежие первыми.</summary>
public sealed record NewsSnapshot(IReadOnlyList<NewsItem> Items, DateTimeOffset FetchedAt, bool FromCache)
    : DataSnapshot(FetchedAt, FromCache);

/// <param name="Source">Короткая метка источника капсом: "TASS", "BBC", "R/WORLDNEWS".</param>
/// <param name="Lang">ISO-код языка заголовка: "ru", "en", "de"...</param>
public sealed record NewsItem(string Source, string Title, DateTimeOffset? Published, string Lang);

/// <summary>"weather" — текущая погода и прогноз по дням.</summary>
public sealed record WeatherSnapshot(string City, double Lat, double Lon, WeatherNow Now, IReadOnlyList<WeatherDay> Days,
    DateTimeOffset FetchedAt, bool FromCache) : DataSnapshot(FetchedAt, FromCache);

/// <param name="Code">WMO weather code (как в Open-Meteo).</param>
public sealed record WeatherNow(double TempC, double WindKmh, double WindDirDeg, int Code);
public sealed record WeatherDay(DateOnly Date, double MinC, double MaxC, double PrecipMm, int Code);

/// <summary>"teletext" — набор страниц одного или нескольких каналов.</summary>
public sealed record TeletextSnapshot(IReadOnlyList<TeletextPage> Pages, DateTimeOffset FetchedAt, bool FromCache)
    : DataSnapshot(FetchedAt, FromCache);

/// <summary>
/// Страница телетекста, нормализованная: Rows × Cols ячеек (обычно 25 × 40).
/// Цвета — индексы классической палитры 0..7: чёрный, красный, зелёный, жёлтый, синий, пурпурный, голубой, белый.
/// Mosaic[i] != 0 — ячейка мозаичная: биты 0..5 = секстели 2×3 (бит 0 — верх-лево, 1 — верх-право, 2 — середина-лево,
/// 3 — середина-право, 4 — низ-лево, 5 — низ-право); тогда Chars[i] игнорируется.
/// </summary>
public sealed record TeletextPage(string Channel, string Lang, int Page, int SubPage, int Rows, int Cols,
    char[] Chars, byte[] Fg, byte[] Bg, byte[] Mosaic);

/// <summary>"satellite" — кадр спутника, уже уменьшенный до разумного размера (RGB, построчно сверху вниз).</summary>
public sealed record SatelliteSnapshot(string Source, DateTimeOffset TakenAt, int Width, int Height, byte[] Rgb,
    DateTimeOffset FetchedAt, bool FromCache) : DataSnapshot(FetchedAt, FromCache);

/// <summary>Классическая палитра телетекста (индекс → цвет).</summary>
public static class TeletextColors
{
    public static readonly Rgba[] Palette =
    {
        Rgba.Hex(0x000000), Rgba.Hex(0xFF0000), Rgba.Hex(0x00FF00), Rgba.Hex(0xFFFF00),
        Rgba.Hex(0x0000FF), Rgba.Hex(0xFF00FF), Rgba.Hex(0x00FFFF), Rgba.Hex(0xFFFFFF),
    };
}
