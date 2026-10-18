using Phosphor.Core;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Phosphor.Scenes.Tests")]

namespace Phosphor.Scenes;

/// <summary>
/// Измеритель дивергенции: ряд из 8 стеклянных газоразрядных ламп с проволочными цифрами и точкой.
/// Горит текущее значение с мерцанием, раз в interval секунд значение сдвигается: лампы перебирают цифры
/// и останавливаются слева направо. Иногда прогоняется профилактика катодов. Бас качает ореол, сильный удар
/// после тишины запускает сдвиг вне очереди.
/// </summary>
public sealed class DivergenceScene : IScene, ISceneParams, IAudioReactive
{
    public string Id => "divergence";

    public IReadOnlyList<SceneParamSpec> Params { get; } = new[]
    {
        new SceneParamSpec("interval", SceneParamKind.Float, 30f, 5f, 120f, "секунд между сдвигами значения"),
        new SceneParamSpec("ghosts", SceneParamKind.Bool, 1f, 0f, 1f, "видны ли призраки цифр в лампах"),
        new SceneParamSpec("glow", SceneParamKind.Float, 0.6f, 0f, 1f, "сила ореола вокруг ламп"),
    };

    private const int Lamps = 8;
    private const int DotLamp = 1;                 // точка горит в левой части прибора, после первой цифры
    private const string Peak = "1.048596";        // самое долгое значение
    private const double DimTime = 0.1;            // просадка яркости перед сдвигом
    private const float DimLevel = 0.35f;
    private const double MaintGap = 0.8;           // сдвиг старта проверки между лампами
    private const double MaintStep = 0.08;         // шаг перебора цифр при проверке катодов
    private const double MaintTime = (Lamps - 1) * MaintGap + 10 * MaintStep;

    private static readonly string[] KnownValues =
    {
        "0.000000", "0.337187", "0.409431", "0.456903", "0.523307", "0.571024",
        "0.571046", "1.048596", "1.129848", "1.130205", "1.130426", "1.382733",
    };

    // Цвета. Стекло тёмное, проволока и ореол — тёплые тона.
    private static readonly Rgba Glass = Rgba.Hex(0x0d0907);
    private static readonly Rgba Frame = Rgba.Hex(0x7a6650);
    private static readonly Rgba Mesh = Rgba.Hex(0x1c120a);     // сетка-анод: редкая, ровная, почти как стекло
    private static readonly Rgba Ghost = new(33, 20, 5);         // призраки: ~13% яркости горящей (255,156,42)
    private static readonly Rgba Lit = Rgba.Hex(0xff9c2a);
    private static readonly Rgba Halo = Rgba.Hex(0x9a4a14);
    private static readonly Rgba BoardBg = Rgba.Hex(0x0a120e);
    private static readonly Rgba BoardLine = Rgba.Hex(0x2c3b33);
    private static readonly Rgba BoardPart = Rgba.Hex(0x3d4a40);
    private static readonly Rgba PilotOff = Rgba.Hex(0x3a0c08);
    private static readonly Rgba PilotOn = Rgba.Hex(0xff3b1f);

    // Субпиксели: байт кода — 0 пусто, 1 призрак, 2 горит.
    private const byte CodeGhost = 1;
    private const byte CodeLit = 2;

    private enum Phase { Idle, Dim, Spin, Maint }

    private Random _rng = new(1);
    private float _aspect;
    private float _interval;
    private bool _ghosts;
    private float _glow;

    private double _clock;
    private double _sinceBeat;
    private double _nextShift, _nextMaint;
    private double _phaseStart, _maintStart;
    private Phase _phase;
    private float _bass, _beatPulse;

    // Геометрия (в ячейках, пересчитывается в Init).
    private int _pitch, _tw, _ox, _y0, _tubeRows, _dr, _boardH;
    private int _ws, _hs, _dy0, _subW, _subH;
    private readonly int[] _lx = new int[Lamps];
    private readonly int[] _dx0 = new int[Lamps];
    private byte[] _sub = Array.Empty<byte>();

