using System.Text.RegularExpressions;

namespace Phosphor.Data;

/// <summary>
/// Фильтр «спокойных» новостей: отсекает заголовки о войне и насилии.
/// Совпадение по началу слова ("удар" режет "удару", но не "обудар"); точные слова — только там, где префикс даёт мусор.
/// </summary>
public static class CalmFilter
{
    private static readonly Regex WordRx = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    // Точные слова: короткие аббревиатуры, которые как префикс цепляют мирные слова ("сво" → "свобода").
    private static readonly HashSet<string> ExactWords = new(StringComparer.Ordinal)
    {
        "сво", "всу", "пво", "бои", "war", "wars",
    };

    // Основы по началу слова, рус + англ.
    private static readonly string[] PrefixStems =
    {
        "бпла", "беспилот", "дрон", "удар", "обстрел", "ракетн", "взрыв", "погиб", "ранен", "убит", "убийств",
        "атак", "войн", "фронт", "мобилиз", "теракт", "артилл", "сбит", "сбил", "боев", "пострадавш",
        "drone", "strike", "missile", "killed", "killing", "attack", "shelling", "troops", "bomb", "explosion",
        "airstrike", "casualt", "invasion", "ceasefire", "frontline", "wounded", "injured", "wartime", "warfare",
        "warzone", "artillery", "terror",
    };

    /// <summary>
    /// true, если заголовок содержит слово из встроенного списка или из extraStems (их тоже считаем основами по началу слова).
    /// </summary>
    public static bool IsLoud(string title, IReadOnlyList<string>? extraStems = null)
    {
        var lower = title.ToLowerInvariant();
        var hasAir = false;
        var hasAlarm = false;
        foreach (Match m in WordRx.Matches(lower))
        {
            var word = m.Value;
            if (ExactWords.Contains(word)) return true;
            foreach (var stem in PrefixStems)
                if (word.StartsWith(stem, StringComparison.Ordinal)) return true;
            if (extraStems is not null)
                foreach (var stem in extraStems)
                    if (word.StartsWith(stem, StringComparison.Ordinal)) return true;
            // «воздушная тревога» режем только в паре: одно слово «тревога» мирное («тревога за детей»).
            if (word.StartsWith("воздушн", StringComparison.Ordinal)) hasAir = true;
            if (word.StartsWith("тревог", StringComparison.Ordinal)) hasAlarm = true;
        }
        return hasAir && hasAlarm;
    }
}
