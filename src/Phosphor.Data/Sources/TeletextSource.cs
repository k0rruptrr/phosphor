using System.Collections.Concurrent;
using System.Drawing;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Phosphor.Core;

namespace Phosphor.Data;

/// <summary>
/// Телетекст голландского NOS и шведского SVT Text: страницы → нормализованная сетка 40×25 (TeletextPage).
/// Палитра — классическая 0..7 (чёрный, красный, зелёный, жёлтый, синий, пурпурный, голубой, белый).
/// Мозаика NOS — сущности &#xF0NN;. Мозаика SVT — картинки 13×16 px (GIF) одного символа, разбираются в 2×3 секстели.
/// </summary>
public sealed class TeletextSource : IDataSource
{
    private const int Rows = 25;
    private const int Cols = 40;
    private const byte White = 7;
    private const byte Black = 0;

    private static readonly int[] NosPages = { 100, 101, 102, 103, 104, 105, 106, 110, 601, 702 };
    private static readonly int[] SvtPages = { 100, 101, 102, 103, 104, 300, 400 };

    private const int MaxNewGifsPerUpdate = 150;
    private static readonly TimeSpan GifPause = TimeSpan.FromMilliseconds(100);

    private readonly string _channel;
    private readonly int[] _pages;

    /// <summary>Пауза между запросами страниц канала. По умолчанию 1.5 с; тесты ставят ноль.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(1500);

    private TeletextSource(string channel, int[] pages)
    {
        _channel = channel;
        _pages = pages;
    }

    /// <summary>"nos" | "svt"; для других каналов — null.</summary>
    public static TeletextSource? Create(string channel) => channel switch
    {
        "nos" => new TeletextSource("nos", NosPages),
        "svt" => new TeletextSource("svt", SvtPages),
        _ => null,
    };

    public string Key => "teletext";
    public string Name => "teletext." + _channel;
    public TimeSpan Interval => TimeSpan.FromMinutes(10);

