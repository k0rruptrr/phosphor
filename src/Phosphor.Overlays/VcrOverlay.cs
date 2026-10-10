using Phosphor.Core;

namespace Phosphor.Overlays;

/// <summary>
/// OSD видика 90-х поверх сцены: PLAY, часы, счётчик ленты S[====  ]E, AUTO TRACKING, REW в конце ленты.
/// Показ по времени как у настоящего видика (см. VcrState): PLAY/часы/счётчик — несколько секунд после включения,
/// потом гаснут; AUTO TRACKING — пока усталость держится; REW — в конце ленты.
/// Крупный режим (cols >= 60 и rows >= 12): пиксельный шрифт 5×7 из секстантов (PixelFont), шкала — блоками █.
/// Мелкий режим (меньше): обычный текст в клетках. Плашек нет: Bg не трогается, контур рисует OsdLayer.
/// Рисует только символами из GlyphSet, поэтому проходит через фильтры вместе со сценой.
/// Дирижёр включает его только для пресетов со стадией vhs (см. OverlayConfig.VcrFor).
/// </summary>
public sealed class VcrOverlay : IOverlay, IOverlayParams
{
    public string Id => "vcr";

    /// <summary>Постоянный показ (превью в окне и снимки): PLAY без мигания, часы и счётчик видны всё время.</summary>
    public bool Demo { get; set; }

    // Параметры overlay.params.vcr (см. Configure). size и stroke читает хост, здесь не применяются.
    private static readonly SceneParamSpec[] ParamSpecs =
    {
        new("style", SceneParamKind.Choice, 0f, 0f, 1f, "стиль букв (pixel — 5×7, text — шрифт)", new[] { "pixel", "text" }),
        new("size", SceneParamKind.Float, 1f, 0.5f, 2f, "размер"),
        new("stroke", SceneParamKind.Float, 2f, 0f, 4f, "обводка, px"),
        new("color", SceneParamKind.Choice, 0f, 0f, 3f, "цвет букв", new[] { "white", "green", "cyan", "yellow" }),
        new("showSec", SceneParamKind.Float, 4f, 2f, 15f, "показ после PLAY, с"),
        new("clock", SceneParamKind.Bool, 1f, 0f, 1f, "часы"),
        new("counter", SceneParamKind.Bool, 1f, 0f, 1f, "счётчик ленты"),
    };

    private static readonly Rgba DefaultFg = Rgba.White;

    public IReadOnlyList<SceneParamSpec> Params => ParamSpecs;

    private Rgba _fg = DefaultFg;
    private bool _text;        // style=text: всегда мелкий режим
    private bool _clock = true;
    private bool _counter = true;

    /// <summary>Применяет overlay.params.vcr. Вызывается хостом до Init; без вызова — значения по умолчанию.</summary>
    public void Configure(SceneOptions options)
    {
        _text = options.GetString("style", "pixel") == "text";
        _fg = options.GetString("color", "white") switch
        {
            "green" => Rgba.Hex(0x3CFF3C),
            "cyan" => Rgba.Hex(0x3CF0FF),
            "yellow" => Rgba.Hex(0xFFE83C),
            _ => DefaultFg,
        };
        _state.ShowLength = Math.Clamp(options.GetFloat("showSec", 4f), 2f, 15f);
        _clock = options.GetBool("clock", true);
        _counter = options.GetBool("counter", true);
    }

    // Константы OSD
    private const int Margin = 2;                    // отступ от краёв, в клетках
    private const int BarWide = 20;                  // ширина шкалы (cols >= 40, в крупном режиме — меньше при узкой сетке)
    private const int BigMinCols = 60;               // крупный шрифт — от этой ширины
    private const int BigMinRows = 12;
    private const int BigBarOverhead = 51;           // клетки под надписи крупной строки счётчика (кроме шкалы)
    private const int BufLen = 64;

    private static readonly bool HasPlayGlyph = GlyphSet.Contains('►');
    private static readonly bool HasRewGlyph = GlyphSet.Contains('◄');
    private const string PlayBig = "PLAY ►";
    private const string RewBig = "REW ◄◄";
    private static readonly string PlaySmall = HasPlayGlyph ? "PLAY ►" : "PLAY >";
    private static readonly string RewSmall = HasRewGlyph ? "REW ◄◄" : "REW <<";
    private const string AutoText = "AUTO TRACKING";

    private readonly VcrState _state = new();
    private readonly char[] _buf = new char[BufLen];
    private int _bn;

    public VcrState State => _state;

    public void Init(int cols, int rows, Random rng)
    {
        _state.SetOn(false);
    }

    /// <summary>Видик включён/выключен (сцена с vhs). Включение запускает показ.</summary>
    public void SetOn(bool on) => _state.SetOn(on);

    /// <summary>Есть ли что рисовать в этом кадре. Если нет — Apply не вызывают, слой не рисуется вовсе.</summary>
    public bool IsVisible(float fatigue) => Demo || _state.ShowOsd || _state.AutoTracking(fatigue);

    public void Update(float dt) => _state.Update(dt);

