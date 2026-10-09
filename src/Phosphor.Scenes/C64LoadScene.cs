using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// Загрузка игры на Commodore 64: BASIC V2, набор LOAD"*",8,1, SEARCHING/LOADING, полосы в рамке,
/// READY. / RUN и титульник выдуманной игры, затем цикл заново. Текстовый режим: сетка символов напрямую.
/// Экран 40×25 — 1 символ = 1 ячейка, если сетка достаточна; иначе экран масштабируется по сетке.
/// Со звуком: удар даёт вспышку полос в рамке; во время загрузки полосы синхронны с ударом.
/// </summary>
public sealed class C64LoadScene : IScene, IAudioReactive, IPreferredRows
{
    /// <summary>25 строк экрана плюс по 3 строки рамки сверху и снизу: экран занимает ~80% высоты.</summary>
    public int PreferredRows => 31;

    private const float Hz = 50f;
    private const float TickDt = 1f / 50f;
    private const int ScrCols = 40, ScrRows = 25;
    private const byte Border = 14;         // светло-голубой (рамка по умолчанию)
    private const byte Text = 14;           // светло-голубой текст
    private const byte Blue = 6;            // синий экран
    private const float BootTime = 2f;      // с, экран включения
    private const float EnterTime = 0.5f;   // с, после Enter: SEARCHING FOR *
    private const float ReadyTime = 1f;     // с, READY. / RUN перед титульником
    private const float TitleTime = 10f;    // с, титульник до цикла
    private const float FlashTime = 0.15f;  // с, вспышка полос после удара
    private const float LoadMin = 8f, LoadRange = 7f;
    private const string Command = "LOAD\"*\",8,1";
    private const string Header0 = "    **** COMMODORE 64 BASIC V2 ****";
    private const string Header1 = " 64K RAM SYSTEM  38911 BASIC BYTES FREE";

    // Цвета полос при вспышке: белый, жёлтый, светло-красный, светло-зелёный.
    private static readonly byte[] FlashPal = { 1, 7, 10, 13 };

    private static readonly string[][] Titles =
    {
        new[] { "VOID", "RUNNER" },
        new[] { "NEON", "GHOST" },
        new[] { "PIXEL", "KNIGHT" },
        new[] { "MOON BASE", "7" },
        new[] { "SKY", "PIRATES" },
        new[] { "BLOCK", "HUNTER" },
    };

    private enum St { Boot, Typing, Enter, Loading, Ready, Title }

    private Random _rng = new(0);
    private float _speed = 1f;
    private int _cols, _rows;
    private RetroClock _clock;
    private byte[] _rowCol = Array.Empty<byte>();   // цвет рамки по строкам (полосы)

    private St _st;
    private float _stT;
    private int _typed;
    private float _keyT;
    private float _loadDur;
    private int _game;
    private float _flashT;
    private int _bandTicks;
    private bool _active, _beatPending;

    // Экран 40×25 в буфере: символ, цвет текста, цвет фона (индексы палитры C64).
    private readonly char[] _sch = new char[ScrCols * ScrRows];
    private readonly byte[] _sfg = new byte[ScrCols * ScrRows];
    private readonly byte[] _sbg = new byte[ScrCols * ScrRows];

