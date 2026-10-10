using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// «Крэктро»: заставка крэкерской группы эпохи Amiga/C64. Снизу вверх: звёздный параллакс, копперные полосы,
/// металлический логотип и синус-скроллер поверх сетки. Звук: удар пинает логотип и сжимает полосы,
/// бас утолщает полосы, уровень ускоряет скроллер. Без звука сцена живёт сама.
/// </summary>
public sealed class CracktroScene : IScene, IDataScene, IAudioReactive, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec ScrollSpec = new("scrollSpeed", SceneParamKind.Float, 12f, 2f, 60f, "скорость бегущей строки, симв./с");
    public static readonly SceneParamSpec BounceSpec = new("bounce", SceneParamKind.Float, 110f, 0f, 300f, "сила отскока логотипа, px/с");
    private static readonly SceneParamSpec[] ParamList = { ScrollSpec, BounceSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    private float _charsPerSec, _jumpSpeed;

    // Шрифт логотипа 5×7: по 7 строк, в каждой 5 бит (старший бит — слева).
    // Порядок: P, H, O, S, R.
    private static readonly byte[][] Font =
    {
        new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000 }, // P
        new byte[] { 0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 }, // H
        new byte[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 }, // O
        new byte[] { 0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110 }, // S
        new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001 }, // R
    };

    private const string LogoText = "PHOSPHOR";
    private const int GlyphH = 7;
    private const int LogoUnitsW = 8 * 6 - 1; // 8 букв по 5 колонок + промежутки по 1

    /// <summary>Куски бегущей строки; между ними вставляются новости.</summary>
    private static readonly string[] Greetings =
    {
        "PHOSPHOR PRESENTS ... CRACKED BY THE NIGHT SHIFT ... ",
        "GREETINGS TO ALL SCENERS ... HELLO TO THE BYTE BANDITS AND THE PIXEL PIRATES ... ",
        "NO SPRITES WERE HARMED IN THE MAKING OF THIS INTRO ... ",
        "THIS CRACK TOOK THREE NIGHTS AND ONE VERY BAD SANDWICH ... ",
    };

    private const int BufCap = 2048;
    private const int MaxNewsPerPass = 3;
    private const int MaxNewsChars = 90;
    private const float SpringK = 190f;    // жёсткость пружины логотипа (ω ≈ 13.8 рад/с)
    private const float SpringC = 5.5f;    // демпфирование: недодемпфировано, отскок виден

    private static readonly float[] BarHeights = { 8f, 10f, 12f, 9f, 11f, 10f };
    private static readonly Rgba[] BarColors =
    {
        Rgba.Hex(0xFF2828), // красный
        Rgba.Hex(0xFF8C00), // оранжевый
        Rgba.Hex(0x28DC3C), // зелёный
        Rgba.Hex(0x3C8CFF), // голубой
        Rgba.Hex(0xAA3CFF), // фиолетовый
    };

    private static readonly Rgba[] Metal =
    {
        Rgba.Hex(0xFFFFFF), Rgba.Hex(0x9FB6D8), Rgba.Hex(0x3A4A7A), Rgba.Hex(0x8FA8D0),
        Rgba.Hex(0xF0F4FF), Rgba.Hex(0x6078A8), Rgba.Hex(0x2A3560),
    };

    private static readonly Rgba[] Rainbow =
    {
        Rgba.Hex(0xFF0000), Rgba.Hex(0xFF8000), Rgba.Hex(0xFFFF00), Rgba.Hex(0x00FF00),
        Rgba.Hex(0x00FFFF), Rgba.Hex(0x0000FF), Rgba.Hex(0xFF00FF),
    };

    public string Id => "cracktro";

    public IReadOnlyList<string> DataKeys { get; } = new[] { "news" };

    private int _cols, _rows, _w, _h, _logoScale, _zoneTop, _zoneH, _baseRow, _amp;
    private float _speed = 1f;
    private float _t;
    private Random _rng = new(0);
    private RetroCanvas _canvas = null!;
    private RetroClock _clock = new(50f);
    private float _copTime;

    // Звёзды: три плана; на каждом плане свой цвет и скорость.
    private float[] _starX = Array.Empty<float>();
    private int[] _starY = Array.Empty<int>();
    private byte[] _starPlane = Array.Empty<byte>();
    private readonly Rgba[] _planeColor = new Rgba[3];
    private static readonly float[] PlaneSpeed = { 4f, 9f, 18f };

    // Копперные полосы: позиции и глубина (cos фазы) обновляются на тике 50 Гц.
    private readonly float[] _barCenter = new float[6];
    private readonly float[] _barDepth = new float[6];
    private readonly int[] _order = new int[6];

    // Логотип: пружина по y (px, + вниз).
    private float _logoOff, _logoVel;
    private Rgba _shadow;

    // Звук.
    private bool _audioActive, _beatPending;
    private float _bass, _level, _bassSm, _levelSm, _kick;

    // Скроллер: символы в буфере, позиция в символах (по модулю длины буфера).
    private readonly char[] _buf = new char[BufCap];
    private int _bufLen;
    private float _scrollPos;
    private int _newsCursor;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _charsPerSec = Math.Clamp(options.GetFloat(ScrollSpec.Name, ScrollSpec.Default), ScrollSpec.Min, ScrollSpec.Max);
        _jumpSpeed = Math.Clamp(options.GetFloat(BounceSpec.Name, BounceSpec.Default), BounceSpec.Min, BounceSpec.Max);
        _cols = Math.Max(1, cols);
        _rows = Math.Max(1, rows);
        _rng = rng;
        _speed = MathF.Max(0.05f, options.Speed);
        _canvas = new RetroCanvas(_cols, _rows, CanvasMode.HalfBlock);
        _w = _canvas.Width;
        _h = _canvas.Height;

        _logoScale = LogoScale(_w, _h);
        _zoneH = Math.Max(1, _rows / 3);
        _zoneTop = _rows - _zoneH;
        _baseRow = _zoneTop + (_zoneH - 1) / 2;
        _amp = Math.Min(3, (_zoneH - 1) / 2);

        _planeColor[0] = Q(0x444466);
        _planeColor[1] = Q(0x888899);
        _planeColor[2] = Q(0xFFFFFF);
        _shadow = Q(0x303060);

        int n = Math.Max(8, _cols * _rows / 10);
        _starX = new float[n];
        _starY = new int[n];
        _starPlane = new byte[n];
        for (int i = 0; i < n; i++)
        {
            float r = rng.NextSingle();
            _starPlane[i] = r < 0.5f ? (byte)0 : r < 0.8f ? (byte)1 : (byte)2;
            _starX[i] = rng.NextSingle() * _w;
            _starY[i] = rng.Next(_h);
        }

        _clock = new RetroClock(50f);
        _copTime = 0f;
        UpdateBars();

        _logoOff = 0f;
        _logoVel = 0f;
        _audioActive = false;
        _beatPending = false;
        _bass = 0f;
        _level = 0f;
        _bassSm = 0f;
        _levelSm = 0f;
        _kick = 0f;

        _scrollPos = 0f;
        _newsCursor = 0;
        _t = 0f;
        BuildScroller(null);
    }

    public void OnData(string key, DataSnapshot snapshot)
    {
        if (key != "news" || snapshot is not NewsSnapshot ns) return;
        BuildScroller(ns.Items);
    }

    public void OnAudio(in AudioFrame audio)
    {
        if (!audio.Active)
        {
            _audioActive = false;
            _bass = 0f;
            _level = 0f;
            return;
        }
        _audioActive = true;
        _bass = Clamp01(audio.Bass);
        _level = Clamp01(audio.Level);
        _kick = Clamp01(audio.BeatPulse);
        if (audio.Beat) _beatPending = true;
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;
        dt = MathF.Min(dt, 0.1f);
        _t += dt;

        // Звук (или его имитация без музыки).
        float bassT = _audioActive ? _bass : 0.5f + 0.5f * MathF.Sin(_t * 2.2f);
        float levelT = _audioActive ? _level : 0.4f + 0.3f * MathF.Sin(_t * 0.37f);
        float k = MathF.Min(1f, dt * 8f);
        _bassSm += (bassT - _bassSm) * k;
        _levelSm += (levelT - _levelSm) * k;

        // Без звука ударов нет: только плавные LFO. Удары — только от настоящего AudioFrame.
        if (!_audioActive) _kick = 0f;
        if (_beatPending)
        {
            _beatPending = false;
            _logoVel -= _jumpSpeed;
        }

        StepLogo(dt);
        StepStars(dt);

        // Копперы и скроллер — ступеньками 50 Гц.
        int ticks = _clock.Advance(dt);
        if (ticks > 0)
        {
            _copTime = _clock.Time;
            UpdateBars();
            float step = _charsPerSec * _speed * (1f + _levelSm) / _clock.Hz;
            _scrollPos = (_scrollPos + ticks * step) % _bufLen;
        }
    }

    public void Draw(CharGrid grid)
    {
        _canvas.Clear(Rgba.Black);
        DrawStars();
        DrawBars();
        DrawLogo();
        _canvas.Present(grid);
        DrawScroller(grid);
    }

    // ----- состояние -----

    private void StepLogo(float dt)
    {
        int n = Math.Max(1, (int)MathF.Ceiling(dt * 120f));
        float h = dt / n;
        for (int i = 0; i < n; i++)
        {
            float acc = -SpringK * _logoOff - SpringC * _logoVel;
            _logoVel += acc * h;
            _logoOff += _logoVel * h;
        }
    }

    private void StepStars(float dt)
    {
        for (int i = 0; i < _starX.Length; i++)
        {
            float x = _starX[i] - PlaneSpeed[_starPlane[i]] * _speed * dt;
            if (x < 0f)
            {
                x = x % _w + _w;
                if (x >= _w) x = 0f;
                _starY[i] = _rng.Next(_h);
            }
            _starX[i] = x;
        }
    }

    /// <summary>Порядок отрисовки по фазе: дальние полосы раньше, ближние поверх (вставками, без аллокаций).</summary>
    private void UpdateBars()
    {
        float mid = _h * 0.5f, amp = _h * 0.32f;
        for (int i = 0; i < 6; i++)
        {
            float th = _copTime * (0.9f + 0.13f * i) + i * 0.95f;
            _barCenter[i] = mid + MathF.Sin(th) * amp;
            _barDepth[i] = MathF.Cos(th);
            _order[i] = i;
        }
        for (int i = 1; i < 6; i++)
        {
            int v = _order[i], j = i - 1;
            while (j >= 0 && _barDepth[_order[j]] > _barDepth[v])
            {
                _order[j + 1] = _order[j];
                j--;
            }
            _order[j + 1] = v;
        }
    }

    private void BuildScroller(IReadOnlyList<NewsItem>? items)
    {
        int count = items?.Count ?? 0;
        int slots = Math.Min(MaxNewsPerPass, count);
        int ni = 0;
        _bufLen = 0;
        for (int g = 0; g < Greetings.Length; g++)
        {
            Append(Greetings[g]);
            // Новости — со второго промежутка: заголовок не виден сразу, а въезжает в кадр через скролл.
            if (g >= 1 && ni < slots)
            {
                Append("... LATEST NEWS: ");
                AppendNews(items![(_newsCursor + ni) % count].Title);
                Append(" ... ");
                ni++;
            }
        }
        _newsCursor = count > 0 ? (_newsCursor + slots) % count : 0;
        if (_scrollPos >= _bufLen) _scrollPos = 0f;
    }

    private void Append(string s)
    {
        for (int i = 0; i < s.Length && _bufLen < BufCap; i++) _buf[_bufLen++] = s[i];
    }

    private void AppendNews(string? title)
    {
        // Порядок важен: сначала верхний регистр, потом приведение к глифам сетки.
        var t = GlyphSet.Sanitize((title ?? "").ToUpperInvariant()).Trim();
        int n = Math.Min(t.Length, MaxNewsChars);
        for (int i = 0; i < n && _bufLen < BufCap; i++) _buf[_bufLen++] = t[i];
    }

    // ----- отрисовка в холст -----

    private void DrawStars()
    {
        for (int i = 0; i < _starX.Length; i++)
            _canvas.Set((int)_starX[i], _starY[i], _planeColor[_starPlane[i]]);
    }

    private void DrawBars()
    {
        float kick = _kick;
        float thick = 1f + 0.6f * _bassSm;
        float mid = _h * 0.5f;
        for (int oi = 0; oi < 6; oi++)
        {
            int i = _order[oi];
            float center = mid + (_barCenter[i] - mid) * (1f - 0.6f * kick);
            float hgt = BarHeights[i] * thick * (1f - 0.4f * kick);
            int y0 = (int)MathF.Round(center - hgt * 0.5f);
            int hh = Math.Max(1, (int)MathF.Round(hgt));
            var bright = BarColors[i % BarColors.Length];
            var dark = bright.Scale(0.12f);
            for (int r = 0; r < hh; r++)
            {
                // Вертикальный градиент тёмный → яркий → тёмный, квантованный под Amiga.
                float u = (r + 0.5f) / hh;
                float tri = 1f - MathF.Abs(2f * u - 1f);
                var c = RetroPalettes.Amiga12(Rgba.Lerp(dark, bright, tri));
                _canvas.FillRect(0, y0 + r, _w, y0 + r + 1, c);
            }
        }
    }

    private void DrawLogo()
    {
        int sc = _logoScale;
        int lw = LogoUnitsW * sc, lh = GlyphH * sc;
        int ox = (int)MathF.Round(_w * 0.5f - lw * 0.5f + MathF.Sin(_t * 0.6f) * 2f);
        int oy = (int)MathF.Round(_h * 0.42f - lh * 0.5f + _logoOff + MathF.Sin(_t * 0.9f) * 1.5f);

        // Тень — смещение на 1 пиксель холста; промежутки между буквами — 1 пиксель шрифта.
        for (int ly = 0; ly < lh; ly++) DrawLogoRow(ox + 1, oy + 1 + ly, ly, _shadow);
        for (int ly = 0; ly < lh; ly++)
        {
            // «Металл»: вертикальный градиент, переливающийся по времени.
            var c = RetroPalettes.Amiga12(RetroPalettes.Gradient(Metal, ly / (float)lh + _t * 0.25f));
            DrawLogoRow(ox, oy + ly, ly, c);
        }
    }

    private void DrawLogoRow(int x0, int y, int ly, Rgba c)
    {
        if ((uint)y >= (uint)_h) return;
        int sc = _logoScale, uy = ly / sc;
        for (int ux = 0; ux < LogoUnitsW; ux++)
        {
            if (!LogoBit(ux, uy)) continue;
            int x = x0 + ux * sc;
            // Клип здесь: FillRect не защищает от x0 правее холста.
            if (x >= _w || x + sc <= 0) continue;
            _canvas.FillRect(Math.Max(0, x), y, Math.Min(_w, x + sc), y + 1, c);
        }
    }

    private static bool LogoBit(int ux, int uy)
    {
        int li = ux / 6, col = ux % 6;
        if (col == 5) return false;
        return ((Font[LetterIndex(LogoText[li])][uy] >> (4 - col)) & 1) != 0;
    }

    private static int LetterIndex(char c) => c switch
    {
        'P' => 0,
        'H' => 1,
        'O' => 2,
        'S' => 3,
        _ => 4, // R
    };

    private static int LogoScale(int w, int h)
    {
        int sx = (w - 8) / LogoUnitsW;
        int sy = (int)(h * 0.28f) / GlyphH;
        return Math.Clamp(Math.Min(sx, sy), 1, 3);
    }

    /// <summary>Синус-скроллер: символы сетки поверх Present, в зоне нижней трети.</summary>
    private void DrawScroller(CharGrid grid)
    {
        int cols = grid.Cols;
        int rowsEnd = Math.Min(_rows, grid.Rows);
        for (int y = _zoneTop; y < rowsEnd; y++)
            for (int x = 0; x < cols; x++)
                grid.Cells[y * cols + x] = new Cell(' ', Rgba.Black, Rgba.Black);

        int b = (int)_scrollPos;
        for (int x = 0; x < cols; x++)
        {
            char ch = _buf[(b + x) % _bufLen];
            if (ch == ' ') continue;
            // Длинная волна (~35 символов на период), фаза течёт по времени.
            int dy = (int)MathF.Round(MathF.Sin(x * 0.18f + _t * 3f) * _amp);
            var fg = RetroPalettes.Amiga12(RetroPalettes.Gradient(Rainbow, x / (float)cols + _t * 0.1f));
            grid.Put(x, _baseRow + dy, ch, fg, Rgba.Black);
        }
    }

    private static Rgba Q(uint hex) => RetroPalettes.Amiga12(Rgba.Hex(hex));

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
