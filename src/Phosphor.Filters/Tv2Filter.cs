using Phosphor.Gfx;

namespace Phosphor.Filters;

/// <summary>
/// PAL / SECAM «по вкусу»: не точная физика, а узнаваемый вид. Шейдер написал пользователю Claude Opus в другом чате
/// (генератор таблицы заменён входной картинкой). Факелы SECAM от перерегулирования каузального ФНЧ, «рыбки» ЧМ-шума,
/// PAL-D/PAL-S и «жалюзи», cross-color и dot crawl, прокрутка кадра с видимым гашением (бутылки, испытательные строки ГОСТ 18471, телетекст).
/// Один проход. Пресеты pal2 и secam2.
/// </summary>
public sealed class Tv2Filter : ShaderFilter
{
    private static readonly IReadOnlyList<ParamSpec> _params = new ParamSpec[]
    {
        new ParamSpec("mode", 0f, 0f, 1f, new[] { "secam", "pal" }, "Система цветности", "система цвета"),
        new ParamSpec("amount", 0.8f, 0f, 1f, null, "Сила артефактов: полоса цветности, факелы, перекрёстные искажения", "сила эффекта"),
        new ParamSpec("noise", 0.15f, 0f, 1f, null, "Помехи приёма: «рыбки» SECAM, шум цветности PAL, снег", "помехи приёма"),
        new ParamSpec("fault", 0f, 0f, 1f, null, "Неисправность (> 0.5): SECAM — перепутано опознавание Db/Dr; PAL — без линии задержки, «жалюзи»", "неисправность"),
        new ParamSpec("roll", 0f, 0f, 312f, null, "Прокрутка кадра в строках: видно кадровое гашение (бутылки SECAM, испытательные строки, телетекст)", "прокрутка кадра"),
        new ParamSpec("blanking", 1f, 0f, 1f, null, "Показывать кадровое гашение (КГИ): 24 строки сверху с телетекстом; 0 — картинка на весь кадр", "КГИ (гашение)"),
        new ParamSpec("bottles", 1f, 0f, 1f, null, "SECAM: сигналы опознавания («бутылки») в КГИ; 0 — выкл", "бутылки"),
        new ParamSpec("vits", 1f, 0f, 1f, null, "Испытательные строки I–IV в КГИ (ГОСТ 18471, VITS): лесенки, импульсы 2T/20T, пакеты частот; 0 — выкл", "испытательные строки"),
    };

    public override string Id => "tv2";

    protected override string FragmentResource => "Phosphor.Filters.Shaders.tv2.frag";

    public override IReadOnlyList<ParamSpec> Params => _params;
}
