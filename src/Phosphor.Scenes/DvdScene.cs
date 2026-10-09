using System.Text;
using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// Прыгающий DVD-логотип. Отскоки по обеим осям в пределах 0.15 с — угол: праздник на 4 с.
/// Параметр trail (по умолчанию false) — тающий след. Палитры green/amber/mono — оттенки цвета палитры.
/// </summary>
public sealed class DvdScene : IScene, IAudioReactive
{
    private const float BaseSpeed = 14f;          // ячеек/с при Speed = 1 (по X)
    private const float VyRatio = 0.55f;          // vy = vx * 0.55: ячейки высокие, диагональ ~45°
    private const float CornerWindow = 0.15f;     // с: два отскока ближе этого — угол
    private const float PartyDuration = 4f;       // с
    private const float PartyBlinkOn = 0.35f;     // с видна большая надпись
    private const float PartyBlinkCycle = 0.45f;  // с: 0.35 видна + 0.1 скрыта
    private const float SecondVolleyAt = 0.6f;    // с после угла — второй залп
    private const float FlashDuration = 0.15f;    // с вспышки фона
    private const float KickTau = 0.12f;          // с: затухание рывка скорости после удара (×2 в момент удара)
    private const float Gravity = 11f;            // ячеек/с²
    private const float SpeedMin = 25f;          // ячеек/с (до умножения на sin и 0.55)
    private const float SpeedMax = 70f;
    private const int TextMarginX = 2;           // безопасная зона ЭЛТ для текста интерфейса
    private const int TextMarginY = 1;
    private const float ConfettiMinDeg = 10f;
    private const float ConfettiMaxDeg = 80f;
    private const float Never = 1000f;            // «давно» для таймеров
    private const int VolleyMin = 100;            // залп: 100..128 частиц
    private const int VolleyRange = 29;
    private const int MaxParticles = 256;         // два залпа по ≤128
    private const int TrailLength = 20;
    private const int BigTextMinCols = 44;        // уже — обычный текст

    private static readonly string[] BigLogo =
    {
        "██████╗ ██╗   ██╗██████╗ ",
        "██╔══██╗██║   ██║██╔══██╗",
        "██║  ██║██║   ██║██║  ██║",
        "██║  ██║╚██╗ ██╔╝██║  ██║",
        "██████╔╝ ╚████╔╝ ██████╔╝",
        "╚═════╝   ╚═══╝  ╚═════╝ ",
        "  ══════ V I D E O ══════",
    };
    private static readonly string[] SmallLogo = { "[DVD]" };
    private static readonly int BigWidth = BigLogo.Max(l => l.Length);
    private static readonly char[] Confetti = { '*', '+', '·', '•', '°', '♦' };
    private static readonly Rgba CounterColor = new(0x70, 0x70, 0x70);

    // Шрифт 5×5 для «CORNER!»: только буквы C O R N E и «!». '█' — точка, пробел — пусто.
    private static readonly string[] GlyphC = { " ████", "█    ", "█    ", "█    ", " ████" };
    private static readonly string[] GlyphO = { " ███ ", "█   █", "█   █", "█   █", " ███ " };
    private static readonly string[] GlyphR = { "████ ", "█   █", "████ ", "█  █ ", "█   █" };
    private static readonly string[] GlyphN = { "█   █", "██  █", "█ █ █", "█  ██", "█   █" };
    private static readonly string[] GlyphE = { "█████", "█    ", "████ ", "█    ", "█████" };
    private static readonly string[] GlyphBang = { "  █  ", "  █  ", "  █  ", "     ", "  █  " };
    private static readonly string[] BigCorner = Compose(GlyphC, GlyphO, GlyphR, GlyphN, GlyphE, GlyphR, GlyphBang);

    public string Id => "dvd";

    /// <summary>Сколько раз логотип отскочил от стенок (по каждой оси отдельно).</summary>
    public int Bounces => _bounces;

    /// <summary>Сколько раз логотип попал в угол.</summary>
    public int Corners => _corners;

    /// <summary>Левый край логотипа в ячейках (всегда в пределах экрана).</summary>
    public int X => Math.Clamp((int)MathF.Floor(_x), 0, _maxX);

    /// <summary>Верхний край логотипа в ячейках (всегда в пределах экрана).</summary>
    public int Y => Math.Clamp((int)MathF.Floor(_y), 0, _maxY);

    public int LogoWidth => _logoW;
    public int LogoHeight => _logoH;

    private struct Particle
    {
        public float X, Y, VX, VY, Age, Life;
        public char Ch;
        public Rgba Color;
    }

