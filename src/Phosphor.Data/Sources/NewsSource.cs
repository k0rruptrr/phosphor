using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Phosphor.Core;

namespace Phosphor.Data;

/// <summary>
/// Один источник новостей (RSS 2.0 или Atom). Пишет в ключ "news"; несколько источников объединяет NewsCatalog.Merge.
/// </summary>
public sealed class NewsSource : IDataSource
{
    private const int MaxItems = 30;
    private static readonly XNamespace AtomNs = "http://www.w3.org/2005/Atom";
    private static readonly Regex TagRx = new("<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex SpacesRx = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex GmtRx = new(@"\s+(GMT|UTC|Z)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OffsetRx = new(@"([+-]\d{2})(\d{2})$", RegexOptions.Compiled);

    static NewsSource()
    {
        // XmlReader получает кодировку через Encoding.GetEncoding; windows-1251 без провайдера не находится.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private readonly string _id;
    private readonly string _label;
    private readonly string _url;
    private readonly string _lang;
    private readonly bool _atom;
    private readonly string? _titleSuffix;

    public NewsSource(string id, string label, string url, string lang, TimeSpan interval, bool atom)
    {
        _id = id;
        _label = label;
        _url = url;
        _lang = lang;
        Interval = interval;
        _atom = atom;
        _titleSuffix = id == "reuters" ? " - Reuters" : null;
    }

    public string Key => "news";

    // Двоеточие из "reddit:<sub>" недопустимо в имени файла кеша (NTFS), поэтому заменяем.
    public string Name => "news." + _id.Replace(':', '-');

    public TimeSpan Interval { get; }

    public async Task<DataSnapshot?> FetchAsync(HttpClient http, CancellationToken ct)
    {
        // Поток, а не строка: XDocument сам возьмёт кодировку из XML-декларации.
        using var stream = await http.GetStreamAsync(_url, ct).ConfigureAwait(false);
        var doc = XDocument.Load(stream);

        var entries = _atom ? doc.Descendants(AtomNs + "entry") : doc.Descendants("item");
        var items = new List<NewsItem>();
        foreach (var e in entries)
        {
            if (items.Count >= MaxItems) break;
            var title = Clean(_atom ? e.Element(AtomNs + "title")?.Value : e.Element("title")?.Value);
            if (title.Length == 0) continue;
            var published = ParseDate(_atom ? e.Element(AtomNs + "updated")?.Value : e.Element("pubDate")?.Value);
            items.Add(new NewsItem(_label, title, published, _lang));
        }

        if (items.Count == 0)
            throw new InvalidDataException($"{Name}: в ответе нет заголовков ({_url})");

        return new NewsSnapshot(items, DateTimeOffset.Now, false);
    }

    private string Clean(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var s = WebUtility.HtmlDecode(raw);
        s = TagRx.Replace(s, "");
        s = SpacesRx.Replace(s, " ").Trim();
        if (_titleSuffix is not null && s.EndsWith(_titleSuffix, StringComparison.Ordinal))
            s = s[..^_titleSuffix.Length].TrimEnd();
        s = GlyphSet.Sanitize(s);
        return SpacesRx.Replace(s, " ").Trim();
    }

    /// <summary>RFC 822 из RSS ("+0300", "GMT") и ISO 8601 из Atom.</summary>
    private static DateTimeOffset? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        t = GmtRx.Replace(t, " +00:00");
        t = OffsetRx.Replace(t, "$1:$2");
        return DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}

/// <summary>Каталог известных источников новостей и объединение их снимков.</summary>
public static class NewsCatalog
{
    private const int MaxMerged = 60;

    private static readonly (string Id, string Label, string Url, string Lang, int Minutes, bool Atom)[] Table =
    {
        ("tass", "TASS", "https://tass.ru/rss/v2.xml", "ru", 10, false),
        ("tass-en", "TASS", "https://tass.com/rss/v2.xml", "en", 10, false),
        ("ria", "RIA", "https://ria.ru/export/rss2/archive/index.xml", "ru", 10, false),
        ("interfax", "INTERFAX", "https://www.interfax.ru/rss.asp", "ru", 10, false),
        ("bbc", "BBC", "https://feeds.bbci.co.uk/news/world/rss.xml", "en", 10, false),
        ("reuters", "REUTERS", "https://news.google.com/rss/search?q=site:reuters.com&hl=en-US&gl=US&ceid=US:en", "en", 15, false),
    };

    /// <summary>Фиксированные id из каталога. "reddit:&lt;sub&gt;" создаётся через Create по запросу.</summary>
    public static IReadOnlyList<string> Ids { get; } = Table.Select(t => t.Id).ToArray();

    /// <summary>Источник по id из конфига. Неизвестный id или некорректный сабреддит — null.</summary>
    public static NewsSource? Create(string id)
    {
        if (id.StartsWith("reddit:", StringComparison.Ordinal))
        {
            var sub = id["reddit:".Length..];
            if (!IsSub(sub)) return null;
            return new NewsSource(id, "R/" + sub.ToUpperInvariant(),
                $"https://www.reddit.com/r/{sub}/top/.rss?t=day&limit=15", "en", TimeSpan.FromMinutes(30), true);
        }

        foreach (var t in Table)
            if (t.Id == id)
                return new NewsSource(t.Id, t.Label, t.Url, t.Lang, TimeSpan.FromMinutes(t.Minutes), t.Atom);
        return null;
    }

    private static bool IsSub(string sub) =>
        sub.Length > 0 && sub.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');

    /// <summary>
    /// Объединение снимков новостей: свежие первыми; внутри одного часа источники чередуются;
    /// без даты — в конце; не больше 60 элементов. FetchedAt — максимум, FromCache — только если все из кеша.
    /// </summary>
    public static DataSnapshot? Merge(IReadOnlyList<DataSnapshot> perSource)
    {
        var snaps = perSource.OfType<NewsSnapshot>().ToList();
        if (snaps.Count == 0) return null;

        var sorted = snaps.SelectMany(s => s.Items)
            .OrderBy(i => i.Published is null)
            .ThenByDescending(i => i.Published)
            .ToList();

        var result = new List<NewsItem>();
        foreach (var hour in sorted.GroupBy(i => i.Published is { } p ? p.UtcTicks / TimeSpan.TicksPerHour : (long?)null))
        {
            var queues = hour.GroupBy(i => (i.Source, i.Lang)).Select(g => new Queue<NewsItem>(g)).ToList();
            while (queues.Any(q => q.Count > 0))
                foreach (var q in queues)
                    if (q.Count > 0) result.Add(q.Dequeue());
        }

        if (result.Count > MaxMerged) result = result.GetRange(0, MaxMerged);

        return new NewsSnapshot(result, snaps.Max(s => s.FetchedAt), snaps.All(s => s.FromCache));
    }
}
