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

/// <summary>
/// "weather" — текущая погода и прогноз по дням.
/// Source — провайдер ("open-meteo" или "met.no"). Query — запрос города из конфига (нужен, чтобы координаты из кеша
/// не подменяли другой город); null — кеш старого формата.
/// Пропущенные значения источника — double.NaN, код погоды — -1: сцена рисует «--».
/// </summary>
public sealed record WeatherSnapshot(string City, double Lat, double Lon, WeatherNow Now, IReadOnlyList<WeatherDay> Days,
    DateTimeOffset FetchedAt, bool FromCache, string Source = "open-meteo", string? Query = null) : DataSnapshot(FetchedAt, FromCache);

/// <param name="Code">WMO weather code (как в Open-Meteo); -1 — нет данных.</param>
public sealed record WeatherNow(double TempC, double WindKmh, double WindDirDeg, int Code);
public sealed record WeatherDay(DateOnly Date, double MinC, double MaxC, double PrecipMm, int Code);

/// <summary>
/// Статус источника ключа "&lt;key&gt;.status" (например "weather.status"). Не кешируется; хаб публикует его при каждой попытке.
/// Kind = null — последняя попытка удалась. Иначе: BLOCKED (WSAEACCES, фаервол), TIMEOUT, DNS, HTTP, NET, NODATA, PARSE, OFF (сеть выключена в конфиге), ERROR.
/// </summary>
public sealed record SourceStatusSnapshot(string? Kind, string? Message, DateTimeOffset LastAttempt) : DataSnapshot(LastAttempt, false);

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

/// <summary>
/// "satellite" — одна спутниковая лента (источник = лента). Несколько лент одного ключа хост сливает в
/// SatelliteSetSnapshot (см. SatelliteCatalog.Merge в Phosphor.Data); сцена получает именно его.
/// Frames — кольцо последних кадров, старые первыми (для таймлапса); у суточных лент — 1–2 кадра.
/// </summary>
/// <param name="Id">"meteosat" | "himawari" | "viirs".</param>
/// <param name="Label">Подпись для HUD капсом: "METEOSAT-12 · FCI · GEOCOLOUR".</param>
/// <param name="Area">Где кадр на Земле.</param>
public sealed record SatelliteFeed(string Id, string Label, SatelliteArea Area, IReadOnlyList<SatelliteFrame> Frames,
    DateTimeOffset FetchedAt, bool FromCache) : DataSnapshot(FetchedAt, FromCache);

/// <summary>"satellite" после слияния — все доступные ленты (пустых лент в списке нет).</summary>
public sealed record SatelliteSetSnapshot(IReadOnlyList<SatelliteFeed> Feeds, DateTimeOffset FetchedAt, bool FromCache)
    : DataSnapshot(FetchedAt, FromCache);

/// <summary>Кадр: RGB построчно сверху вниз, Width × Height × 3 байта, уже уменьшен (≤ 480 по большей стороне).</summary>
public sealed record SatelliteFrame(DateTimeOffset TakenAt, int Width, int Height, byte[] Rgb);

/// <summary>
/// Геопривязка кадра.
/// Equirect: пиксель (x, y) ↔ lon = West + (x+0.5)/W·(East−West), lat = North − (y+0.5)/H·(North−South).
/// FullDisk: вид геостационара на весь диск с точки над (0°, SubLon); диск вписан в кадр, радиус = Width/2·DiskScale.
/// MarkLat/MarkLon — точка интереса (город из конфига), NaN — нет; MarkName — подпись капсом.
/// </summary>
public sealed record SatelliteArea(SatelliteProjection Projection, double West, double South, double East, double North,
    double SubLon, double DiskScale, double MarkLat, double MarkLon, string? MarkName);

public enum SatelliteProjection { Equirect, FullDisk }

/// <summary>"system" — состояние ПК, обновляется хостом ~2 раза в секунду (не сеть, не кеш).</summary>
/// <param name="CorePct">Загрузка каждого логического ядра 0..100.</param>
/// <param name="GpuPct">Загрузка GPU 0..100, NaN — неизвестно.</param>
/// <param name="NetDownBps">Байт/с по всем интерфейсам; диск — тоже байт/с.</param>
public sealed record SystemSnapshot(string Host, string Os, TimeSpan Uptime, float CpuPct, float[] CorePct,
    double RamUsedGb, double RamTotalGb, float GpuPct, string? GpuName, double NetDownBps, double NetUpBps,
    double DiskReadBps, double DiskWriteBps, int Processes, DateTimeOffset At) : DataSnapshot(At, false);