    private int _cols, _rows;
    private Random _rng = new(0);
    private float _speed = 1f;
    private float _audioMul = 1f;
    private float _kick;               // рывок скорости после удара: 1 в момент удара, затухает к 0
    private bool _beatPending;
    private bool _trail;
    private bool _mono;                // палитра green/amber/mono: цвет — оттенки Palettes.Base
    private string _palName = "default";

    private bool _big;
    private string[] _lines = SmallLogo;
    private int _logoW, _logoH;
    private int _maxX, _maxY;          // диапазон левого/верхнего края; 0 — стенки по этой оси нет

    private float _x, _y, _vx, _vy;    // базовые скорости (без audio-множителя), ячеек/с
    private float _hue;
    private Rgba _color;
    private int _bounces, _corners;
    private float _clock;              // общее время сцены, с
    private float _cornerClock;        // момент последнего угла
    private float _lastInterval;       // с между предпоследним и последним углом
    private float _tX, _tY;            // с с последнего отскока по каждой оси

    private bool _party;
    private float _partyT;
    private int _cornerCX, _cornerCY;
    private bool _volley2Pending;
    private Particle[] _parts = Array.Empty<Particle>();

    private readonly int[] _trailX = new int[TrailLength];
    private readonly int[] _trailY = new int[TrailLength];
    private int _trailPos;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = cols;
        _rows = rows;
        _rng = rng;
        _speed = MathF.Max(0f, options.Speed);
        _trail = options.GetBool("trail", false);
        _palName = options.Palette.ToLowerInvariant();
        _mono = _palName is "green" or "amber" or "mono";

        _big = cols >= BigWidth + 2 && rows >= BigLogo.Length + 2;
        _lines = _big ? BigLogo : SmallLogo;
        _logoW = _big ? BigWidth : SmallLogo[0].Length;
        _logoH = _lines.Length;
        _maxX = Math.Max(0, cols - _logoW);
        _maxY = Math.Max(0, rows - _logoH);

        _audioMul = 1f;
        _kick = 0f;
        _beatPending = false;
        _bounces = 0;
        _corners = 0;
        _clock = 0f;
        _cornerClock = 0f;
        _lastInterval = 0f;
        _tX = Never;
        _tY = Never;

        _party = false;
        _partyT = 0f;
        _cornerCX = 0;
        _cornerCY = 0;
        _volley2Pending = false;
        _parts = new Particle[MaxParticles];

        _x = rng.NextSingle() * _maxX;
        _y = rng.NextSingle() * _maxY;
        _vx = (rng.Next(2) == 0 ? 1f : -1f) * BaseSpeed * _speed;
        _vy = (rng.Next(2) == 0 ? 1f : -1f) * BaseSpeed * VyRatio * _speed;
        _hue = rng.NextSingle() * 360f;
        _color = _mono ? Palettes.Base(_palName) : Rgba.Hsv(_hue, 0.8f, 1f);

        _trailPos = 0;
        for (int i = 0; i < TrailLength; i++)
        {
            _trailX[i] = X;
            _trailY[i] = Y;
        }
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;
        _clock += dt;

        if (_beatPending)
        {
            _beatPending = false;
            Recolor();
        }

        if (_party)
        {
            _partyT += dt;
            if (_partyT >= PartyDuration) _party = false;
        }
        if (_volley2Pending && _partyT >= SecondVolleyAt)
        {
            _volley2Pending = false;
            SpawnVolley();
        }

        for (int i = 0; i < _parts.Length; i++)
        {
            ref Particle p = ref _parts[i];
            if (p.Age >= p.Life) continue;
            p.Age += dt;
            p.VY += Gravity * dt;
            p.X += p.VX * dt;
            p.Y += p.VY * dt;
        }

        _tX = MathF.Min(_tX + dt, Never);
        _tY = MathF.Min(_tY + dt, Never);

        if (_kick > 0f) _kick *= MathF.Exp(-dt / KickTau);
        float step = dt * _audioMul * (1f + _kick);
        bool hx = _maxX > 0 && Advance(ref _x, ref _vx, _maxX, step);
        bool hy = _maxY > 0 && Advance(ref _y, ref _vy, _maxY, step);

        if (hx)
        {
            _bounces++;
            Recolor();
        }
        if (hy)
        {
            _bounces++;
            Recolor();
        }

        if (hx && hy) Corner();
        else if (hx)
        {
            if (_tY <= CornerWindow) Corner();
            else _tX = 0f;
        }
        else if (hy)
        {
            if (_tX <= CornerWindow) Corner();
            else _tY = 0f;
        }

