using System.Runtime.CompilerServices;
using System.Text.Json;
using Phosphor.Core;

namespace Phosphor.Filters;

/// <summary>
/// Монохром через плотность символов: яркость ячейки передаётся штриховкой рампы, а не цветом фона.
/// Заливочные ячейки (пробел, блоки штриховки) меняют символ по яркости; полублоки '▀' сохраняют форму
/// ('▀'/'▄' по более светлой половине), текстовые ячейки и секстели — только серый Fg/Bg.
/// Шаг сетки, запускается до GPU-фильтров. Apply не аллоцирует.
/// Горячий цикл целочисленный: яркость 0..255, все преобразования (contrast, gamma, black, рампа, дизеринг,
/// серые цвета) заранее вычислены в таблицах в Configure/конструкторе.
/// </summary>
public sealed class MonoPass : IGridPass
{
    private const string DefaultRamp = " ░▒▓█";
    private const float DefaultContrast = 1f;
    private const float DefaultGamma = 1f;
    private const bool DefaultDither = true;
    private const float DefaultBlack = 0.12f;
    /// <summary>Яркость текстового фона, ниже которой Bg становится чёрным (чтобы тёмная вода не давала плашек).</summary>
    private const float TextBgBlackThreshold = 0.35f;

    /// <summary>Заливочные символы блоков и штриховок (IsFill).</summary>
    private const string FillBlocks = "░▒▓█▀▄▌▐■▪";
    private const char UpperHalf = '▀';
    private const char LowerHalf = '▄';
    /// <summary>Порог разницы верх/низ для полублоков '▀'/'▄' — 0.35 в байтах (0.35*255 = 89.25), сравнение в целых ×100.</summary>
    private const int HalfThresholdX100 = 8925;

    /// <summary>Матрица Байера 4×4.</summary>
    private static readonly int[] Bayer =
    {
         0,  8,  2, 10,
        12,  4, 14,  6,
         3, 11,  1,  9,
        15,  7, 13,  5,
    };

    /// <summary>Заливочные ячейки: пробел и блоки штриховки/полублоки. Секстели сюда не входят (их цвет — как у текста). bool[65536], заполняется один раз.</summary>
    private static readonly bool[] FillTable = BuildFillTable();

    private static readonly Rgba Black = Rgba.Black;

    private string _ramp = DefaultRamp;
    private float _contrast = DefaultContrast;
    private float _gamma = DefaultGamma;
    private bool _dither = DefaultDither;
    private float _black = DefaultBlack;

    // Таблицы (считаются в BuildTables). Индекс заливки: (яркость 0..255 << 4) | позиция Байера 0..15.
    private char[] _fillCh = new char[256 * 16];
    private Rgba[] _fillFg = new Rgba[256 * 16];
    // Серые цвета по яркости 0..255.
    private Rgba[] _textFg = new Rgba[256];
    private Rgba[] _textBg = new Rgba[256];

    public MonoPass() => BuildTables();

    public string Id => "mono";

    public void Configure(JsonElement stage, List<string> warnings)
    {
        _ramp = DefaultRamp;
        _contrast = DefaultContrast;
        _gamma = DefaultGamma;
        _dither = DefaultDither;
        _black = DefaultBlack;

        try
        {
            ParseStage(stage, warnings);
        }
        finally
        {
            BuildTables();
        }
    }