    public string Id => "c64load";

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _rng = rng;
        _speed = MathF.Max(0f, options.Speed);
        _cols = cols;
        _rows = rows;
        _clock = new RetroClock(Hz);
        _rowCol = new byte[rows];
        Array.Fill(_rowCol, Border);
        _game = rng.Next(Titles.Length);
        _flashT = 0f;
        _bandTicks = 0;
        _active = false;
        _beatPending = false;
        GoTo(St.Boot);
    }

    public void Update(float dt)
    {
        int n = _clock.Advance(Math.Max(0f, dt) * _speed);
        for (int i = 0; i < n; i++) Tick();
    }

    public void OnAudio(in AudioFrame audio)
    {
        _active = audio.Active && audio.Bpm > 0f;
        if (_active && audio.Beat) _beatPending = true;
    }

    public void Draw(CharGrid grid)
    {
        BuildScreen();
        Layout(grid.Cols, grid.Rows, out int sx, out int sy, out int sw, out int sh);
        // Рамка заливает все строки сетки: если сетка выше, чем при Init, расширяем буфер полос (редко).
        if (_rowCol.Length < grid.Rows)
        {
            var wider = new byte[grid.Rows];
            Array.Fill(wider, Border);
            _rowCol = wider;
        }
        int rows = grid.Rows;
        for (int y = 0; y < rows; y++)
        {
            Rgba border = RetroPalettes.C64[_rowCol[y]];
            for (int x = 0; x < grid.Cols; x++)
            {
                int lx = x - sx, ly = y - sy;
                ref var cell = ref grid.Cells[y * grid.Cols + x];
                if ((uint)lx < (uint)sw && (uint)ly < (uint)sh)
                {
                    int i = (ly * ScrRows / sh) * ScrCols + lx * ScrCols / sw;
                    cell = new Cell(_sch[i], RetroPalettes.C64[_sfg[i]], RetroPalettes.C64[_sbg[i]]);
                }
                else
                {
                    cell = new Cell(' ', border, border);
                }
            }
        }
    }

    private void Tick()
    {
        _stT += TickDt;
        switch (_st)
        {
            case St.Boot:
                if (_stT >= BootTime) GoTo(St.Typing);
                break;
            case St.Typing:
                _keyT -= TickDt;
                if (_keyT <= 0f)
                {
                    _typed++;
                    if (_typed >= Command.Length) GoTo(St.Enter);
                    else _keyT = KeyDelay();
                }
                break;
            case St.Enter:
                if (_stT >= EnterTime) GoTo(St.Loading);
                break;
            case St.Loading:
                if (_stT >= _loadDur) GoTo(St.Ready);
                break;
            case St.Ready:
                if (_stT >= ReadyTime) GoTo(St.Title);
                break;
            case St.Title:
                if (_stT >= TitleTime)
                {
                    _game = (_game + 1) % Titles.Length;
                    GoTo(St.Boot);
                }
                break;
        }
        UpdateBands();
    }

    private void GoTo(St st)
    {
        _st = st;
        _stT = 0f;
        switch (st)
        {
            case St.Typing:
                _typed = 0;
                _keyT = KeyDelay();
                break;
            case St.Loading:
                _loadDur = LoadMin + _rng.NextSingle() * LoadRange;
                _bandTicks = 0;
                break;
        }
    }

    /// <summary>Пауза между нажатиями: живой темп, иногда набирающий задумывается.</summary>
    private float KeyDelay()
    {
        float d = 0.1f + _rng.NextSingle() * 0.22f;
        if (_rng.Next(4) == 0) d += 0.25f + _rng.NextSingle() * 0.3f;
        return d;
    }

    private void UpdateBands()
    {
        if (_beatPending)
        {
            _beatPending = false;
            _flashT = FlashTime;
        }
        if (_flashT > 0f)
        {
            _flashT -= TickDt;
            Regen(true);
            return;
        }
        if (_st == St.Loading)
        {
            if (--_bandTicks <= 0)
            {
                Regen(false);
                _bandTicks = 1 + _rng.Next(3);   // полосы меняются каждые 1–3 такта
            }
        }
        else
        {
            Array.Fill(_rowCol, Border);
        }
    }

    /// <summary>Полосы случайной высоты (1–4 строки) и цвета из палитры C64.</summary>
    private void Regen(bool flash)
    {
        int y = 0;
        while (y < _rowCol.Length)
        {
            int h = 1 + _rng.Next(4);
            byte c = flash ? FlashPal[_rng.Next(FlashPal.Length)] : (byte)_rng.Next(16);
            for (int k = 0; k < h && y < _rowCol.Length; k++) _rowCol[y++] = c;
        }
    }

    /// <summary>Раскладка экрана 40×25 в сетку: 1:1 по центру, если сетка достаточна; иначе — масштаб с рамкой 1.</summary>
    private static void Layout(int cols, int rows, out int sx, out int sy, out int sw, out int sh)
    {
        if (cols >= ScrCols && rows >= ScrRows)
        {
            sw = ScrCols;
            sh = ScrRows;
        }
        else
        {
            sw = cols > 2 ? cols - 2 : cols;
            sh = rows > 2 ? rows - 2 : rows;
        }
        sx = (cols - sw) / 2;
        sy = (rows - sh) / 2;
    }

    /// <summary>Собрать экран 40×25 из состояния. Чистая функция состояния: Draw не меняет его.</summary>
    private void BuildScreen()
    {
        Array.Fill(_sch, ' ');
        Array.Fill(_sfg, Text);
        Array.Fill(_sbg, Blue);

        if (_st == St.Title)
        {
            DrawTitle(Titles[_game]);
            return;
        }

        PutText(0, 0, Header0);
        PutText(1, 0, Header1);
        PutText(2, 0, "READY.");

        bool cursorOn = (_clock.Ticks * 2 / 25) % 2 == 0;   // ~2 Гц
        switch (_st)
        {
            case St.Boot:
                if (cursorOn) PutCursor(0, 3);
                break;
            case St.Typing:
                PutText(3, 0, Command.Substring(0, _typed));
                if (cursorOn) PutCursor(_typed, 3);
                break;
            default:
                PutText(3, 0, Command);
                break;
        }

        if (_st is St.Enter or St.Loading or St.Ready) PutText(4, 0, "SEARCHING FOR *");
        if (_st is St.Loading or St.Ready) PutText(5, 0, "LOADING");
        if (_st == St.Ready)
        {
            PutText(6, 0, "READY.");
            PutText(7, 0, "RUN");
        }
    }

    private void DrawTitle(string[] lines)
    {
        PutBlock(3, lines[0]);
        if (lines.Length > 1) PutBlock(9, lines[1]);
        PutCentered(16, "(C) 1986 NOVAVISION");
        PutCentered(19, "PRESS FIRE");
    }

    private void PutCursor(int col, int row)
    {
        if (col < 0 || col >= ScrCols || row < 0 || row >= ScrRows) return;
        int i = row * ScrCols + col;
        _sch[i] = '█';
        _sfg[i] = Text;
        _sbg[i] = Blue;
    }

    private void PutText(int row, int col, string s)
    {
        for (int k = 0; k < s.Length; k++)
        {
            int x = col + k;
            if ((uint)x >= ScrCols || (uint)row >= ScrRows) continue;
            _sch[row * ScrCols + x] = s[k];
        }
    }

    private void PutCentered(int row, string s) => PutText(row, Math.Max(0, (ScrCols - s.Length) / 2), s);

    /// <summary>Крупные буквы из блоков '█' (шрифт 3×5, между буквами 1 пробел).</summary>
    private void PutBlock(int row, string s)
    {
        int w = s.Length * 4 - 1;
        int x0 = Math.Max(0, (ScrCols - w) / 2);
        for (int k = 0; k < s.Length; k++)
        {
            var g = Glyph(s[k]);
            int ox = x0 + k * 4;
            for (int r = 0; r < 5; r++)
                for (int c = 0; c < 3; c++)
                {
                    int x = ox + c, y = row + r;
                    if ((uint)x >= ScrCols || (uint)y >= ScrRows) continue;
                    if (g[r][c] == '█') _sch[y * ScrCols + x] = '█';
                }
        }
    }

    // Шрифт для титульников: только буквы из названий, блоки '█'. Пробел — пустая ячейка.
    private static readonly string[] Blank = { "   ", "   ", "   ", "   ", "   " };
    private static readonly string[] GlyphA = { " █ ", "█ █", "███", "█ █", "█ █" };
    private static readonly string[] GlyphB = { "██ ", "█ █", "██ ", "█ █", "██ " };
    private static readonly string[] GlyphC = { " ██", "█  ", "█  ", "█  ", " ██" };
    private static readonly string[] GlyphD = { "██ ", "█ █", "█ █", "█ █", "██ " };
    private static readonly string[] GlyphE = { "███", "█  ", "██ ", "█  ", "███" };
    private static readonly string[] GlyphG = { " ██", "█  ", "█ █", "█ █", " ██" };
    private static readonly string[] GlyphH = { "█ █", "█ █", "███", "█ █", "█ █" };
    private static readonly string[] GlyphI = { "███", " █ ", " █ ", " █ ", "███" };
    private static readonly string[] GlyphK = { "█ █", "██ ", "█  ", "██ ", "█ █" };
    private static readonly string[] GlyphL = { "█  ", "█  ", "█  ", "█  ", "███" };
    private static readonly string[] GlyphM = { "█ █", "███", "███", "█ █", "█ █" };
    private static readonly string[] GlyphN = { "██ ", "█ █", "█ █", "█ █", "█ █" };
    private static readonly string[] GlyphO = { "███", "█ █", "█ █", "█ █", "███" };
    private static readonly string[] GlyphP = { "██ ", "█ █", "██ ", "█  ", "█  " };
    private static readonly string[] GlyphR = { "██ ", "█ █", "██ ", "█ █", "█ █" };
    private static readonly string[] GlyphS = { " ██", "█  ", " █ ", "  █", "██ " };
    private static readonly string[] GlyphT = { "███", " █ ", " █ ", " █ ", " █ " };
    private static readonly string[] GlyphU = { "█ █", "█ █", "█ █", "█ █", "███" };
    private static readonly string[] GlyphV = { "█ █", "█ █", "█ █", "█ █", " █ " };
    private static readonly string[] GlyphX = { "█ █", "█ █", " █ ", "█ █", "█ █" };
    private static readonly string[] GlyphY = { "█ █", "█ █", " █ ", " █ ", " █ " };
    private static readonly string[] Glyph7 = { "███", "  █", "  █", " █ ", " █ " };

    private static string[] Glyph(char c) => c switch
    {
        'A' => GlyphA, 'B' => GlyphB, 'C' => GlyphC, 'D' => GlyphD, 'E' => GlyphE,
        'G' => GlyphG, 'H' => GlyphH, 'I' => GlyphI, 'K' => GlyphK, 'L' => GlyphL,
        'M' => GlyphM, 'N' => GlyphN, 'O' => GlyphO, 'P' => GlyphP, 'R' => GlyphR,
        'S' => GlyphS, 'T' => GlyphT, 'U' => GlyphU, 'V' => GlyphV, 'X' => GlyphX,
        'Y' => GlyphY, '7' => Glyph7,
        _ => Blank,
    };
}