        _trailX[_trailPos] = X;
        _trailY[_trailPos] = Y;
        _trailPos = (_trailPos + 1) % TrailLength;
    }

    public void Draw(CharGrid grid)
    {
        grid.Clear(FlashColor());
        if (_trail) DrawTrail(grid);
        DrawParticles(grid);
        DrawLogo(grid);
        if (_party) DrawParty(grid);
        DrawCounter(grid);
    }

    public void OnAudio(in AudioFrame audio)
    {
        if (!audio.Active)
        {
            _audioMul = 1f;
            _kick = 0f;
            return;
        }
        _audioMul = 1f + 0.5f * audio.Bass;
        if (audio.Beat)
        {
            _beatPending = true;
            _kick = 1f;
        }
    }

    /// <summary>Сдвигает край на step; при ударе о стенку ставит край ровно к ней и разворачивает скорость.</summary>
    private static bool Advance(ref float p, ref float v, int max, float step)
    {
        p += v * step;
        if (p <= 0f)
        {
            p = 0f;
            if (v < 0f)
            {
                v = -v;
                return true;
            }
            return false;
        }
        if (p >= max)
        {
            p = max;
            if (v > 0f)
            {
                v = -v;
                return true;
            }
        }
        return false;
    }

    private static float TimeToWall(float p, float v, int max)
    {
        if (max <= 0 || v == 0f) return float.PositiveInfinity;
        float d = v > 0f ? max - p : p;
        return MathF.Max(0f, d) / MathF.Abs(v);
    }

    private void Recolor()
    {
        if (_mono)
        {
            // Оттенки палитры по яркости.
            _color = Palettes.Base(_palName).Scale(0.55f + 0.45f * _rng.NextSingle());
            return;
        }
        _hue = (_hue + 60f + _rng.NextSingle() * 240f) % 360f;
        _color = Rgba.Hsv(_hue, 0.8f, 1f);
    }

    /// <summary>Угол: праздник, два залпа конфетти из этого угла.</summary>
    private void Corner()
    {
        _corners++;
        _lastInterval = _clock - _cornerClock;
        _cornerClock = _clock;
        _tX = Never;
        _tY = Never;
        _party = true;
        _partyT = 0f;

        _cornerCX = _x * 2f < _maxX ? 0 : _cols - 1;
        _cornerCY = _y * 2f < _maxY ? 0 : _rows - 1;
        for (int i = 0; i < _parts.Length; i++) _parts[i].Life = 0f;
        SpawnVolley();
        _volley2Pending = true;
    }

    /// <summary>Залп 100..128 частиц в свободные слоты пула.</summary>
    private void SpawnVolley()
    {
        int n = VolleyMin + _rng.Next(VolleyRange + 1);
        int spawned = 0;
        for (int i = 0; i < _parts.Length && spawned < n; i++)
        {
            if (_parts[i].Age < _parts[i].Life) continue;
            _parts[i] = MakeConfetti();
            spawned++;
        }
    }

    private Particle MakeConfetti()
    {
        float sx = _cornerCX == 0 ? 1f : -1f;   // внутрь экрана
        float sy = _cornerCY == 0 ? 1f : -1f;
        float a = (ConfettiMinDeg + (ConfettiMaxDeg - ConfettiMinDeg) * _rng.NextSingle()) * MathF.PI / 180f;
        float speed = SpeedMin + (SpeedMax - SpeedMin) * _rng.NextSingle();
        return new Particle
        {
            X = _cornerCX + 0.5f,
            Y = _cornerCY + 0.5f,
            VX = sx * speed * MathF.Cos(a),
            VY = sy * speed * MathF.Sin(a) * VyRatio,
            Age = 0f,
            Life = 2f + _rng.NextSingle(),
            Ch = Confetti[_rng.Next(Confetti.Length)],
            Color = Rgba.Hsv(_rng.NextSingle() * 360f, 0.9f, 1f),
        };
    }

    private Rgba FlashColor() =>
        _party && _partyT < FlashDuration
            ? Rgba.White.Scale(0.25f * (1f - _partyT / FlashDuration))
            : Rgba.Black;

    private void DrawTrail(CharGrid grid)
    {
        // Самая старая запись — та, что будет перезаписана следующей.
        for (int a = 0; a < TrailLength; a++)
        {
            int idx = (_trailPos + a) % TrailLength;
            Rgba fg = _color.Scale(0.1f + 0.5f * (a + 1) / (float)TrailLength);
            int tx = _trailX[idx], ty = _trailY[idx];
            for (int r = 0; r < _lines.Length; r++)
            {
                string line = _lines[r];
                for (int c = 0; c < line.Length; c++)
                {
                    if (line[c] != ' ') grid.Put(tx + c, ty + r, '░', fg);
                }
            }
        }
    }

    private void DrawParticles(CharGrid grid)
    {
        for (int i = 0; i < _parts.Length; i++)
        {
            ref Particle p = ref _parts[i];
            if (p.Age >= p.Life) continue;
            float k = 1f - 0.6f * (p.Age / p.Life);
            grid.Put((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), p.Ch, p.Color.Scale(k));
        }
    }

    private void DrawLogo(CharGrid grid)
    {
        int x0 = X, y0 = Y;
        for (int r = 0; r < _lines.Length; r++)
        {
            string line = _lines[r];
            for (int c = 0; c < line.Length; c++)
            {
                char ch = line[c];
                if (ch == ' ') continue;
                Rgba fg = _party ? Rgba.Hsv(_partyT * 180f + (x0 + c) * 9f, 0.8f, 1f) : _color;
                grid.Put(x0 + c, y0 + r, ch, fg);
            }
        }
    }

    private void DrawParty(CharGrid grid)
    {
        if (_cols >= BigTextMinCols)
        {
            DrawBigParty(grid);
            return;
        }
        Rgba fg = Rgba.Hsv(_partyT * 180f, 0.7f, 1f);
        int mid = _rows / 2;
        grid.Text((_cols - 15) / 2, mid - 1, "*** CORNER! ***", fg);
        DrawInfo(grid, (_cols - InfoWidth()) / 2, mid + 1, fg);
    }

    /// <summary>Большая «CORNER!» 5×5 по центру, радуга по колонкам, мигает; под ней строка с номером и интервалом.</summary>
    private void DrawBigParty(CharGrid grid)
    {
        int top = _rows / 2 - 5;
        int x0 = (_cols - BigCorner[0].Length) / 2;
        if (_partyT % PartyBlinkCycle < PartyBlinkOn)
        {
            for (int r = 0; r < BigCorner.Length; r++)
            {
                string line = BigCorner[r];
                for (int c = 0; c < line.Length; c++)
                {
                    if (line[c] != '█') continue;
                    Rgba fg = Rgba.Hsv(_partyT * 180f + (x0 + c) * 9f, 0.8f, 1f);
                    grid.Put(x0 + c, top + r, '█', fg);
                }
            }
        }
        DrawInfo(grid, (_cols - InfoWidth()) / 2, top + 6, Rgba.Hsv(_partyT * 180f + 180f, 0.5f, 1f));
    }

    /// <summary>«CORNER #N · mm:ss» — mm:ss это интервал с прошлого угла.</summary>
    private void DrawInfo(CharGrid grid, int x, int y, Rgba fg)
    {
        int total = (int)_lastInterval;
        int m = total / 60, s = total % 60;
        grid.Text(x, y, "CORNER #", fg);
        x += 8;
        x = WriteInt(grid, x, y, _corners, 1, fg);
        grid.Text(x, y, " · ", fg);
        x += 3;
        x = WriteInt(grid, x, y, m, 2, fg);
        grid.Put(x, y, ':', fg);
        x++;
        WriteInt(grid, x, y, s, 2, fg);
    }

    private int InfoWidth()
    {
        int m = (int)_lastInterval / 60;
        return 8 + DigitCount(_corners) + 3 + Math.Max(2, DigitCount(m)) + 3;
    }

    private void DrawCounter(CharGrid grid)
    {
        // Текст интерфейса — с отступом 2 по X и 1 по Y от краёв.
        int w = 9 + DigitCount(_corners);
        DrawNumber(grid, _cols - TextMarginX - w, _rows - 1 - TextMarginY, "corners: ", _corners, CounterColor);
    }

    /// <summary>Пишет prefix и неотрицательное число без аллокаций.</summary>
    private static void DrawNumber(CharGrid grid, int x, int y, string prefix, int n, Rgba fg)
    {
        grid.Text(x, y, prefix, fg);
        WriteInt(grid, x + prefix.Length, y, n, 1, fg);
    }

    /// <summary>Пишет число (не короче minDigits цифр с ведущими нулями), возвращает x за последней цифрой.</summary>
    private static int WriteInt(CharGrid grid, int x, int y, int n, int minDigits, Rgba fg)
    {
        int d = Math.Max(minDigits, DigitCount(n));
        for (int i = d - 1; i >= 0; i--)
        {
            grid.Put(x + i, y, (char)('0' + n % 10), fg);
            n /= 10;
        }
        return x + d;
    }

    private static int DigitCount(int n)
    {
        int d = 1;
        for (int v = n; v >= 10; v /= 10) d++;
        return d;
    }

    private static string[] Compose(params string[][] glyphs)
    {
        var rows = new string[5];
        for (int r = 0; r < rows.Length; r++)
        {
            var sb = new StringBuilder();
            for (int g = 0; g < glyphs.Length; g++)
            {
                if (g > 0) sb.Append(' ');
                sb.Append(glyphs[g][r]);
            }
            rows[r] = sb.ToString();
        }
        return rows;
    }
}