/// <summary>"phosphor" — самочувствие самой заставки, ~2 раза в секунду.</summary>
/// <param name="Scenes">Текущая сцена на каждом мониторе (по порядку мониторов); Presets — пресет фильтров там же.</param>
/// <param name="Bpm">Темп музыки, 0 — музыки нет.</param>
/// <param name="Fatigue">Усталость дирижёра 0..1; Breath — фаза дыхания 0..1; Night — ночной режим.</param>
public sealed record PhosphorSnapshot(float Fps, IReadOnlyList<string> Scenes, IReadOnlyList<string> Presets, float Bpm,
    float Fatigue, float Breath, bool Night, TimeSpan Uptime, long Frames, DateTimeOffset At) : DataSnapshot(At, false);

/// <summary>Классическая палитра телетекста (индекс → цвет).</summary>
public static class TeletextColors
{
    public static readonly Rgba[] Palette =
    {
        Rgba.Hex(0x000000), Rgba.Hex(0xFF0000), Rgba.Hex(0x00FF00), Rgba.Hex(0xFFFF00),
        Rgba.Hex(0x0000FF), Rgba.Hex(0xFF00FF), Rgba.Hex(0x00FFFF), Rgba.Hex(0xFFFFFF),
    };
}

/// <summary>
/// "sattv" — список каналов одного спутника (источник = спутник, данные KingOfSat, обновление раз в сутки).
/// Несколько спутников хост сливает в SatTvSetSnapshot (SatTvCatalog.Merge в Phosphor.Data); сцена получает его.
/// </summary>
/// <param name="Id">id из конфига (network.satTv), например "astra1n".</param>
/// <param name="Name">Имя спутника как на сайте: "Astra 1N".</param>
/// <param name="Position">Орбитальная позиция: "19.2°E".</param>
/// <param name="Channels">Только ТВ-каналы (без радио и data), по возрастанию частоты; не больше 400.</param>
public sealed record SatTvSatellite(string Id, string Name, string Position, IReadOnlyList<SatTvChannel> Channels,
    DateTimeOffset FetchedAt, bool FromCache) : DataSnapshot(FetchedAt, FromCache);

/// <summary>Один канал.</summary>
/// <param name="FreqMhz">Частота, МГц: 11214.25.</param>
/// <param name="Pol">'H' | 'V' | 'L' | 'R'.</param>
/// <param name="SymbolRate">Символьная скорость, kSym/s (27500), 0 — неизвестно.</param>
/// <param name="Fec">"3/4", "" — неизвестно.</param>
/// <param name="Standard">"DVB-S" | "DVB-S2" | "DVB-S2X" | "".</param>
/// <param name="Country">Страна/язык как на сайте, коротко: "Germany", "Russia", "" — неизвестно.</param>
/// <param name="Fta">Открытый канал (Free To Air).</param>
public sealed record SatTvChannel(string Name, double FreqMhz, char Pol, int SymbolRate, string Fec, string Standard,
    string Country, bool Fta);

/// <summary>"sattv" после слияния — спутники с непустыми списками, в порядке конфига.</summary>
public sealed record SatTvSetSnapshot(IReadOnlyList<SatTvSatellite> Satellites, DateTimeOffset FetchedAt, bool FromCache)
    : DataSnapshot(FetchedAt, FromCache);

/// <summary>
/// "fires" — активные пожары за 24 ч вокруг города (NASA FIRMS, VIIRS NOAA-20, глобальный CSV без ключа, обновление раз в 3 ч).
/// Центр — город из конфига, округлённый до 0.5° (примерное местоположение); в квадрате ±RadiusKm.
/// </summary>
public sealed record FireSnapshot(string CenterName, double CenterLat, double CenterLon, double RadiusKm,
    IReadOnlyList<FirePoint> Points, DateTimeOffset FetchedAt, bool FromCache) : DataSnapshot(FetchedAt, FromCache);

/// <summary>Очаг. Frp — мощность, МВт; BrightK — яркостная температура, K; Confidence — "low" | "nominal" | "high".</summary>
public sealed record FirePoint(double Lat, double Lon, float Frp, float BrightK, string Confidence, DateTimeOffset AcquiredAt, bool Day);

/// <summary>
/// "flights" — самолёты вокруг точки (ADS-B, adsb.lol, опрос раз в 60 с). Если над городом меньше 3 самолётов в воздухе —
/// источник берёт «дальнюю станцию» из конфига по кругу: Remote = true, StationName — её имя.
/// </summary>
public sealed record FlightSnapshot(string StationName, bool Remote, double CenterLat, double CenterLon, double RadiusNm,
    IReadOnlyList<Aircraft> Aircraft, DateTimeOffset FetchedAt, bool FromCache) : DataSnapshot(FetchedAt, FromCache);

/// <summary>Самолёт. AltFt &lt; 0 — на земле. Callsign/Type — "" если нет. TrackDeg — курс, 0 = север, по часовой.</summary>
public sealed record Aircraft(string Hex, string Callsign, string Type, double Lat, double Lon, int AltFt,
    float SpeedKt, float TrackDeg, float VertRateFpm);
