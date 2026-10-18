using Phosphor.Core;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Phosphor.Scenes.Tests")]

namespace Phosphor.Scenes;

/// <summary>
/// Орреий: схема Солнечной системы сверху, как в старом астрономическом софте. В центре Солнце с круглым ореолом,
/// орбиты Меркурия–Нептуна тонким пунктиром из секстелей (радиусы в шкале sqrt, круги с учётом CellAspect), периоды настоящие,
/// у Земли Луна, у Сатурна кольцо. Каждая планета тянет тусклый след. Раз в минуту пролетает комета.
/// Масштаб дышит между внутренними планетами и всей системой; скобка отмечает выбранную планету, рамка внизу — её данные.
/// Звук: удар — вспышка Солнца (ореол больше), громкость слегка ускоряет время.
/// </summary>
public sealed class OrreryScene : IScene, IAudioReactive, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec DaysSpec = new("daysPerSec", SceneParamKind.Float, 30f, 1f, 365f, "дней модели в секунду");
    public static readonly SceneParamSpec LabelsSpec = new("labels", SceneParamKind.Bool, 1f, 0f, 1f, "подписи планет");
    public static readonly SceneParamSpec ZoomSpec = new("zoom", SceneParamKind.Choice, 0f, 0f, 2f, "масштаб: авто, внутренние, все",
        new[] { "auto", "inner", "outer" });
    private static readonly SceneParamSpec[] ParamList = { DaysSpec, LabelsSpec, ZoomSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    public string Id => "orrery";

    /// <summary>Glow — сила подсветки фона клетки планеты (0 — нет); Ring — кольцо по бокам (Сатурн).</summary>
    private readonly record struct Body(string Name, float Au, float Period, uint Color, char Glyph, float Glow, bool Ring);

    private static readonly Body[] Bodies =
    {
        new("MERCURY", 0.387f, 88f, 0xB0B0B0, '•', 0.08f, false),
        new("VENUS", 0.723f, 225f, 0xF2D28B, '●', 0.12f, false),
        new("EARTH", 1.000f, 365f, 0x4DA6FF, '●', 0.12f, false),
        new("MARS", 1.524f, 687f, 0xFF6A3D, '●', 0.12f, false),
        new("JUPITER", 5.203f, 4333f, 0xE8B98A, '●', 0.30f, false),
        new("SATURN", 9.537f, 10759f, 0xD9C58A, '●', 0.30f, true),
        new("URANUS", 19.19f, 30687f, 0x8FE3E8, '●', 0.15f, false),
        new("NEPTUNE", 30.07f, 60190f, 0x5A7CFF, '●', 0.15f, false),
    };

    private const int EarthIdx = 2;
    private const int JupiterIdx = 4, SaturnIdx = 5;
    private const float SunAu = 30.07f;            // самая далёкая орбита — полный радиус схемы
    private const float ScaleFill = 0.46f;         // полный радиус = 46% меньшей стороны экрана
    private const float InnerZoom = 2f;            // зум на внутренние планеты
    private const float ZoomPeriod = 80f;          // полный цикл авто-зума, с (40 с внутри, 40 с снаружи)
    private const float CometPass = 30f;           // длительность пролёта кометы, с
    private const float SelPeriod = 15f;          // смена выбранной планеты, с
    private const float MoonPeriodDays = 27.32f;
    private const float MoonRadius = 2.6f;         // радиус орбиты Луны, ячеек (схема, не в масштабе)
    private const float HaloR = 2.6f;              // радиус ореола Солнца, ячеек
    private const float OrbitStep = 1.25f;         // шаг точек орбиты, ширин ячейки (≈2.5 субпикселя)
    private const float TrailSpan = 15f * MathF.PI / 180f;   // длина следа планеты, рад (≈15°)
    private const int TrailSteps = 8;
    private const int BoxW = 24, BoxH = 5, BoxX = 3;   // карточка выбранной планеты, отступ 3 колонки
    private const int HudMargin = 2;

    internal static readonly Rgba OrbitColor = Rgba.Hex(0x2F4A4D);      // тусклый серо-зелёный, ~25%
    internal static readonly Rgba OrbitHi = Rgba.Hex(0x4C7478);         // орбита выбранной планеты, ~40%
    private static readonly Rgba SunColor = Rgba.Hex(0xFFE070);
    private static readonly Rgba HaloColor = Rgba.Hex(0xFFA000);
    private static readonly Rgba MoonColor = Rgba.Hex(0x9A9A9A);
    private static readonly Rgba CometColor = Rgba.Hex(0xE6FBFF);
    private static readonly Rgba TailColor = Rgba.Hex(0x6FC8DA);
    private static readonly Rgba HudColor = Rgba.Hex(0xB8E8C0);
    private static readonly Rgba BoxColor = Rgba.Hex(0x4A6A5E);
    private static readonly int[] MonthLen = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
    private static readonly int[] LabelPriority = { EarthIdx, JupiterIdx, SaturnIdx };

    private int _cols, _rows;
    private float _speed = 1f;
    private float _aspect = 1.8f;                  // высота / ширина ячейки
    private float _daysPerSec = 30f;
    private bool _labels = true;
    private int _zoomMode;                         // 0 auto, 1 inner, 2 outer
    private Random _rng = new(1);

    private readonly double[] _phase = new double[Bodies.Length];   // фаза на эпохе, рад
    private readonly double[] _theta = new double[Bodies.Length];   // текущий угол, рад
    private readonly float[] _px = new float[Bodies.Length];        // положение, ширины ячейки от центра
    private readonly float[] _py = new float[Bodies.Length];
    private readonly int[] _cx = new int[Bodies.Length];            // ячейка планеты
    private readonly int[] _cy = new int[Bodies.Length];

    private double _days;          // дни модели с эпохи 2000-01-01
    private double _time;          // сим-секунды
    private float _loud, _tLoud, _bass, _tBass, _flash;
    private int _sel;
    private float _selTimer;
    private float _cometLeft;
    private bool _cometOn;
    private float _cometT;
    private double _cometOmega, _cometA, _cometE;
    private float _hx, _hy;        // голова кометы, координаты ячеек
    private float _rmax;           // полный радиус схемы в ширинах ячейки
    private float _zoom = 1f;
    private float _ox, _oy;        // центр схемы в координатах ячеек (Солнце)

    private char[] _ch = Array.Empty<char>();
    private Rgba[] _fg = Array.Empty<Rgba>();
    private Rgba[] _bg = Array.Empty<Rgba>();
    private int[] _bits = Array.Empty<int>();      // секстели по ячейкам
    private Rgba[] _bitFg = Array.Empty<Rgba>();
    private bool[] _occ = Array.Empty<bool>();     // занятость для подписей
    private bool[] _hud = Array.Empty<bool>();     // ячейки под текстом и рамкой: орбиты туда не рисуем

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = cols;
        _rows = rows;
        _rng = rng;
        _speed = MathF.Max(0f, options.Speed);
        _aspect = MathF.Max(0.5f, options.CellAspect);
        _daysPerSec = Math.Clamp(options.GetFloat(DaysSpec.Name, DaysSpec.Default), DaysSpec.Min, DaysSpec.Max);
        _labels = options.GetBool(LabelsSpec.Name, LabelsSpec.Default >= 0.5f);
        string zoom = options.GetString(ZoomSpec.Name, ZoomSpec.Options![(int)ZoomSpec.Default]);
        _zoomMode = Math.Max(0, Array.IndexOf(ZoomSpec.Options!, zoom));

        int n = cols * rows;
        _ch = new char[n];
        Array.Fill(_ch, ' ');
        _fg = new Rgba[n];
        _bg = new Rgba[n];
        _bits = new int[n];
        _bitFg = new Rgba[n];
        _occ = new bool[n];
        _hud = new bool[n];

        _ox = cols / 2 + 0.5f;
        _oy = rows / 2 + 0.5f;

        for (int i = 0; i < Bodies.Length; i++) _phase[i] = rng.NextDouble() * Math.Tau;
        _days = 0;
        _time = 0;
        _loud = _tLoud = _bass = _tBass = _flash = 0f;
        _sel = 0;
        _selTimer = SelPeriod;
        _cometOn = false;
        _cometLeft = 20f + 40f * (float)rng.NextDouble();
        _cometT = 0f;
        _cometOmega = _cometA = _cometE = 0;
        _hx = _hy = 0f;
        _rmax = 0f;
        _zoom = 1f;
    }

    public void OnAudio(in AudioFrame audio)
    {
        _tLoud = audio.Active ? audio.Level : 0f;
        _tBass = audio.Active ? audio.Bass : 0f;
        if (audio.Active && audio.Beat) _flash = 1f;
    }

    public void Update(float dt)
    {
        dt = Math.Clamp(dt, 0f, 0.1f);
        float k = 1f - MathF.Exp(-dt * 6f);
        _loud += (_tLoud - _loud) * k;
        _bass += (_tBass - _bass) * k;
        _flash *= MathF.Exp(-dt / 0.15f);

        // Время — только сумма dt. Громкость слегка ускоряет модель (без звука множитель равен 1).
        float st = dt * _speed;
        _time += st;
        _days += st * _daysPerSec * (1f + 0.3f * _loud);

        _selTimer -= st;
        if (_selTimer <= 0f)
        {
            _selTimer += SelPeriod;
            _sel = (_sel + 1) % Bodies.Length;
        }

        if (_cometOn)
        {
            _cometT += st / CometPass;
            if (_cometT >= 1f)
            {
                _cometOn = false;
                _cometLeft = 30f + 30f * (float)_rng.NextDouble();
            }
        }
        else
        {
            _cometLeft -= st;
            if (_cometLeft <= 0f) StartComet();
        }

        int w = _cols, h = _rows;
        float zf = _zoomMode switch
        {
            0 => Smooth(0.5f + 0.5f * MathF.Cos(MathF.Tau * (float)_time / ZoomPeriod)),
            1 => 1f,
            _ => 0f,
        };
        _zoom = 1f + (InnerZoom - 1f) * zf;
        _rmax = ScaleFill * MathF.Min(w, h * _aspect) * _zoom;

        // Положения планет: радиус в шкале sqrt, угол по периоду.
        for (int i = 0; i < Bodies.Length; i++)
        {
            var b = Bodies[i];
            double th = _phase[i] + Math.Tau * _days / b.Period;
            _theta[i] = th;
            float r = _rmax * MathF.Sqrt(b.Au / SunAu);
            _px[i] = r * (float)Math.Cos(th);
            _py[i] = r * (float)Math.Sin(th);
        }

        // Зона HUD: под текстом и рамкой орбиты и кометы не рисуются.
        MarkHud(w, h);

        // Ореол Солнца: дизеринг точками секстелей по физическому расстоянию. Круглый по CellAspect, без блоков фона.
        float glowAmp = 0.42f * (1f + 0.4f * _bass) + 0.5f * _flash;
        float haloSigma = 1f + 0.6f * _flash;
        float sig2 = 2f * haloSigma * haloSigma;
        int reach = (int)MathF.Ceiling(HaloR * 2.6f) + 1;
        int scx = (int)MathF.Floor(_ox), scy = (int)MathF.Floor(_oy);
        for (int cy = Math.Max(0, scy - reach); cy <= Math.Min(h - 1, scy + reach); cy++)
        for (int cx = Math.Max(0, scx - reach); cx <= Math.Min(w - 1, scx + reach); cx++)
        for (int dy = 0; dy < 3; dy++)
        for (int dx = 0; dx < 2; dx++)
        {
            float fx = cx + (dx + 0.5f) / 2f, fy = cy + (dy + 0.5f) / 3f;
            float px = fx - _ox, py = (fy - _oy) * _aspect;
            float v = MathF.Min(1f, glowAmp * MathF.Exp(-(px * px + py * py) / (HaloR * HaloR) / sig2));
            if (v < 0.03f) continue;
            if (v <= BayerThreshold(cx * 2 + dx, cy * 3 + dy)) continue;
            Plot(fx, fy, HaloColor.Scale(0.45f + 0.55f * v));
        }

        // Орбиты: редкий тонкий пунктир, одна точка через ~2.5 субпикселя.
        for (int i = 0; i < Bodies.Length; i++)
        {
            float r = _rmax * MathF.Sqrt(Bodies[i].Au / SunAu);
            Rgba col = i == _sel ? OrbitHi : OrbitColor;
            int count = Math.Clamp((int)(r * MathF.Tau / OrbitStep), 32, 4000);
            for (int j = 0; j < count; j++)
            {
                float a = j * MathF.Tau / count;
                Plot(_ox + r * MathF.Cos(a), _oy + r * MathF.Sin(a) / _aspect, col);
            }
        }

        // След: короткий затухающий хвост позади каждой планеты (против направления движения).
        for (int i = 0; i < Bodies.Length; i++)
        {
            float r = _rmax * MathF.Sqrt(Bodies[i].Au / SunAu);
            Rgba pc = Rgba.Hex(Bodies[i].Color);
            for (int s = 1; s <= TrailSteps; s++)
            {
                float a = (float)_theta[i] - s * TrailSpan / TrailSteps;
                float alpha = 0.5f * (1f - (float)s / (TrailSteps + 1));
                Plot(_ox + r * MathF.Cos(a), _oy + r * MathF.Sin(a) / _aspect, Rgba.Lerp(Rgba.Black, pc, alpha));
            }
        }

        // Луна вокруг Земли.
        float ex = _ox + _px[EarthIdx], ey = _oy + _py[EarthIdx] / _aspect;
        for (int j = 0; j < 24; j += 2)
        {
            float a = j * MathF.Tau / 24f;
            Plot(ex + MoonRadius * MathF.Cos(a), ey + MoonRadius * MathF.Sin(a) / _aspect, OrbitColor);
        }
        double moonA = Math.Tau * _days / MoonPeriodDays;
        float mx = ex + MoonRadius * (float)Math.Cos(moonA);
        float my = ey + MoonRadius * (float)Math.Sin(moonA) / _aspect;

        // Комета: эллипс с Солнцем в фокусе, хвост от Солнца.
        if (_cometOn)
        {
            double nu = Math.PI * (-0.93 + 1.86 * _cometT);
            double r = _cometA * (1 - _cometE * _cometE) / (1 + _cometE * Math.Cos(nu));
            double phi = nu + _cometOmega;
            float rr = _rmax * (float)Math.Sqrt(r / SunAu);
            float cos = (float)Math.Cos(phi), sin = (float)Math.Sin(phi);
            _hx = _ox + rr * cos;
            _hy = _oy + rr * sin / _aspect;
            float prox = Math.Clamp((float)(1 - r / 3), 0f, 1f);
            float tailLen = 2f + 14f * prox;
            float tdx = cos, tdy = sin / _aspect;
            for (float s = 1f; s <= tailLen; s += 0.6f)
                Plot(_hx + tdx * s, _hy + tdy * s, TailColor);
        }

        // Слой секстелей в клетки. Фон чёрный: свечение только точками.
        int cells = w * h;
        for (int i = 0; i < cells; i++)
        {
            int bits = _bits[i];
            _bits[i] = 0;
            _ch[i] = bits != 0 ? GlyphSet.Sextant(bits) : ' ';
            _fg[i] = _bitFg[i];
            _bg[i] = Rgba.Black;
        }
        Array.Fill(_occ, false);

        // Солнце.
        Put((int)MathF.Floor(_ox), (int)MathF.Floor(_oy), '●', SunColor);

        // Планеты: точка, у крупных — подсветка фона и у Сатурна кольцо.
        for (int i = 0; i < Bodies.Length; i++)
        {
            var b = Bodies[i];
            int cx = (int)MathF.Floor(_ox + _px[i]);
            int cy = (int)MathF.Floor(_oy + _py[i] / _aspect);
            _cx[i] = cx;
            _cy[i] = cy;
            Rgba col = Rgba.Hex(b.Color);
            Put(cx, cy, b.Glyph, col);
            if (b.Glow > 0f && (uint)cx < (uint)w && (uint)cy < (uint)h) _bg[cy * w + cx] = col.Scale(b.Glow);
            if (b.Ring)
            {
                Rgba ring = Rgba.Lerp(Rgba.Black, col, 0.6f);
                Put(cx - 1, cy, '─', ring);
                Put(cx + 1, cy, '─', ring);
            }
        }
        Put((int)MathF.Floor(mx), (int)MathF.Floor(my), '·', MoonColor);

        if (_labels)
        {
            // Приоритет подписей: выбранная, Земля, Юпитер, Сатурн, остальные.
            LabelBody(_sel);
            foreach (int p in LabelPriority) if (p != _sel) LabelBody(p);
            for (int i = 0; i < Bodies.Length; i++)
                if (i != _sel && Array.IndexOf(LabelPriority, i) < 0) LabelBody(i);
        }

        if (_cometOn) Put((int)MathF.Floor(_hx), (int)MathF.Floor(_hy), '*', CometColor);

        DrawHud(w, h);
    }

    /// <summary>Порог упорядоченного дизеринга (матрица Байера 4×4) для субпикселя (sx, sy), 0..1.</summary>
    private static float BayerThreshold(int sx, int sy)
    {
        ReadOnlySpan<byte> m = stackalloc byte[] { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
        return (m[(sy & 3) * 4 + (sx & 3)] + 0.5f) / 16f;
    }

    /// <summary>Отмечает ячейки, где стоит текст HUD и рамка: там не рисуем орбиты, следы и комету.</summary>
    private void MarkHud(int w, int h)
    {
        Array.Fill(_hud, false);
        MaskRect(0, 0, Math.Min(w, 32), 1);
        MaskRect(0, h - 1, Math.Min(w, 32), 1);
        MaskRect(Math.Max(0, w - 18), h - 1, Math.Min(w, 18), 1);
        if (h >= BoxH + BoxX + 2 && w >= BoxX + BoxW) MaskRect(BoxX, BoxBottom(h) - BoxH + 1, BoxW, BoxH);
    }

    private void MaskRect(int x0, int y0, int rw, int rh)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(_rows, y0 + rh); y++)
        for (int x = Math.Max(0, x0); x < Math.Min(_cols, x0 + rw); x++)
            _hud[y * _cols + x] = true;
    }

    /// <summary>Нижняя строка рамки выбранной планеты: над строкой даты с пустой строкой между.</summary>
    private static int BoxBottom(int h) => h - 3;

    private void LabelBody(int i) => Label(_cx[i], _cy[i], Bodies[i].Name, Rgba.Hex(Bodies[i].Color));

    private void DrawHud(int w, int h)
    {
        // Скобка на выбранной планете.
        int cx = _cx[_sel], cy = _cy[_sel];
        Put(cx - 2, cy - 1, '┌', HudColor);
        Put(cx + 2, cy - 1, '┐', HudColor);
        Put(cx - 2, cy + 1, '└', HudColor);
        Put(cx + 2, cy + 1, '┘', HudColor);

        // Верх: название и масштаб.
        int x = Text(HudMargin, 0, "SOL SYSTEM  ZOOM ", HudColor);
        x = Fix(x, 0, _zoom, 1, HudColor);
        Text(x, 0, "X", HudColor);

        // Карточка выбранной планеты в левом нижнем углу: тонкая рамка, три строки.
        if (h >= BoxH + BoxX + 2 && w >= BoxX + BoxW)
        {
            int x0 = BoxX, x1 = BoxX + BoxW - 1, y1 = BoxBottom(h), y0 = y1 - BoxH + 1;
            for (int yy = y0; yy <= y1; yy++)
            for (int xx = x0; xx <= x1; xx++)
            {
                int idx = yy * w + xx;
                _bg[idx] = Rgba.Black;
                _ch[idx] = ' ';
            }
            Put(x0, y0, '┌', BoxColor);
            Put(x1, y0, '┐', BoxColor);
            Put(x0, y1, '└', BoxColor);
            Put(x1, y1, '┘', BoxColor);
            for (int xx = x0 + 1; xx < x1; xx++)
            {
                Put(xx, y0, '─', BoxColor);
                Put(xx, y1, '─', BoxColor);
            }
            for (int yy = y0 + 1; yy < y1; yy++)
            {
                Put(x0, yy, '│', BoxColor);
                Put(x1, yy, '│', BoxColor);
            }

            var b = Bodies[_sel];
            int tx = x0 + 2;
            Text(tx, y0 + 1, b.Name, Rgba.Hex(b.Color));
            int x2 = Text(tx, y0 + 2, "A ", HudColor);
            x2 = Fix(x2, y0 + 2, b.Au, 2, HudColor);
            x2 = Text(x2, y0 + 2, " AU  P ", HudColor);
            x2 = Num(x2, y0 + 2, (long)b.Period, 1, HudColor);
            Text(x2, y0 + 2, " D", HudColor);
            // Строка 3: расстояние до Земли; для Земли — до Луны.
            int x3;
            if (_sel == EarthIdx)
            {
                x3 = Text(tx, y0 + 3, "MOON ", HudColor);
                x3 = Fix(x3, y0 + 3, 0.00257f, 4, HudColor);
                Text(x3, y0 + 3, " AU", HudColor);
            }
            else
            {
                double ax = b.Au * Math.Cos(_theta[_sel]), ay = b.Au * Math.Sin(_theta[_sel]);
                double ex = Math.Cos(_theta[EarthIdx]), ey = Math.Sin(_theta[EarthIdx]);
                float dist = (float)Math.Sqrt((ax - ex) * (ax - ex) + (ay - ey) * (ay - ey));
                x3 = Text(tx, y0 + 3, "DIST ", HudColor);
                x3 = Fix(x3, y0 + 3, dist, 2, HudColor);
                Text(x3, y0 + 3, " AU", HudColor);
            }
        }

        // Низ: дата от 2000-01-01 и скорость.
        if (h >= 2)
        {
            long days = (long)_days;
            x = Text(HudMargin, h - 1, "DATE ", HudColor);
            x = WriteDate(x, h - 1, days, HudColor);
            x = Text(x, h - 1, "  J+", HudColor);
            Num(x, h - 1, days, 1, HudColor);
            // Справа: SPEED n D/S
            long dps = (long)_daysPerSec;
            int len = 6 + Digits(dps) + 4;
            x = Text(w - len - HudMargin, h - 1, "SPEED ", HudColor);
            x = Num(x, h - 1, dps, 1, HudColor);
            Text(x, h - 1, " D/S", HudColor);
        }
    }

    public void Draw(CharGrid grid)
    {
        int n = Math.Min(grid.Cells.Length, _ch.Length);
        for (int i = 0; i < n; i++) grid.Cells[i] = new Cell(_ch[i], _fg[i], _bg[i]);
    }

    private void StartComet()
    {
        _cometOn = true;
        _cometT = 0f;
        _cometOmega = _rng.NextDouble() * Math.Tau;
        _cometA = 8.0 + 8.0 * _rng.NextDouble();
        _cometE = 0.90 + 0.06 * _rng.NextDouble();
    }

    /// <summary>Точка секстеля в координатах ячеек (x — ячейка 0..W, y — ячейка 0..H; 2 × 3 субпикселя на ячейку).
    /// Ячейки HUD пропускаются.</summary>
    private void Plot(float fx, float fy, Rgba color)
    {
        if (fx < 0f || fy < 0f || fx >= _cols || fy >= _rows) return;
        int sx = (int)MathF.Floor(fx * 2f);
        int sy = (int)MathF.Floor(fy * 3f);
        int cx = sx >> 1, cy = sy / 3;
        int idx = cy * _cols + cx;
        if (_hud[idx]) return;
        int bit = (sy % 3) * 2 + (sx & 1);
        _bits[idx] |= 1 << bit;
        _bitFg[idx] = color;
    }

    private void Put(int x, int y, char ch, Rgba fg)
    {
        if ((uint)x >= (uint)_cols || (uint)y >= (uint)_rows) return;
        int i = y * _cols + x;
        _ch[i] = ch;
        _fg[i] = fg;
        _occ[i] = true;
    }

    private bool Free(int x, int y, int len)
    {
        if ((uint)y >= (uint)_rows || x < 0 || x + len > _cols) return false;
        for (int k = 0; k < len; k++)
            if (_occ[y * _cols + x + k]) return false;
        return true;
    }

    /// <summary>Подпись справа от планеты, если не влезает или занято — слева; если и там нельзя — пропуск.</summary>
    private void Label(int cx, int cy, string s, Rgba fg)
    {
        int x0 = cx + 2;
        if (!Free(x0, cy, s.Length)) x0 = cx - 2 - s.Length + 1;
        if (!Free(x0, cy, s.Length)) return;
        Text(x0, cy, s, fg);
    }

    private int Text(int x, int y, string s, Rgba fg)
    {
        for (int i = 0; i < s.Length; i++) Put(x + i, y, s[i], fg);
        return x + s.Length;
    }

    private static int Digits(long v)
    {
        int d = 1;
        v = Math.Abs(v);
        while (v >= 10) { v /= 10; d++; }
        return d;
    }

    /// <summary>Целое, дополненное нулями до width знаков. Возвращает x после последней цифры.</summary>
    private int Num(int x, int y, long v, int width, Rgba fg)
    {
        v = Math.Abs(v);
        int len = Math.Max(width, Digits(v));
        for (int k = len - 1; k >= 0; k--)
        {
            Put(x + k, y, (char)('0' + (int)(v % 10)), fg);
            v /= 10;
        }
        return x + len;
    }

    /// <summary>Число с dec знаками после точки.</summary>
    private int Fix(int x, int y, float v, int dec, Rgba fg)
    {
        long p = 1;
        for (int i = 0; i < dec; i++) p *= 10;
        long s = (long)MathF.Round(MathF.Abs(v) * p);
        x = Num(x, y, s / p, 1, fg);
        Put(x, y, '.', fg);
        return Num(x + 1, y, s % p, dec, fg);
    }

    /// <summary>Дата YYYY-MM-DD от 2000-01-01 + days, без DateTime и строк на кадр.</summary>
    private int WriteDate(int x, int y, long days, Rgba fg)
    {
        long d = days;
        int year = 2000;
        while (true)
        {
            int yl = IsLeap(year) ? 366 : 365;
            if (d < yl) break;
            d -= yl;
            year++;
        }
        int month = 0;
        while (true)
        {
            int ml = month == 1 && IsLeap(year) ? 29 : MonthLen[month];
            if (d < ml) break;
            d -= ml;
            month++;
        }
        x = Num(x, y, year, 4, fg);
        x = Text(x, y, "-", fg);
        x = Num(x, y, month + 1, 2, fg);
        x = Text(x, y, "-", fg);
        return Num(x, y, d + 1, 2, fg);
    }

    private static bool IsLeap(int y) => (y % 4 == 0 && y % 100 != 0) || y % 400 == 0;

    private static float Smooth(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Тест: угол Земли в градусах (без приведения по модулю).</summary>
    internal double EarthAngleDeg => _theta[EarthIdx] * 180.0 / Math.PI;

    /// <summary>Тест: полный радиус схемы в ширинах ячейки на текущем кадре.</summary>
    internal float FullRadius => _rmax;
}