    // Состояние ламп.
    private readonly char[] _value = new char[Lamps];   // горящее значение, например "1.048596"
    private readonly char[] _next = new char[Lamps];    // новое значение, к которому идёт сдвиг
    private readonly char[] _disp = new char[Lamps];    // что показывает лампа сейчас
    private readonly bool[] _settled = new bool[Lamps];
    private readonly double[] _stopAt = new double[Lamps];
    private readonly double[] _spinNext = new double[Lamps];
    private readonly float[] _period = new float[Lamps];

    /// <summary>Время остановки каждой лампы в последнем сдвиге (хук для тестов).</summary>
    internal float[] SettleTimes { get; } = new float[Lamps];

    /// <summary>Сколько сдвигов начато (хук для тестов).</summary>
    internal int ShiftCount { get; private set; }

    /// <summary>Идёт ли сдвиг прямо сейчас (хук для тестов).</summary>
    internal bool Shifting => _phase is Phase.Dim or Phase.Spin;

    /// <summary>Значение, которое горит, например "1.048596" (хук для тестов).</summary>
    internal string Shown => new string(_value);

    /// <summary>Прямоугольник цифры лампы в субпикселях: x, y, ширина, высота (хук для тестов).</summary>
    internal (int X, int Y, int W, int H) LampRect(int i) => (_dx0[i], _dy0, _ws, _hs);

    /// <summary>Прямоугольник точки лампы в субпикселях (хук для тестов).</summary>
    internal (int X, int Y, int W, int H) DotRect(int i) =>
        ((_lx[i] + _tw - 3) * 2, (_y0 + _dr + 1) * 3 + 1, 2, 2);

    /// <summary>Маска цифры w×h субпикселей, 1 — штрих (хук для тестов, та же растеризация, что в сцене).</summary>
    internal static byte[] DigitMask(int digit, int w, int h)
    {
        var m = new byte[w * h];
        StrokeDigit(m, w, h, 0, 0, w, h, digit, 1);
        return m;
    }

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _rng = rng;
        _aspect = Math.Clamp(options.CellAspect, 1f, 3f);
        _interval = Math.Clamp(options.GetFloat("interval", 30f), 5f, 120f);
        _ghosts = options.GetBool("ghosts", true);
        _glow = Math.Clamp(options.GetFloat("glow", 0.6f), 0f, 1f);

        // Ряд ламп занимает ~80% ширины. Между трубками — зазор в 1 ячейку.
        _pitch = Math.Max(4, (int)(cols * 0.8) / Lamps);
        _tw = _pitch - 1;
        int total = Lamps * _pitch - 1;
        _ox = (cols - total) / 2;

        // Цифра ~45% высоты экрана; трубка на 2 строки выше и ниже цифры.
        _dr = Math.Max(1, (int)MathF.Round(0.45f * rows));
        _tubeRows = _dr + 4;
        if (_tubeRows > rows)
        {
            _tubeRows = Math.Max(1, rows);
            _dr = Math.Max(1, _tubeRows - 4);
        }

        // Под трубками — плата, если хватает высоты.
        _boardH = rows - _tubeRows >= 5 ? 4 : 0;
        int blockH = _tubeRows + (_boardH > 0 ? 5 : 0);
        _y0 = Math.Max(0, (rows - blockH) / 2);

        // Цифра по физике: высота dr·A, ширина ≈ 0.45 высоты, но не шире трубки.
        // Субпиксель: 0.5 ширины ячейки × A/3 высоты, поэтому ws = 2·ширина, hs = 3·dr.
        float hu = _dr * _aspect;
        float wu = MathF.Min(0.45f * hu, _tw - 2f);
        _ws = Math.Max(2, (int)(2f * wu));
        _hs = 3 * _dr;
        _dy0 = (_y0 + 2) * 3;
        for (int i = 0; i < Lamps; i++)
        {
            _lx[i] = _ox + i * _pitch;
            _dx0[i] = (_lx[i] + _tw / 2) * 2 - _ws / 2;
        }

