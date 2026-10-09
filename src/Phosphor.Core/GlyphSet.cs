namespace Phosphor.Core;

/// <summary>
/// Полный набор символов, которые рендерер ОБЯЗАН уметь рисовать.
/// Сцены используют только символы отсюда; остальное рендерер покажет как '?'.
/// </summary>
public static class GlyphSet
{
    /// <summary>ASCII 0x20..0x7E.</summary>
    public static readonly string Ascii = Range(0x20, 0x7E);

    /// <summary>Latin-1: À..ÿ (кроме × ÷ — они в Misc) + £ € ¿ ¡ « » — для телетекста и европейских новостей.</summary>
    public static readonly string Latin1 = Range(0xC0, 0xD6) + Range(0xD8, 0xF6) + Range(0xF8, 0xFF) + "£€¿¡«»";

    /// <summary>Кириллица А..я + Ё ё.</summary>
    public static readonly string Cyrillic = Range(0x0410, 0x044F) + "Ёё";

    /// <summary>Полуширинная катакана U+FF66..U+FF9D (для матрицы). В Consolas её нет — берётся из MS Gothic.</summary>
    public static readonly string Katakana = Range(0xFF66, 0xFF9D);

    /// <summary>Полуширинные знаки озвончения ﾞ ﾟ (U+FF9E, U+FF9F). Отдельно: сами по себе выглядят как кавычки.</summary>
    public static readonly string KatakanaMarks = Range(0xFF9E, 0xFF9F);

    /// <summary>
    /// Псевдографика. Рисуется ПРОЦЕДУРНО, не из шрифта, чтобы линии стыковались между ячейками без щелей.
    /// </summary>
    public const string Box =
        "─│┌┐└┘├┤┬┴┼" +          // одинарные
        "═║╔╗╚╝╠╣╦╩╬" +          // двойные
        "╒╓╕╖╘╙╛╜╞╟╡╢╤╥╧╨╪╫" +   // смешанные одинарные/двойные
        "╭╮╯╰" +                 // скруглённые
        "░▒▓█▀▄▌▐■▪";            // блоки и штриховки

    /// <summary>Прочие символы (из шрифта, с фолбэком): точки, стрелки, масти, ноты.</summary>
    public const string Misc = "·•°○●◦◘◙♥♦♣♠♪♫☼►◄▲▼←↑→↓≈∙×÷±§¤";

    /// <summary>
    /// Секстели телетекста: U+E000 + bits (0..63), область частного использования. Рисуются ПРОЦЕДУРНО:
    /// ячейка делится на 2 колонки × 3 ряда; бит 0 — верх-лево, 1 — верх-право, 2 — середина-лево,
    /// 3 — середина-право, 4 — низ-лево, 5 — низ-право. U+E000 — пусто, U+E03F — вся ячейка.
    /// </summary>
    public static readonly string Sextants = Range(0xE000, 0xE03F);
    public static char Sextant(int bits) => (char)(0xE000 + (bits & 63));
    public static bool IsSextant(char c) => c >= (char)0xE000 && c <= (char)0xE03F;

    public static readonly string All = Ascii + Latin1 + Cyrillic + Katakana + KatakanaMarks + Box + Misc + Sextants;

    /// <summary>Привести произвольный текст (заголовки из сети) к рисуемому: известные символы как есть,
    /// типографские кавычки/тире/многоточие — к ASCII, остальное — '?'. Для сцен с внешними данными.</summary>
    public static string Sanitize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (Contains(c)) { sb.Append(c); continue; }
            sb.Append(c switch
            {
                '‘' or '’' or '‚' or '′' => '\'',
                '“' or '”' or '„' => '"',
                '–' or '—' or '−' => '-',
                '…' => '.',
                ' ' or '\t' or '\n' or '\r' => ' ',
                _ => '?',
            });
        }
        return sb.ToString();
    }

    private static readonly HashSet<char> _all = new(All);
    private static readonly HashSet<char> _box = new(Box);
    public static bool Contains(char c) => _all.Contains(c);
    public static bool IsBox(char c) => _box.Contains(c);

    private static string Range(int a, int b)
    {
        var chars = new char[b - a + 1];
        for (int i = a; i <= b; i++) chars[i - a] = (char)i;
        return new string(chars);
    }
}