    public async Task<DataSnapshot?> FetchAsync(HttpClient http, CancellationToken ct)
    {
        var pages = new List<TeletextPage>();
        int newGifs = 0;
        bool first = true;
        foreach (var page in _pages)
        {
            if (!first) await Task.Delay(Delay, ct);
            first = false;
            try
            {
                var json = await http.GetStringAsync(PageUrl(page), ct);
                if (_channel == "nos")
                {
                    pages.Add(ParseNos(json, page));
                }
                else
                {
                    newGifs = await PrefetchGifsAsync(http, json, newGifs, ct);
                    pages.Add(ParseSvt(json, page, GifBitsFromCache));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Неудачная страница пропускается, остальные всё равно нужны.
            }
        }

        if (pages.Count == 0)
            throw new InvalidOperationException($"teletext.{_channel}: ни одна страница не загружена");

        return new TeletextSnapshot(pages, DateTimeOffset.Now, false);
    }

    private string PageUrl(int page) => _channel == "nos"
        ? "https://teletekst-data.nos.nl/json/" + page
        : "https://api.texttv.nu/api/get/" + page + "?app=phosphor";

    // ───────────────────────── NOS ─────────────────────────

    /// <summary>Разбор JSON NOS: поле content — HTML, строки разделены '\n'.</summary>
    public static TeletextPage ParseNos(string json, int page)
    {
        using var doc = JsonDocument.Parse(json);
        var html = doc.RootElement.GetProperty("content").GetString() ?? "";
        var grid = new Grid();
        var lines = html.Split('\n');
        for (int r = 0; r < Rows && r < lines.Length; r++)
            ParseNosRow(lines[r], grid, r);
        return grid.ToPage("NOS", "nl", page);
    }

    private static void ParseNosRow(string html, Grid grid, int row)
    {
        var stack = new Stack<Style>();
        stack.Push(DefaultStyle);
        int col = 0;
        foreach (var t in Tokenize(html))
        {
            switch (t.Kind)
            {
                case TokKind.Open when t.Name is "span" or "a":
                {
                    var own = NosColours(ClassesOf(t.Attr), out bool any);
                    // span без цветов — по умолчанию (белый на чёрном); a без цветов — наследует текущий стиль.
                    stack.Push(any ? own : t.Name == "a" ? stack.Peek() : DefaultStyle);
                    break;
                }
                case TokKind.Close when t.Name is "span" or "a":
                    if (stack.Count > 1) stack.Pop();
                    break;
                case TokKind.Text:
                    foreach (var (ch, mosaic) in NosCells(t.Text))
                    {
                        var s = stack.Peek();
                        grid.Put(row, col++, ch, s.Fg, s.Bg, mosaic);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Текст строки NOS → ячейки. &#xF0NN; (NN 20..7F): 0x20..0x3F и 0x60..0x7F — мозаика,
    /// 0x40..0x5F — обычный символ. Прочий текст — WebUtility.HtmlDecode.
    /// </summary>
    private static IEnumerable<(char Ch, byte Mosaic)> NosCells(string raw)
    {
        int i = 0;
        while (i < raw.Length)
        {
            var m = MosaicEntityRe.Match(raw, i);
            if (m.Success && m.Index == i)
            {
                int nn = Convert.ToInt32(m.Groups[1].Value, 16);
                if ((nn >= 0x20 && nn <= 0x3F) || (nn >= 0x60 && nn <= 0x7F))
                    yield return (' ', (byte)((nn & 0x1F) | ((nn & 0x40) >> 1)));
                else if (nn >= 0x40 && nn <= 0x5F)
                    yield return ((char)nn, 0);
                else
                    yield return (' ', 0);
                i += m.Length;
                continue;
            }

            int end = m.Success ? m.Index : raw.Length;
            foreach (var ch in WebUtility.HtmlDecode(raw[i..end]))
                yield return (ch, 0);
            i = end;
        }
    }

    private static Style NosColours(HashSet<string> cls, out bool any)
    {
        byte? fg = null, bg = null;
        foreach (var c in cls)
        {
            if (NosColors.TryGetValue(c, out var f)) fg = f;
            else if (c.StartsWith("bg-", StringComparison.Ordinal) && NosColors.TryGetValue(c[3..], out var b)) bg = b;
        }
        any = fg is not null || bg is not null;
        return new Style(fg ?? White, bg ?? Black);
    }

    private static readonly Dictionary<string, byte> NosColors = new()
    {
        ["black"] = 0, ["red"] = 1, ["green"] = 2, ["yellow"] = 3,
        ["blue"] = 4, ["magenta"] = 5, ["cyan"] = 6, ["white"] = 7,
    };

    // ───────────────────────── SVT ─────────────────────────

    /// <summary>
    /// Разбор JSON texttv.nu: [0].content — массив HTML-строк; внутри span class="line" на каждую строку экрана.
    /// Классы фона bgXX, символа XX. Коды: Bl=чёрный, R, G, Y, B, M, C, W (bl как символ — тоже чёрный).
    /// class DH — строка с двойной высотой: растягивается ×2 по вертикали на строки k и k+1
    /// (текст — символ в k, пробел в k+1; мозаика — через StretchSextant). Строка k+1 в HTML — пустой placeholder.
    /// bgImg — мозаика: style=url(.../chars/&lt;id&gt;.gif) — одна ячейка, биты берутся у gifToBits(url);
    /// null (неизвестный GIF) — пустая ячейка с фоном.
    /// </summary>
    public static TeletextPage ParseSvt(string json, int page, Func<string, int?> gifToBits)
    {
        var html = SvtHtml(json);
        var grid = new Grid();
        var dh = new bool[Rows];
        var stack = new Stack<(Style S, bool Img)>();
        int row = -1, col = 0;

        foreach (var t in Tokenize(html))
        {
            if (t.Kind == TokKind.Open && t.Name == "span")
            {
                var cls = ClassesOf(t.Attr);
                if (cls.Contains("line"))
                {
                    row++;
                    col = 0;
                    if (row < Rows && cls.Contains("DH")) dh[row] = true;
                }

                var style = SvtStyle(cls);
                bool img = cls.Contains("bgImg");
                if (img && row >= 0 && row < Rows)
                {
                    var url = UrlOf(t.Attr);
                    int? bits = url is null ? null : gifToBits(url);
                    grid.Put(row, col, ' ', style.Fg, style.Bg, bits is int b ? (byte)(b & 63) : (byte)0);
                    col++;
                }
                stack.Push((style, img));
            }
            else if (t.Kind == TokKind.Close && t.Name == "span")
            {
                if (stack.Count > 0) stack.Pop();
            }
            else if (t.Kind == TokKind.Text && row >= 0 && row < Rows && stack.Count > 0 && !stack.Peek().Img)
            {
                var s = stack.Peek().S;
                foreach (var ch in WebUtility.HtmlDecode(t.Text))
                    grid.Put(row, col++, ch, s.Fg, s.Bg, 0);
            }
        }

        for (int r = 0; r < Rows - 1; r++)
            if (dh[r]) grid.StretchRow(r);

        return grid.ToPage("SVT", "sv", page);
    }

    /// <summary>
    /// Растяжение мозаичной ячейки ×2 по вертикали (DH). Ряды r0 (биты 0,1), r1 (2,3), r2 (4,5).
    /// Верхняя ячейка = ряды [r0, r0, r1], нижняя = [r1, r2, r2]; каждый ряд — 2 бита.
    /// </summary>
    public static (byte top, byte bottom) StretchSextant(byte bits)
    {
        int r0 = bits & 3, r1 = (bits >> 2) & 3, r2 = (bits >> 4) & 3;
        int top = r0 | (r0 << 2) | (r1 << 4);
        int bottom = r1 | (r2 << 2) | (r2 << 4);
        return ((byte)top, (byte)bottom);
    }

    private static string SvtHtml(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            throw new InvalidDataException("texttv: пустой ответ");
        var content = root[0].GetProperty("content");
        var sb = new StringBuilder();
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
                sb.Append(part.GetString()).Append('\n');
        }
        else
        {
            sb.Append(content.GetString());
        }
        return sb.ToString();
    }

    private static Style SvtStyle(HashSet<string> cls)
    {
        byte fg = White, bg = Black;
        foreach (var c in cls)
        {
            if (c.StartsWith("bg", StringComparison.Ordinal))
            {
                if (SvtBg.TryGetValue(c[2..], out var b)) bg = b;
            }
            else if (SvtFg.TryGetValue(c, out var f))
            {
                fg = f;
            }
        }
        return new Style(fg, bg);
    }

    private static readonly Dictionary<string, byte> SvtBg = new()
    {
        ["Bl"] = 0, ["R"] = 1, ["G"] = 2, ["Y"] = 3, ["B"] = 4, ["M"] = 5, ["C"] = 6, ["W"] = 7,
    };

    private static readonly Dictionary<string, byte> SvtFg = new()
    {
        ["bl"] = 0, ["Bl"] = 0, ["R"] = 1, ["G"] = 2, ["Y"] = 3, ["B"] = 4, ["M"] = 5, ["C"] = 6, ["W"] = 7,
    };

    private static string? UrlOf(string attr)
    {
        var m = UrlRe.Match(attr);
        return m.Success ? m.Groups[1].Value : null;
    }

    // ───────────────────────── GIF мозаики SVT ─────────────────────────

    private static readonly ConcurrentDictionary<string, int> GifBitsCache = new();
    private static readonly Regex GifIdRe = new(@"/chars/(\d+)\.gif", RegexOptions.Compiled);

    private static string GifCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Phosphor", "cache", "ttgif");

    private static string GifUrl(string id) => "https://l.texttv.nu/storage/chars/" + id + ".gif";

    /// <summary>Биты мозаики по URL GIF из памяти или с диска; null — пока неизвестно.</summary>
    private static int? GifBitsFromCache(string url)
    {
        var m = GifIdRe.Match(url);
        if (!m.Success) return null;
        var id = m.Groups[1].Value;
        if (GifBitsCache.TryGetValue(id, out var mem)) return mem;
        try
        {
            var path = Path.Combine(GifCacheDir, id + ".bits");
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var disk) && disk is >= 0 and <= 63)
            {
                GifBitsCache[id] = disk;
                return disk;
            }
        }
        catch (Exception)
        {
            // Битый или недоступный кеш — просто считаем, что GIF неизвестен.
        }
        return null;
    }

    /// <summary>Скачать GIF, которых ещё нет в кеше (не больше 150 за обновление, пауза 100 мс).</summary>
    private static async Task<int> PrefetchGifsAsync(HttpClient http, string json, int newSoFar, CancellationToken ct)
    {
        var ids = GifIdRe.Matches(SvtHtml(json)).Select(m => m.Groups[1].Value).Distinct();
        foreach (var id in ids)
        {
            var url = GifUrl(id);
            if (GifBitsFromCache(url) is not null) continue;
            if (newSoFar >= MaxNewGifsPerUpdate) break;
            newSoFar++;
            try
            {
                await Task.Delay(GifPause, ct);
                var bytes = await http.GetByteArrayAsync(url, ct);
                if (DecodeGifBits(bytes) is int bits)
                {
                    GifBitsCache[id] = bits;
                    SaveGifBits(id, bits);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // GIF не скачан — ячейка пустая, повторим в следующем обновлении.
            }
        }
        return newSoFar;
    }

    private static void SaveGifBits(string id, int bits)
    {
        try
        {
            Directory.CreateDirectory(GifCacheDir);
            File.WriteAllText(Path.Combine(GifCacheDir, id + ".bits"), bits.ToString());
        }
        catch (Exception)
        {
            // Кеш на диске — оптимизация; без него просто скачаем снова.
        }
    }

    /// <summary>
    /// GIF одного символа → 6 бит секстелей. Фон — самый частый цвет. Область 2×3 «включена», если
    /// больше 50% её пикселей отличаются от фона. Бит r*2+c: 0 — верх-лево, 1 — верх-право, 2 — середина-лево, ...
    /// Для тестов и для PrefetchGifsAsync. null — картинку не удалось открыть.
    /// </summary>
    public static int? DecodeGifBits(byte[] gif)
    {
        try
        {
            using var ms = new MemoryStream(gif);
            using var bmp = new Bitmap(ms);
            int w = bmp.Width, h = bmp.Height;
            if (w < 2 || h < 3) return null;

            var px = new int[w * h];
            var counts = new Dictionary<int, int>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int argb = bmp.GetPixel(x, y).ToArgb();
                    px[y * w + x] = argb;
                    counts[argb] = counts.GetValueOrDefault(argb) + 1;
                }
            int bg = counts.MaxBy(kv => kv.Value).Key;

            int bits = 0;
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 2; c++)
                {
                    int x0 = c * w / 2, x1 = (c + 1) * w / 2;
                    int y0 = r * h / 3, y1 = (r + 1) * h / 3;
                    int total = 0, lit = 0;
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++)
                        {
                            total++;
                            if (px[y * w + x] != bg) lit++;
                        }
                    if (total > 0 && lit * 2 > total) bits |= 1 << (r * 2 + c);
                }
            return bits;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // ───────────────────────── Общее ─────────────────────────

    private readonly record struct Style(byte Fg, byte Bg);

    private static readonly Style DefaultStyle = new(White, Black);

    private static readonly Regex TagRe = new(@"<(/?)([a-zA-Z][a-zA-Z0-9]*)([^>]*)>", RegexOptions.Compiled);
    private static readonly Regex ClassRe = new(@"\bclass\s*=\s*""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex UrlRe = new(@"url\(\s*['""]?([^'"")\s]+)", RegexOptions.Compiled);
    private static readonly Regex MosaicEntityRe = new(@"&#[xX][Ff]0([0-9A-Fa-f]{2});", RegexOptions.Compiled);

    private enum TokKind { Open, Close, Text }

    private readonly record struct Tok(TokKind Kind, string Name, string Attr, string Text);

    private static IEnumerable<Tok> Tokenize(string html)
    {
        int pos = 0;
        foreach (Match m in TagRe.Matches(html))
        {
            if (m.Index > pos) yield return new Tok(TokKind.Text, "", "", html[pos..m.Index]);
            bool close = m.Groups[1].Length > 0;
            yield return new Tok(close ? TokKind.Close : TokKind.Open, m.Groups[2].Value.ToLowerInvariant(), m.Groups[3].Value, "");
            pos = m.Index + m.Length;
        }
        if (pos < html.Length) yield return new Tok(TokKind.Text, "", "", html[pos..]);
    }

    private static HashSet<string> ClassesOf(string attr)
    {
        var m = ClassRe.Match(attr);
        return m.Success
            ? new HashSet<string>(m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            : new HashSet<string>();
    }

    /// <summary>Символ → допустимый для рендерера (GlyphSet). NBSP — пробел, прочее — Sanitize.</summary>
    private static char SafeChar(char c)
    {
        if (c == ' ') return ' ';
        if (GlyphSet.Contains(c)) return c;
        var s = GlyphSet.Sanitize(c.ToString());
        return s.Length == 1 ? s[0] : '?';
    }

    private sealed class Grid
    {
        private readonly char[] _chars = Enumerable.Repeat(' ', Rows * Cols).ToArray();
        private readonly byte[] _fg = Enumerable.Repeat(White, Rows * Cols).ToArray();
        private readonly byte[] _bg = new byte[Rows * Cols];
        private readonly byte[] _mosaic = new byte[Rows * Cols];

        public void Put(int row, int col, char ch, byte fg, byte bg, byte mosaic)
        {
            if ((uint)row >= Rows || (uint)col >= Cols) return;
            int i = row * Cols + col;
            _chars[i] = mosaic != 0 ? ' ' : SafeChar(ch);
            _fg[i] = fg;
            _bg[i] = bg;
            _mosaic[i] = mosaic;
        }

        /// <summary>Растяжение DH-строки k на строки k и k+1: текст остаётся в k (в k+1 — пробел), фон и цвет одинаковые,
        /// мозаика делится через StretchSextant.</summary>
        public void StretchRow(int k)
        {
            int a = k * Cols, b = (k + 1) * Cols;
            for (int c = 0; c < Cols; c++)
            {
                int i = a + c, j = b + c;
                if (_mosaic[i] != 0)
                {
                    var (top, bottom) = StretchSextant(_mosaic[i]);
                    _chars[i] = ' ';
                    _mosaic[i] = top;
                    _chars[j] = ' ';
                    _mosaic[j] = bottom;
                }
                else
                {
                    _chars[j] = ' ';
                    _mosaic[j] = 0;
                }
                _fg[j] = _fg[i];
                _bg[j] = _bg[i];
            }
        }

        public TeletextPage ToPage(string channel, string lang, int page) =>
            new(channel, lang, page, 0, Rows, Cols, _chars, _fg, _bg, _mosaic);
    }
}

/// <summary>Склейка страниц всех каналов телетекста в один снимок.</summary>
public static class TeletextCatalog
{
    public static DataSnapshot? Merge(IReadOnlyList<DataSnapshot> perSource)
    {
        var parts = perSource.OfType<TeletextSnapshot>().ToList();
        if (parts.Count == 0) return null;
        var pages = parts.SelectMany(p => p.Pages).ToList();
        return new TeletextSnapshot(pages, parts.Max(p => p.FetchedAt), parts.All(p => p.FromCache));
    }
}
