using Phosphor.Core;

namespace Phosphor.Overlays;

/// <summary>
/// Бирка «логотип» монитора (IBadge "logo"). source: phosphor — встроенный PNG (logo\phosphor.png рядом с exe),
/// file — свой PNG (если файла нет — текст «PHOSPHOR» и FileMissing = true), text — текст шрифтом сетки.
/// Текстовый режим: ImagePath == null, Rows = 1, Cols = длина текста (до 40 символов). Символы вне GlyphSet — '?'.
/// Логотип не меняется: Version постоянна (0), хост перерисовывает его один раз.
/// </summary>
public sealed class LogoBadge : IBadge, IOverlayParams
{
    public string Id => "logo";

    private const string DefaultText = "PHOSPHOR";
    private const int MaxText = 40;

    private static readonly string[] Sources = { "phosphor", "file", "text", "greyprism" };
    private static readonly string[] Positions = { "tl", "tc", "tr", "ml", "mc", "mr", "bl", "bc", "br" };
    private static readonly string[] Colors = { "white", "green", "amber", "cyan" };
    private static readonly string[] Layers = { "over", "under" };

    // Параметры overlay.params.logo (см. Configure). file, text, font — строки вне Params.
    private static readonly SceneParamSpec[] ParamSpecs =
    {
        new("source", SceneParamKind.Choice, 3f, 0f, 3f, "источник", Sources),
        new("position", SceneParamKind.Choice, 2f, 0f, 8f, "позиция", Positions),
        new("size", SceneParamKind.Float, 0.08f, 0.02f, 0.3f, "размер"),
        new("margin", SceneParamKind.Float, 0.03f, 0f, 0.1f, "отступ от края"),
        new("opacity", SceneParamKind.Float, 0.5f, 0f, 1f, "непрозрачность (0 — не рисуется)"),
        new("color", SceneParamKind.Choice, 0f, 0f, 3f, "цвет текста", Colors),
        new("stroke", SceneParamKind.Float, 1.5f, 0f, 4f, "обводка, px"),
        new("layer", SceneParamKind.Choice, 1f, 0f, 1f, "слой (поверх / под эффектами)", Layers),
        new("drift", SceneParamKind.Bool, 1f, 0f, 1f, "защита от выгорания"),
        new("plate", SceneParamKind.Choice, 0f, 0f, 2f, "подложка", BadgeEffects.Plates),
        new("plateColor", SceneParamKind.Choice, 0f, 0f, 2f, "цвет подложки", BadgeEffects.PlateColors),
        new("plateOpacity", SceneParamKind.Float, 0.5f, 0f, 1f, "прозрачность подложки"),
        new("shadow", SceneParamKind.Float, 0f, 0f, 1f, "тень"),
        new("glow", SceneParamKind.Float, 0f, 0f, 1f, "свечение"),
        new("silhouette", SceneParamKind.Bool, 0f, 0f, 1f, "силуэт (одним цветом)"),
    };

    private char[] _text = DefaultText.ToCharArray();   // текст текстового режима (только глифы GlyphSet)
    private Rgba _fg = Rgba.Hex(0xFFFFFF);

    public IReadOnlyList<SceneParamSpec> Params => ParamSpecs;

    public int Cols { get; private set; } = 1;
    public int Rows { get; private set; } = 1;
    public string? ImagePath { get; private set; }
    /// <summary>source=file, а файла нет: показан текст «PHOSPHOR». Хост пишет одно предупреждение в лог.</summary>
    public bool FileMissing { get; private set; }
    public BadgePlacement Placement { get; private set; }
    /// <summary>Подложка, тень, свечение, силуэт (хост читает через badge is LogoBadge l ? l.Effects).</summary>
    public BadgeEffects Effects { get; private set; }
    public long Version => 0;

    public LogoBadge()
    {
        Configure(new SceneOptions());
    }

    /// <summary>Применяет overlay.params.logo и text/file/font. Вызывается хостом до первого использования; без вызова — значения по умолчанию.</summary>
    public void Configure(SceneOptions options)
    {
        int src = ChoiceIndex(options, "source", Sources, 3);
        int pos = ChoiceIndex(options, "position", Positions, 2);
        bool over = ChoiceIndex(options, "layer", Layers, 1) == 0;
        _fg = ColorOf(options.GetString("color", "white"));

        ImagePath = null;
        FileMissing = false;
        switch (src)
        {
            case 0:
                ImagePath = Path.Combine(AppContext.BaseDirectory, "logo", "phosphor.png");
                break;
            case 1:
                string file = options.GetString("file", "");
                if (file.Length > 0 && File.Exists(file)) ImagePath = file;
                else FileMissing = true;
                break;
            case 3:
                ImagePath = Path.Combine(AppContext.BaseDirectory, "logo", "greyprism.png");
                break;
        }

        if (ImagePath != null)
        {
            Cols = 1;   // картиночная бирка: Cols/Rows хост не использует
            Rows = 1;
        }
        else
        {
            SetText(FileMissing ? DefaultText : options.GetString("text", DefaultText));
        }

        Placement = new BadgePlacement(
            Positions[pos],
            Math.Clamp(options.GetFloat("size", 0.08f), 0.02f, 0.3f),
            Math.Clamp(options.GetFloat("margin", 0.03f), 0f, 0.1f),
            Math.Clamp(options.GetFloat("opacity", 0.5f), 0f, 1f),
            over,
            options.GetBool("drift", true),
            Math.Clamp(options.GetFloat("stroke", 1.5f), 0f, 4f),
            options.GetString("font", ""));
        Effects = BadgeEffects.Read(options, _fg);
    }

    public void Update(float dt, in OverlayContext ctx)
    {
        // логотип статичен
    }

    public void Draw(CharGrid grid)
    {
        grid.Clear(Rgba.Black);
        if (ImagePath != null) return;
        for (int i = 0; i < _text.Length; i++) grid.Put(i, 0, _text[i], _fg);
    }

    /// <summary>Текст текстового режима: пусто → «PHOSPHOR», длина до 40, символы вне GlyphSet → '?'.</summary>
    private void SetText(string s)
    {
        if (string.IsNullOrEmpty(s)) s = DefaultText;
        int n = Math.Min(s.Length, MaxText);
        _text = new char[n];
        for (int i = 0; i < n; i++) _text[i] = GlyphSet.Contains(s[i]) ? s[i] : '?';
        Cols = n;
        Rows = 1;
    }

    private static int ChoiceIndex(SceneOptions o, string name, string[] options, int def)
    {
        int i = Array.IndexOf(options, o.GetString(name, options[def]));
        return i < 0 ? def : i;
    }

    private static Rgba ColorOf(string name) => name switch
    {
        "green" => Rgba.Hex(0x3CFF3C),
        "amber" => Rgba.Hex(0xFFB000),
        "cyan" => Rgba.Hex(0x3CF0FF),
        _ => Rgba.Hex(0xFFFFFF),
    };
}