    public void Apply(CharGrid grid, in OverlayContext ctx)
    {
        bool show = Demo || _state.ShowOsd;
        bool auto = _state.AutoTracking(ctx.Organism.Fatigue);
        if (!show && !auto) return;

        // Сетка OSD своя: очищаем, рисуем только текущие надписи
        grid.Clear(Rgba.Black);

        int cols = grid.Cols, rows = grid.Rows;
        bool big = !_text && cols >= BigMinCols && rows >= BigMinRows;
        int adv = big ? PixelFont.Advance : 1;          // шаг символа, клеток
        int h = big ? PixelFont.CellsH : 1;             // высота строки текста, клеток
        bool rew = _state.Rew;
        double pos = _state.Pos;                        // позиция ленты в проходе, с
        int barY = rows - 1 - Margin - (h - 1);         // нижняя строка OSD — на 2 клетки выше нижнего края

        if (!show)
        {
            // Только AUTO TRACKING по центру верхней строки
            AutoTrack(grid, Margin, cols - Margin, adv, big);
            return;
        }

        int leftEnd = Margin;                           // правый край надписи слева (для AUTO TRACKING)
        // Левый верх: REW после конца ленты, иначе PLAY (мигает первые секунды показа)
        if (rew || Demo || _state.PlayVisible)
        {
            string s = rew ? (big ? RewBig : RewSmall) : (big ? PlayBig : PlaySmall);
            Emit(grid, Margin, Margin, s, big);
            leftEnd = Margin + s.Length * adv;
        }

        // Правый верх: SP и часы HH:MM (двоеточие мигает раз в секунду)
        _bn = 0;
        Append("SP");
        if (_clock)
        {
            Append(" ");
            if (ctx.Clock is DateTime clk)
            {
                AppendNum(clk.Hour, 2);
                _buf[_bn++] = _state.ColonVisible ? ':' : ' ';
                AppendNum(clk.Minute, 2);
            }
            else Append("--:--");
        }
        int cw = _bn * adv;
        int cx = cols - Margin - cw;
        Emit(grid, cx, Margin, _buf.AsSpan(0, _bn), big);
        if (auto) AutoTrack(grid, leftEnd, cx - 2, adv, big);

        // counter=false: шкала и счётчик не рисуются
        if (!_counter) return;

        // Счётчик ленты: S[====    ]E 0:12:34
        int inner = big
            ? Math.Min(BarWide, cols - BigBarOverhead)
            : (cols >= 40 ? BarWide : Math.Max(0, cols / 2 - 6));
        // Во время REW шкала быстро уезжает влево к пустой; иначе — доля ленты (180 мин)
        double frac = rew
            ? Math.Max(0, 1 - (VcrState.RewSec - _state.RewLeft) / VcrState.RewQuick)
            : pos / VcrState.TapeSec;
        int filled = (int)Math.Round(frac * inner);

        int x = Margin;
        x += Emit(grid, x, barY, "S[", big);
        int barX = x;
        for (int i = 0; i < inner; i++)
        {
            if (big)
            {
                // Шкала — блоки █ высотой 2 строки; остальные клетки строки — пустые
                for (int dy = 0; dy < h; dy++) grid.Put(barX + i, barY + dy, ' ', _fg);
                if (i < filled)
                {
                    grid.Put(barX + i, barY, '█', _fg);
                    grid.Put(barX + i, barY + 1, '█', _fg);
                }
            }
            else grid.Put(barX + i, barY, i < filled ? '=' : ' ', _fg);
        }
        x += inner;

        _bn = 0;
        Append("]E ");
        int secs = (int)pos;
        AppendNum(secs / 3600, 1);
        _buf[_bn++] = ':';
        AppendNum(secs / 60 % 60, 2);
        _buf[_bn++] = ':';
        AppendNum(secs % 60, 2);
        Emit(grid, x, barY, _buf.AsSpan(0, _bn), big);
    }

    /// <summary>
    /// AUTO TRACKING по центру верхней строки в полосе [left, right) клеток.
    /// Если места нет — не рисуем, чтобы не слипаться с соседними надписями.
    /// </summary>
    private void AutoTrack(CharGrid grid, int left, int right, int adv, bool big)
    {
        int aw = AutoText.Length * adv;
        int lo = left + 2;
        int hi = right;
        if (hi - lo < aw) return;
        int ax = lo + (hi - lo - aw) / 2;
        Emit(grid, ax, Margin, AutoText, big);
    }

    private void Append(string s)
    {
        for (int i = 0; i < s.Length && _bn < BufLen; i++) _buf[_bn++] = s[i];
    }

    private void AppendNum(int v, int digits)
    {
        for (int d = digits - 1; d >= 0; d--)
        {
            if (_bn >= BufLen) return;
            int p = 1;
            for (int k = 0; k < d; k++) p *= 10;
            _buf[_bn++] = (char)('0' + v / p % 10);
        }
    }

    /// <summary>
    /// Выводит строку с (x, y). Крупно — пиксельным шрифтом (3×3 клетки на символ, шаг 4, интервал пустой),
    /// мелко — по одной клетке на символ. Возвращает сдвиг курсора в клетках. Что за границей — обрезается.
    /// </summary>
    private int Emit(CharGrid grid, int x, int y, ReadOnlySpan<char> s, bool big)
    {
        int start = x;
        foreach (char c in s)
        {
            if (!big)
            {
                grid.Put(x, y, c, _fg);
                x++;
                continue;
            }
            var bits = PixelFont.Bits(c);
            for (int cy = 0; cy < PixelFont.CellsH; cy++)
                for (int cx = 0; cx < PixelFont.CellsW; cx++)
                {
                    int b = bits[cy * PixelFont.CellsW + cx];
                    grid.Put(x + cx, y + cy, b == 0 ? ' ' : GlyphSet.Sextant(b), _fg);
                }
            // межбуквенный интервал — пустой столбец клеток, чтобы сцена не просвечивала между буквами
            for (int cy = 0; cy < PixelFont.CellsH; cy++) grid.Put(x + PixelFont.CellsW, y + cy, ' ', _fg);
            x += PixelFont.Advance;
        }
        return x - start;
    }
}
