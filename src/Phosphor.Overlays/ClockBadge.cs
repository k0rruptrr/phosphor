using Phosphor.Core;

namespace Phosphor.Overlays;

/// <summary>
/// Бирка «часы» монитора (IBadge "clock"): время в углу экрана, видно на всех сценах.
/// Форматы: ЧЧ:ММ, ЧЧ:ММ:СС, ДД.ММ ЧЧ:ММ. Стиль pixel — пиксельный шрифт 5×7 (PixelFont), text — символы шрифта.
/// Двоеточие гаснет во второй половине каждой секунды (blink). Без времени (ctx.Clock == null) — прочерки.
/// Version меняется только тогда, когда строка на экране изменилась. Защита от выгорания — drift в BadgePlacement.
/// </summary>
public sealed class ClockBadge : IBadge, IOverlayParams
{
    public string Id => "clock";

    private static readonly string[] Formats = { "hm", "hms", "dhm" };
    // Шаблоны: d/M/H/m/s — цифры поля (две одинаковые подряд), ':', '.', ' ' — как есть
    private static readonly string[] Patterns = { "HH:mm", "HH:mm:ss", "dd.MM HH:mm" };
    private static readonly string[] Styles = { "pixel", "text" };
    private static readonly string[] Positions = { "tl", "tc", "tr", "ml", "mc", "mr", "bl", "bc", "br" };
    private static readonly string[] Colors = { "white", "green", "amber", "cyan" };
    private static readonly string[] Layers = { "over", "under" };

    // Параметры overlay.params.clock (см. Configure). Font — не здесь, читается в Configure.
    private static readonly SceneParamSpec[] ParamSpecs =
    {
        new("format", SceneParamKind.Choice, 0f, 0f, 2f, "формат (ЧЧ:ММ / ЧЧ:ММ:СС / ДД.ММ ЧЧ:ММ)", Formats),
        new("style", SceneParamKind.Choice, 0f, 0f, 1f, "стиль букв (pixel 5×7 / шрифт)", Styles),
        new("position", SceneParamKind.Choice, 8f, 0f, 8f, "позиция", Positions),
        new("size", SceneParamKind.Float, 0.06f, 0.02f, 0.3f, "размер"),
        new("margin", SceneParamKind.Float, 0.03f, 0f, 0.1f, "отступ от края"),
        new("opacity", SceneParamKind.Float, 0.6f, 0.1f, 1f, "непрозрачность"),
        new("color", SceneParamKind.Choice, 0f, 0f, 3f, "цвет", Colors),
        new("stroke", SceneParamKind.Float, 1.5f, 0f, 4f, "обводка, px"),
        new("layer", SceneParamKind.Choice, 0f, 0f, 1f, "слой (поверх / под эффектами)", Layers),
        new("drift", SceneParamKind.Bool, 1f, 0f, 1f, "защита от выгорания"),
        new("blink", SceneParamKind.Bool, 1f, 0f, 1f, "мигание двоеточия"),
    };

    private const int BufLen = 16;                  // длиннейший шаблон — 11 символов

    private readonly char[] _buf = new char[BufLen];   // текущая строка
    private readonly char[] _last = new char[BufLen];  // строка, которую видели в прошлый раз (для Version)
    private int _len;
    private int _fmt;
    private bool _pixel;
    private bool _blink = true;
    private Rgba _fg = Rgba.Hex(0xFFFFFF);
    private float _phase;                            // фаза секунды 0..1: двоеточие горит в первой половине

    public IReadOnlyList<SceneParamSpec> Params => ParamSpecs;

    public int Cols { get; private set; } = 1;
    public int Rows { get; private set; } = 1;
    public string? ImagePath => null;
    public BadgePlacement Placement { get; private set; }
    public long Version { get; private set; }

    public ClockBadge()
    {
        Configure(new SceneOptions());
    }

    /// <summary>Применяет overlay.params.clock и font. Вызывается хостом до первого использования; без вызова — значения по умолчанию.</summary>
    public void Configure(SceneOptions options)
    {
        _fmt = ChoiceIndex(options, "format", Formats, 0);
        _pixel = ChoiceIndex(options, "style", Styles, 0) == 0;
        int pos = ChoiceIndex(options, "position", Positions, 8);
        bool over = ChoiceIndex(options, "layer", Layers, 0) == 0;
        _blink = options.GetBool("blink", true);
        _fg = ColorOf(options.GetString("color", "white"));

        Placement = new BadgePlacement(
            Positions[pos],
            Math.Clamp(options.GetFloat("size", 0.06f), 0.02f, 0.3f),
            Math.Clamp(options.GetFloat("margin", 0.03f), 0f, 0.1f),
            Math.Clamp(options.GetFloat("opacity", 0.6f), 0.1f, 1f),
            over,
            options.GetBool("drift", true),
            Math.Clamp(options.GetFloat("stroke", 1.5f), 0f, 4f),
            options.GetString("font", ""));

        // Ширина под формат фиксирована: от длины шаблона, не от времени
        _len = Patterns[_fmt].Length;
        Cols = _pixel ? _len * PixelFont.Advance : _len;
        Rows = _pixel ? PixelFont.CellsH : 1;

        _phase = 0f;
        Array.Fill(_last, '\0');
        Render(null);
    }

    public void Update(float dt, in OverlayContext ctx)
    {
        _phase += dt;
        _phase -= MathF.Floor(_phase);               // держим фазу в [0, 1)
        Render(ctx.Clock);
        if (!_buf.AsSpan(0, _len).SequenceEqual(_last.AsSpan(0, _len)))
        {
            _buf.AsSpan(0, _len).CopyTo(_last);
            Version++;
        }
    }

    public void Draw(CharGrid grid)
    {
        grid.Clear(Rgba.Black);
        if (_pixel)
        {
            for (int i = 0; i < _len; i++)
            {
                var bits = PixelFont.Bits(_buf[i]);
                int x0 = i * PixelFont.Advance;
                for (int cy = 0; cy < PixelFont.CellsH; cy++)
                    for (int cx = 0; cx < PixelFont.CellsW; cx++)
                    {
                        int b = bits[cy * PixelFont.CellsW + cx];
                        grid.Put(x0 + cx, cy, b == 0 ? ' ' : GlyphSet.Sextant(b), _fg);
                    }
            }
        }
        else
        {
            for (int i = 0; i < _len; i++) grid.Put(i, 0, _buf[i], _fg);
        }
    }

    /// <summary>Заполняет _buf по шаблону текущего формата. Без времени — прочерки; двоеточие гаснет во второй половине секунды.</summary>
    private void Render(DateTime? clk)
    {
        string p = Patterns[_fmt];
        bool colonOn = !_blink || _phase < 0.5f;
        for (int i = 0; i < _len; i++)
        {
            char pc = p[i];
            char ch;
            if (pc == ':') ch = colonOn ? ':' : ' ';
            else if (pc == '.' || pc == ' ') ch = pc;
            else if (clk is DateTime t)
            {
                int v = FieldValue(t, pc);
                // пара одинаковых букв: первая — десятки, вторая — единицы
                int digit = i > 0 && p[i - 1] == pc ? v % 10 : v / 10 % 10;
                ch = (char)('0' + digit);
            }
            else ch = '-';
            _buf[i] = ch;
        }
    }

    private static int FieldValue(DateTime t, char field) => field switch
    {
        'd' => t.Day,
        'M' => t.Month,
        'H' => t.Hour,
        'm' => t.Minute,
        _ => t.Second,
    };

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