        _subW = cols * 2;
        _subH = rows * 3;
        _sub = new byte[_subW * _subH];

        // Полный сброс состояния.
        _clock = 0; _sinceBeat = 0; _bass = 0; _beatPulse = 0;
        ShiftCount = 0;
        _phase = Phase.Idle;
        Peak.CopyTo(0, _value, 0, Lamps);
        Peak.CopyTo(0, _disp, 0, Lamps);
        Array.Clear(_settled);
        Array.Clear(SettleTimes);
        _nextShift = 0.3 + _rng.NextDouble() * _interval * 0.25;
        _nextMaint = 180 + _rng.NextDouble() * 120;
    }

    public void OnAudio(in AudioFrame a)
    {
        _bass += ((a.Active ? a.Bass : 0f) - _bass) * 0.25f;
        _beatPulse = a.Active ? a.BeatPulse : 0f;
        if (!a.Active || !a.Beat) return;
        // Сильный удар после тишины (≥ 4 с без битов) запускает сдвиг вне очереди.
        if (_sinceBeat >= 4.0 && _phase == Phase.Idle) StartShift();
        _sinceBeat = 0;
    }

    public void Update(float dt)
    {
        _clock += dt;
        _sinceBeat += dt;
        switch (_phase)
        {
            case Phase.Idle:
                if (_clock >= _nextShift) StartShift();
                else if (_clock >= _nextMaint) StartMaint();
                break;
            case Phase.Dim:
                if (_clock - _phaseStart >= DimTime) BeginSpin();
                break;
            case Phase.Spin:
                UpdateSpin();
                break;
            case Phase.Maint:
                UpdateMaint();
                break;
        }
    }

    private void StartShift()
    {
        ChooseNext();
        _phase = Phase.Dim;
        _phaseStart = _clock;
        ShiftCount++;
        Array.Clear(_settled);
    }

    private void ChooseNext()
    {
        if (_rng.NextDouble() < 0.5)
        {
            KnownValues[_rng.Next(KnownValues.Length)].CopyTo(0, _next, 0, Lamps);
            return;
        }
        _next[0] = _rng.Next(2) == 0 ? '0' : '1';
        _next[1] = '.';
        for (int i = 2; i < Lamps; i++) _next[i] = RandDigit();
    }

    private void BeginSpin()
    {
        _phase = Phase.Spin;
        double first = 1.0 + _rng.NextDouble();          // первая лампа встаёт через 1–2 с
        double gap = 0.15 + 0.15 * _rng.NextDouble();    // интервал между остановками
        for (int i = 0; i < Lamps; i++)
        {
            _stopAt[i] = _clock + first + i * gap;
            _spinNext[i] = _clock;
            _period[i] = 0.03f + 0.03f * (float)_rng.NextDouble();
        }
    }

    private void UpdateSpin()
    {
        bool all = true;
        for (int i = 0; i < Lamps; i++)
        {
            if (_settled[i]) continue;
            if (_clock >= _stopAt[i])
            {
                _settled[i] = true;
                _value[i] = _next[i];
                _disp[i] = _next[i];
                SettleTimes[i] = (float)_clock;
                continue;
            }
            all = false;
            if (_clock >= _spinNext[i])
            {
                _disp[i] = RandDigit();
                _spinNext[i] = _clock + _period[i];
            }
        }
        if (all) EndShift();
    }

    private void EndShift()
    {
        _phase = Phase.Idle;
        double hold = _interval * (0.7 + 0.6 * _rng.NextDouble());
        if (SameAs(_value, Peak)) hold += 0.5 * _interval;   // 1.048596 держится дольше всех
        _nextShift = _clock + hold;
    }

    private void StartMaint()
    {
        _phase = Phase.Maint;
        _maintStart = _clock;
    }

    private void UpdateMaint()
    {
        double el = _clock - _maintStart;
        if (el >= MaintTime)
        {
            _phase = Phase.Idle;
            Array.Copy(_value, _disp, Lamps);
            _nextMaint = _clock + 180 + 120 * _rng.NextDouble();
            return;
        }
        // Лампы по очереди прогоняют цифры 0–9, затем возвращается значение.
        for (int i = 0; i < Lamps; i++)
        {
            double s = (el - i * MaintGap) / MaintStep;
            _disp[i] = s >= 0 && s < 10 ? (char)('0' + (int)s) : _value[i];
        }
    }

    public void Draw(CharGrid g)
    {
        g.Clear(Rgba.Black);
        Array.Clear(_sub);

        // Субпиксели: призраки всех цифр, горящая цифра поверх, точка.
        for (int i = 0; i < Lamps; i++)
        {
            if (_ghosts)
                for (int d = 0; d < 10; d++)
                    StrokeDigit(_sub, _subW, _subH, _dx0[i], _dy0, _ws, _hs, d, CodeGhost);
            char c = _disp[i];
            if (c >= '0' && c <= '9')
                StrokeDigit(_sub, _subW, _subH, _dx0[i], _dy0, _ws, _hs, c - '0', CodeLit);
            var (dx, dy, dw, dh) = DotRect(i);
            if (i == DotLamp && c == '.') FillSub(dx, dy, dw, dh, CodeLit);
            else if (_ghosts) FillSub(dx, dy, dw, dh, CodeGhost);
        }

        float pulse = 1f + 0.4f * _beatPulse + 0.2f * _bass;
        for (int i = 0; i < Lamps; i++) DrawLamp(g, i, pulse);
        if (_boardH > 0) DrawBoard(g);
    }

    private void DrawLamp(CharGrid g, int i, float pulse)
    {
        int lx = _lx[i];
        float lvl = _phase == Phase.Dim ? DimLevel : 1f;
        float flick = 1f + 0.05f * MathF.Sin((float)_clock * (1.3f + 0.21f * i) + 1.7f * i);
        float k = lvl * flick;
        float cxMid = lx + _tw * 0.5f, cyMid = _y0 + _tubeRows * 0.5f;
        float halfW = _tw * 0.5f, halfH = _tubeRows * 0.5f;
        Rgba lit = Lit.Scale(k);

        for (int cy = _y0; cy < _y0 + _tubeRows; cy++)
        {
            for (int cx = lx; cx < lx + _tw; cx++)
            {
                // Ореол: радиально от центра трубки, тёплее ближе к центру.
                float dx = (cx + 0.5f - cxMid) / halfW, dy = (cy + 0.5f - cyMid) / halfH;
                float f = 1f - MathF.Sqrt(dx * dx + dy * dy);
                float t = f <= 0f ? 0f : _glow * k * pulse * 0.9f * f * MathF.Sqrt(f);
                Rgba bg = Rgba.Lerp(Glass, Halo, Math.Clamp(t, 0f, 1f));

                bool top = cy == _y0, bottom = cy == _y0 + _tubeRows - 1;
                bool left = cx == lx, right = cx == lx + _tw - 1;
                if (top || bottom || left || right)
                {
                    char ch = (top, left, bottom, right) switch
                    {
                        (true, true, _, _) => '╭',
                        (true, _, _, true) => '╮',
                        (_, true, true, _) => '╰',
                        (_, _, true, true) => '╯',
                        (true, _, _, _) or (_, _, true, _) => '─',
                        _ => '│',
                    };
                    g.Put(cx, cy, ch, Frame, bg);
                    continue;
                }

                // Внутренность: горящая цифра; призраки той же формы тусклым ровным цветом;
                // сетка-анод — редкая регулярная сетка (каждый 3-й столбец, каждый 6-й ряд подъячеек).
                int litBits = 0, ghostBits = 0, meshBits = 0;
                for (int b = 0; b < 6; b++)
                {
                    int sx = cx * 2 + (b & 1), sy = cy * 3 + (b >> 1);
                    byte code = SubAt(sx, sy);
                    if (code == CodeLit) litBits |= 1 << b;
                    else if (code == CodeGhost) ghostBits |= 1 << b;
                    if (sx % 3 == 0 || sy % 6 == 0) meshBits |= 1 << b;
                }
                if (litBits != 0) g.Put(cx, cy, GlyphSet.Sextant(litBits), lit, bg);
                else if (ghostBits != 0) g.Put(cx, cy, GlyphSet.Sextant(ghostBits | meshBits), Ghost, bg);
                else if (meshBits != 0) g.Put(cx, cy, GlyphSet.Sextant(meshBits), Mesh, bg);
                else g.Put(cx, cy, ' ', Mesh, bg);
            }
        }
    }

    private void DrawBoard(CharGrid g)
    {
        int bx0 = _ox - 2, bx1 = _ox + Lamps * _pitch;   // включительно
        int by0 = _y0 + _tubeRows + 1, by1 = by0 + _boardH - 1;
        for (int y = by0; y <= by1; y++)
            for (int x = bx0; x <= bx1; x++)
                g.Put(x, y, ' ', BoardPart, BoardBg);

        for (int x = bx0 + 1; x < bx1; x++)
        {
            g.Put(x, by0, '─', BoardLine, BoardBg);
            g.Put(x, by1, '─', BoardLine, BoardBg);
        }
        for (int y = by0 + 1; y < by1; y++)
        {
            g.Put(bx0, y, '│', BoardLine, BoardBg);
            g.Put(bx1, y, '│', BoardLine, BoardBg);
        }
        g.Put(bx0, by0, '┌', BoardLine, BoardBg);
        g.Put(bx1, by0, '┐', BoardLine, BoardBg);
        g.Put(bx0, by1, '└', BoardLine, BoardBg);
        g.Put(bx1, by1, '┘', BoardLine, BoardBg);

        // Пара деталей: микросхемы и резистор.
        for (int x = bx0 + 4; x < bx1 - 2; x += 7)
            g.Put(x, by0 + 1, '▪', BoardPart, BoardBg);
        g.Text(bx0 + 8, by0 + 2, "[==]", BoardPart, BoardBg);

        // Тусклый неоновый индикатор питания, медленно дышит.
        float breathe = 0.55f + 0.45f * MathF.Sin((float)_clock * 1.9f);
        g.Put(bx1 - 2, by0 + 1, '●', Rgba.Lerp(PilotOff, PilotOn, breathe * 0.5f), BoardBg);
    }

    private byte SubAt(int sx, int sy) =>
        (uint)sx < (uint)_subW && (uint)sy < (uint)_subH ? _sub[sy * _subW + sx] : (byte)0;

    private void FillSub(int x, int y, int w, int h, byte code)
    {
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++)
                if ((uint)xx < (uint)_subW && (uint)yy < (uint)_subH && _sub[yy * _subW + xx] < code)
                    _sub[yy * _subW + xx] = code;
    }

    private char RandDigit() => (char)('0' + _rng.Next(10));

    private static bool SameAs(char[] v, string s)
    {
        for (int i = 0; i < v.Length; i++)
            if (v[i] != s[i]) return false;
        return true;
    }

    /// <summary>Принудительно выставить значение без сдвига (хук для тестов и осмотра цифр).</summary>
    internal void ForceValueForTest(string v)
    {
        v.CopyTo(0, _value, 0, Lamps);
        v.CopyTo(0, _disp, 0, Lamps);
        _phase = Phase.Idle;
        _nextShift = _clock + 1e9;
        _nextMaint = _clock + 1e9;
    }

    /// <summary>Растеризация цифры в прямоугольник буфера: ломаные по целым координатам, линия Брезенхэма, без аллокаций.</summary>
    private static void StrokeDigit(byte[] buf, int bw, int bh, int x0, int y0, int w, int h, int digit, byte code)
    {
        var lines = Strokes[digit];
        for (int p = 0; p < lines.Length; p++)
        {
            var pts = lines[p];
            for (int k = 0; k + 3 < pts.Length; k += 2)
            {
                int ax = (int)MathF.Round(x0 + pts[k] * (w - 1)), ay = (int)MathF.Round(y0 + pts[k + 1] * (h - 1));
                int bx = (int)MathF.Round(x0 + pts[k + 2] * (w - 1)), by = (int)MathF.Round(y0 + pts[k + 3] * (h - 1));
                Bresenham(buf, bw, bh, ax, ay, bx, by, code);
            }
        }
    }

    private static void Bresenham(byte[] buf, int bw, int bh, int x0, int y0, int x1, int y1, byte code)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            if ((uint)x0 < (uint)bw && (uint)y0 < (uint)bh)
            {
                int idx = y0 * bw + x0;
                if (buf[idx] < code) buf[idx] = code;
            }
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>Эллипсная дуга от a0 до a1 (градусы, 0 — вправо, 90 — вниз), шаг не больше 10°.</summary>
    private static float[] Arc(float cx, float cy, float rx, float ry, float a0, float a1)
    {
        int n = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(a1 - a0) / 10f));
        var p = new float[(n + 1) * 2];
        for (int i = 0; i <= n; i++)
        {
            float t = (a0 + (a1 - a0) * i / n) * MathF.PI / 180f;
            p[2 * i] = cx + rx * MathF.Cos(t);
            p[2 * i + 1] = cy + ry * MathF.Sin(t);
        }
        return p;
    }

    private static float[] Pl(params float[] pts) => pts;

    // Проволочные начертания цифр в единичном квадрате (y вниз). Каждая цифра — набор ломаных (пары x,y).
    private static readonly float[][][] Strokes =
    {
        // 0: овал
        new[] { Arc(.5f, .5f, .42f, .5f, 0, 360) },
        // 1: стойка с флажком и подножием
        new[] { Pl(.55f, 0f, .55f, 1f), Pl(.55f, 0f, .12f, .25f), Pl(.2f, 1f, .9f, 1f) },
        // 2: верхняя дуга, диагональ вниз-влево, основание
        new[] { Arc(.5f, .3f, .42f, .28f, 180, 360), Pl(.92f, .3f, .05f, .98f, .95f, .98f) },
        // 3: две дуги, встречаются в средней точке
        // Верхняя дуга заканчивается в точке (.48,.5), нижняя начинается в ней же: касп без перекрестия.
        new[] { Arc(.48f, .27f, .4f, .23f, 200, 450), Arc(.48f, .74f, .42f, .24f, 270, 520) },
        // 4: открытая четвёрка
        new[] { Pl(.72f, 1f, .72f, 0f), Pl(.72f, 0f, .02f, .7f, .98f, .7f) },
        // 5: верхняя перекладина, стойка, плавная нижняя дуга
        new[] { Pl(.9f, .03f, .15f, .03f, .12f, .47f, .27f, .458f), Arc(.48f, .7f, .42f, .28f, 240, 500) },
        // 6: нижняя замкнутая петля и хвост вверх
        new[] { Arc(.5f, .7f, .42f, .28f, 0, 360), Pl(.08f, .7f, .08f, .5f), Arc(.55f, .5f, .47f, .5f, 180, 290) },
        // 7: перекладина и диагональ
        new[] { Pl(.03f, .03f, .97f, .03f, .35f, 1f) },
        // 8: две замкнутые петли, верхняя меньше
        new[] { Arc(.5f, .27f, .36f, .25f, 0, 360), Arc(.5f, .73f, .42f, .27f, 0, 360) },
        // 9: замкнутая верхняя петля и плавный хвост вниз
        new[] { Arc(.5f, .3f, .42f, .28f, 0, 360), Pl(.92f, .3f, .92f, .55f), Arc(.42f, .55f, .5f, .45f, 0, 75) },
    };
}
