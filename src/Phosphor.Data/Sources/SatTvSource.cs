using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Phosphor.Core;

namespace Phosphor.Data;

/// <summary>
/// Список ТВ-каналов одного спутника с KingOfSat (ключ "sattv", страница en.kingofsat.info/sat-&lt;id&gt;).
/// Строки транспондера и каналов в таблице перемешаны: канал относится к ближайшему транспондеру выше.
/// Только ТВ — канал с VPID (радио и data VPID не имеют). FTA — колонка Encryption пуста или начинается с «Clear».
/// Один экземпляр на спутник; раз в сутки. Слияние спутников — SatTvCatalog.Merge.
/// </summary>
public sealed class SatTvSource : IDataSource
{
    private const int MaxChannels = 400;
    private static readonly TimeSpan Refresh = TimeSpan.FromHours(24);

    // id страницы KingOfSat: slug в нижнем регистре ("astra1p", "hb13g", "e36wa").
    private static readonly Regex IdRe = new("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant);
    // Строка транспондера (tp) или канала (ch); у канала без id пустой data-channel-id.
    private static readonly Regex RowRe = new(
        "<tr data-frequency-id=\"\\d+\" class=\"frequency-row\">(?<tp>.*?)</tr>|<tr data-channel-id=\"\\d*\">(?<ch>.*?)</tr>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex CellRe = new("<td[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex TagRe = new("<[^>]+>", RegexOptions.CultureInvariant);
    private static readonly Regex SpaceRe = new("\\s+", RegexOptions.CultureInvariant);
    private static readonly Regex FreqRe = new("^(\\d+(?:\\.\\d+)?)\\s+([HVLR])$", RegexOptions.CultureInvariant);
    private static readonly Regex TitleRe = new("<title>\\s*([^<(]+?)\\s*\\(([^()<]+)\\)", RegexOptions.CultureInvariant);

    // Номера колонок в строках таблицы KingOfSat (нулевой — колонка с логотипом/иконкой).
    private const int TpFreq = 2, TpStandard = 5, TpSymbolRate = 7, TpFec = 8;
    private const int ChName = 1, ChCountry = 2, ChEncryption = 5, ChVpid = 7;

    private readonly string _id;

    private SatTvSource(string id) => _id = id;

    /// <summary>Источник по id страницы KingOfSat; null — id не похож на slug страницы.</summary>
    public static SatTvSource? Create(string id) => IdRe.IsMatch(id) ? new SatTvSource(id) : null;

    public string Key => "sattv";
    public string Name => "sattv." + _id;
    public TimeSpan Interval => Refresh;

    public async Task<DataSnapshot?> FetchAsync(HttpClient http, CancellationToken ct)
    {
        var bytes = await http.GetByteArrayAsync("https://en.kingofsat.info/sat-" + _id, ct).ConfigureAwait(false);
        var sat = Parse(Encoding.UTF8.GetString(bytes), _id, DateTimeOffset.Now);
        // Пустой разбор — ошибка загрузки: хаб оставит прежний кеш и повторит позже.
        if (sat.Channels.Count == 0) throw new InvalidOperationException($"sattv.{_id}: на странице нет ТВ-каналов");
        return sat;
    }

    /// <summary>Разбор страницы спутника. Битый или пустой HTML — спутник без каналов, без исключений.</summary>
    public static SatTvSatellite Parse(string html, string id, DateTimeOffset at)
    {
        var (name, position) = TitleOf(html, id);
        var channels = new List<SatTvChannel>();
        double freq = 0;
        char pol = '\0';
        int symbolRate = 0;
        string fec = "", standard = "";
        bool haveTp = false;

        foreach (Match m in RowRe.Matches(html))
        {
            if (m.Groups["tp"].Success)
            {
                var c = Cells(m.Groups["tp"].Value);
                var fm = c.Count > TpFec ? FreqRe.Match(c[TpFreq]) : Match.Empty;
                haveTp = fm.Success;
                if (!haveTp) continue;
                freq = double.Parse(fm.Groups[1].Value, CultureInfo.InvariantCulture);
                pol = fm.Groups[2].Value[0];
                symbolRate = int.TryParse(c[TpSymbolRate], NumberStyles.None, CultureInfo.InvariantCulture, out var sr) ? sr : 0;
                fec = c[TpFec] is "None" ? "" : c[TpFec];
                standard = c[TpStandard];
                continue;
            }

            // Канал без транспондера выше (битая разметка) — пропускаем.
            if (!haveTp) continue;
            var cells = Cells(m.Groups["ch"].Value);
            if (cells.Count <= ChVpid) continue;
            var chName = cells[ChName];
            // Радио и data — без VPID (пустая ячейка или &nbsp;).
            var vpid = cells[ChVpid];
            if (chName.Length == 0 || vpid.Length == 0 || !vpid.All(char.IsAsciiDigit)) continue;

            var enc = cells[ChEncryption];
            bool fta = enc.Length == 0 || enc == "Clear" || enc.StartsWith("Clear ", StringComparison.Ordinal);
            channels.Add(new SatTvChannel(chName, freq, pol, symbolRate, fec, standard, cells[ChCountry], fta));
        }

        // Порядок стабилен: частота, при равной — открытые первыми, затем имя.
        var list = channels
            .OrderBy(c => c.FreqMhz)
            .ThenByDescending(c => c.Fta)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Take(MaxChannels)
            .ToList();
        return new SatTvSatellite(id, name, position, list, at, false);
    }

    /// <summary>Ячейки строки: очищены от тегов и сущностей, пробелы схлопнуты.</summary>
    private static List<string> Cells(string row)
    {
        var list = new List<string>();
        foreach (Match c in CellRe.Matches(row)) list.Add(Clean(c.Groups[1].Value));
        return list;
    }

    private static string Clean(string html) =>
        SpaceRe.Replace(WebUtility.HtmlDecode(TagRe.Replace(html, " ")), " ").Trim();

    /// <summary>Имя и позиция из заголовка: «Yamal 601 (49.0&amp;deg;E) - ...» → "Yamal 601", "49.0°E".</summary>
    private static (string Name, string Position) TitleOf(string html, string id)
    {
        var m = TitleRe.Match(html);
        if (!m.Success) return (id, "");
        var name = Clean(m.Groups[1].Value);
        var position = Clean(m.Groups[2].Value);
        return (name.Length > 0 ? name : id, position);
    }
}