    private void ParseStage(JsonElement stage, List<string> warnings)
    {
        if (stage.ValueKind != JsonValueKind.Object)
        {
            warnings.Add("mono: параметры должны быть объектом, использованы значения по умолчанию");
            return;
        }

        foreach (var prop in stage.EnumerateObject())
        {
            var v = prop.Value;
            switch (prop.Name)
            {
                case "id":
                    break;

                case "ramp":
                    if (v.ValueKind != JsonValueKind.String)
                    {
                        warnings.Add("mono: ramp должен быть строкой, использована рампа по умолчанию");
                        break;
                    }
                    string ramp = v.GetString() ?? string.Empty;
                    if (ramp.Length < 2)
                    {
                        warnings.Add("mono: ramp должен содержать не меньше 2 символов, использована рампа по умолчанию");
                        break;
                    }
                    string? bad = null;
                    foreach (var c in ramp)
                    {
                        if (!GlyphSet.Contains(c)) { bad = $"U+{(int)c:X4}"; break; }
                    }
                    if (bad is not null)
                    {
                        warnings.Add($"mono: символ рампы {bad} не из GlyphSet, использована рампа по умолчанию");
                        break;
                    }
                    _ramp = ramp;
                    break;

                case "contrast":
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out float contrast) && contrast >= 0f && contrast < 1000f)
                        _contrast = contrast;
                    else
                        warnings.Add("mono: contrast должен быть числом от 0 до 1000, использовано 1.0");
                    break;

                case "gamma":
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out float gamma) && gamma > 0f && gamma <= 100f)
                        _gamma = gamma;
                    else
                        warnings.Add("mono: gamma должен быть числом от 0 (не включая) до 100, использовано 1.0");
                    break;

                case "dither":
                    if (v.ValueKind == JsonValueKind.True) _dither = true;
                    else if (v.ValueKind == JsonValueKind.False) _dither = false;
                    else warnings.Add("mono: dither должен быть true или false, использовано true");
                    break;

                case "black":
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out float black) && black >= 0f && black <= 0.9f)
                        _black = black;
                    else
                        warnings.Add("mono: black должен быть числом от 0 до 0.9, использовано 0.12");
                    break;

                default:
                    warnings.Add($"mono: неизвестный параметр '{prop.Name}'");
                    break;
            }
        }
    }

    /// <summary>Считает все таблицы по текущим параметрам. Вызывается из конструктора и Configure, не из Apply.</summary>
    private void BuildTables()
    {
        int n = _ramp.Length;

        // Заливка: яркость l (0..255) → точка чёрного → contrast/gamma → уровень lp (0..255) → символ и Fg для каждой позиции Байера.
        for (int l = 0; l < 256; l++)
        {
            float lf = l / 255f;
            float blackAdj = lf <= _black ? 0f : (lf - _black) / (1f - _black);
            int lp = Round255(Shape(blackAdj));

            for (int k = 0; k < 16; k++)
            {
                int step = FillStep(lp, n, _dither, Bayer[k]);
                int idx = (l << 4) | k;
                _fillCh[idx] = _ramp[step];
                _fillFg[idx] = step == 0 ? Black : Gray(0.35f + 0.65f * lp / 255f);
            }
        }

        for (int v = 0; v < 256; v++)
        {
            float lv = v / 255f;
            float sh = Shape(lv);
            _textFg[v] = Gray(MathF.Max(0.25f, sh));
            _textBg[v] = sh < TextBgBlackThreshold ? Black : Gray(sh);
        }
    }

    /// <summary>Ступень рампы для уровня lp (0..255) и позиции Байера b. Дизеринг — в целых: сравнение доли с порогом (2b+1)/32.</summary>
    private static int FillStep(int lp, int n, bool dither, int b)
    {
        int prod = lp * (n - 1);
        int baseStep = prod / 255;
        int rem = prod - baseStep * 255; // доля в 1/255
        int step;
        if (dither)
            step = baseStep + (32 * rem > 255 * (2 * b + 1) ? 1 : 0);
        else
            step = baseStep + (2 * rem >= 255 ? 1 : 0);
        return step > n - 1 ? n - 1 : step;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Apply(CharGrid grid, IGlyphCoverage coverage)
    {
        int cols = grid.Cols, rows = grid.Rows;
        var cells = grid.Cells;
        var fillTable = FillTable;
        var fillCh = _fillCh;
        var fillFg = _fillFg;
        var textFg = _textFg;
        var textBg = _textBg;

        for (int y = 0; y < rows; y++)
        {
            int rowBase = y * cols;
            int k4 = (y & 3) * 4;
            for (int x = 0; x < cols; x++)
            {
                ref Cell c = ref cells[rowBase + x];
                char ch = c.Ch;
                int lf = (c.Fg.R * 77 + c.Fg.G * 150 + c.Fg.B * 29) >> 8;
                int lb = (c.Bg.R * 77 + c.Bg.G * 150 + c.Bg.B * 29) >> 8;

                if (!fillTable[ch])
                {
                    // Текстовая ячейка или секстель: символ не трогаем, Fg не темнее 0.25.
                    c.Fg = textFg[lf];
                    c.Bg = textBg[lb];
                    continue;
                }

                int k = k4 + (x & 3);
                if (ch == UpperHalf)
                {
                    int diff = lf > lb ? lf - lb : lb - lf;
                    if (diff * 100 > HalfThresholdX100)
                    {
                        // Полублок сохраняет форму: '▀' если светлее верх, '▄' если светлее низ.
                        // Fg — серый по светлой половине, Bg — серый по тёмной (или чёрный по порогу).
                        if (lf > lb)
                        {
                            c.Ch = UpperHalf;
                            c.Fg = textFg[lf];
                            c.Bg = textBg[lb];
                        }
                        else
                        {
                            c.Ch = LowerHalf;
                            c.Fg = textFg[lb];
                            c.Bg = textBg[lf];
                        }
                    }
                    else
                    {
                        int idx = ((lf + lb) >> 1) << 4 | k;
                        c.Ch = fillCh[idx];
                        c.Fg = fillFg[idx];
                        c.Bg = Black;
                    }
                    continue;
                }

                // Обычная заливка: L = фон*(1-cov) + текст*cov; coverage читается только здесь.
                float cov = coverage.Coverage(ch);
                int l = lb + (int)MathF.Round((lf - lb) * cov);
                if (l < 0) l = 0; else if (l > 255) l = 255;
                int fi = (l << 4) | k;
                c.Ch = fillCh[fi];
                c.Fg = fillFg[fi];
                c.Bg = Black;
            }
        }
    }

    private static bool[] BuildFillTable()
    {
        var t = new bool[65536];
        t[' '] = true;
        foreach (var c in FillBlocks) t[c] = true;
        return t;
    }

    /// <summary>Контраст и гамма к яркости 0..1 (только при сборке таблиц).</summary>
    private float Shape(float l)
    {
        float v = (l - 0.5f) * _contrast + 0.5f;
        if (v <= 0f) return 0f;
        if (v >= 1f) return 1f;
        return _gamma == 1f ? v : MathF.Pow(v, _gamma);
    }

    private static int Round255(float v)
    {
        int r = (int)(v * 255f + 0.5f);
        return r < 0 ? 0 : r > 255 ? 255 : r;
    }

    private static Rgba Gray(float v)
    {
        byte b = v <= 0f ? (byte)0 : v >= 1f ? (byte)255 : (byte)(v * 255f + 0.5f);
        return new Rgba(b, b, b, 255);
    }
}
