using System.Globalization;
using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// «BBS»: дозвон модемом на вымышленную доску объявлений. ATZ/OK, ATDT с шумом рукопожатия в строке состояния,
/// CONNECT, ANSI-заставка из блоков (16 цветов CGA, шрифт PixelFont), потом «прогулка» по доске: листинги файловых
/// областей, письмо от SYSOP построчно, дверь. Прогулка идёт до истечения sessionMax, затем G → NO CARRIER и цикл заново.
/// Экран — виртуальный терминал 80 колонок × высота сетки (25 строк), по центру сетки. Вся печать идёт со скоростью
/// модема (baud/10 символов в секунду). Сцена без сети и без звука.
/// </summary>
public sealed class BbsScene : IScene, ITextScene, ISceneParams, IPreferredRows
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec BaudSpec = new("baud", SceneParamKind.Choice, 0f, 0f, 3f, "скорость модема, бит/с",
        new[] { "2400", "9600", "14400", "28800" });
    public static readonly SceneParamSpec SessionSpec = new("sessionMax", SceneParamKind.Int, 120f, 30f, 300f, "макс. длительность сеанса, с");
    private static readonly SceneParamSpec[] ParamList = { BaudSpec, SessionSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    public string Id => "bbs";

    /// <summary>Настоящая BBS: 25 строк, как экран терминала.</summary>
    public int PreferredRows => 25;

    /// <summary>Ширина виртуального экрана, колонок.</summary>
    private const int ScreenCols = 80;

    // CGA, 16 цветов ANSI.
    private static readonly Rgba[] Cga =
    {
        Rgba.Hex(0x000000), Rgba.Hex(0x0000AA), Rgba.Hex(0x00AA00), Rgba.Hex(0x00AAAA),
        Rgba.Hex(0xAA0000), Rgba.Hex(0xAA00AA), Rgba.Hex(0xAA5500), Rgba.Hex(0xAAAAAA),
        Rgba.Hex(0x555555), Rgba.Hex(0x5555FF), Rgba.Hex(0x55FF55), Rgba.Hex(0x55FFFF),
        Rgba.Hex(0xFF5555), Rgba.Hex(0xFF55FF), Rgba.Hex(0xFFFF55), Rgba.Hex(0xFFFFFF),
    };

    private static readonly Rgba Black = Cga[0], Gray = Cga[7], DarkGray = Cga[8], LBlue = Cga[9],
        LGreen = Cga[10], LCyan = Cga[11], LRed = Cga[12], LMagenta = Cga[13], Yellow = Cga[14], White = Cga[15];

    private static readonly Rgba[] LetterColors = { LCyan, LMagenta, Yellow, LGreen, LRed };

    private static readonly Cell Blank = new(' ', White, Black);

    // Имена только из латиницы: PixelFont не содержит кириллицы.
    private static readonly string[] Names = { "PHOSPHOR BBS", "NIGHT OWL", "DEMOSCENA", "ZX UNDERGROUND" };
    private const string StatText = "ЗВОНКОВ СЕГОДНЯ: 0042, ВРЕМЯ: 60 МИН";
    private const string NoiseLabel = "ХЭНДШЕЙК V.42 ";
    private const string NoiseChars = "░▒▓";

    private static readonly string[] MenuKeys = { "F", "M", "D", "G" };
    private static readonly string[] MenuLabels = { "ФАЙЛЫ", "ПОЧТА", "ДВЕРИ", "ВЫХОД" };
    private const string FilesHeader = "ИМЯ          РАЗМЕР  ДАТА        ОПИСАНИЕ";

    /// <summary>Файловые области: заголовок и строки листинга. Выбирается случайно при каждом заходе в «Файлы».</summary>
    private static readonly string[] AreaTitles =
    {
        " ФАЙЛЫ - ОБЛАСТЬ 3 - DEMO",
        " ФАЙЛЫ - ОБЛАСТЬ 7 - GAMES",
        " ФАЙЛЫ - ОБЛАСТЬ 1 - UTILS",
    };
    private static readonly string[][] AreaLines =
    {
        new[]
        {
            "DEMO93.ZIP    412K  12.09.93  Second Reality - Future Crew",
            "TETRIS.ARJ     38K  02.03.91  Тетрис на двоих",
            "PHOSPH.ZIP    120K  30.07.95  Осциллограф и фигуры Лиссажу",
            "BBSLIST.TXT   4K    01.01.94  Список доск, версия 1994",
            "FIRE.COM      2K    14.02.93  Огонь 320x200, ASM",
            "SNDBLST.ZIP  212K  07.07.96  Сэмплы для SB16",
            "README.NFO    2K    09.09.94  Прочти меня, сынок",
            "DEMOS.LST     9K    21.06.92  Каталог демо-групп",
            "SCENE94.ZIP  380K  03.04.94  Тринадцать минут чёрного экрана",
            "MUSICDSK.LZH  96K  18.10.93  Трекерная музыка, 8 каналов",
            "GREETZ.TXT    3K    11.11.93  Приветы группам",
            "TOP10.ZIP     54K  01.01.95  Топ-10 демо года",
        },
        new[]
        {
            "SNAKE.ZIP     18K  05.05.92  Змейка, 2 игрока",
            "DOOM-SHR.ZIP 1.2M  22.12.94  Шароварный выпуск",
            "LEMMINGS.ZIP 640K  10.10.92  Спасайте леммингов",
            "PACMAN.ARJ    24K  19.08.91  Жёлтый и привидения",
            "CHESS.ZIP     44K  14.03.93  Шахматы, уровни 1-9",
            "PONG.COM       2K  07.07.90  Пинг-понг, без пауз",
            "BREAKOUT.ZIP  16K  30.11.92  Кирпичики",
            "SOKOBAN.ZIP   20K  25.02.94  Двигай ящики",
            "ЗМЕЙКА.ZIP    11K  01.04.93  Русская змейка",
        },
        new[]
        {
            "PKZIP204.EXE  55K  14.09.92  Архиватор, версия 2.04",
            "ARJ241.EXE    78K  03.02.93  Архиватор ARJ",
            "LHA213.EXE    40K  28.06.92  Архиватор LHA",
            "FDISK.TXT      6K  11.01.91  Как разбить диск",
            "DIR2.COM       3K  09.09.90  Каталог с сортировкой",
            "EDIT.EXE      90K  17.05.93  Текстовый редактор",
            "VIRUS.DOC      4K  02.02.92  Не запускайте это",
            "ANTIVIR.ZIP   66K  19.12.93  Проверка на вирусы",
            "TREE.COM       2K  23.04.91  Дерево каталогов",
        },
    };

    /// <summary>Письмо от SYSOP: выводится построчно, с паузой на чтение.</summary>
    private static readonly string[] MailLines =
    {
        "ОТ: SYSOP              ТЕМА: ДОБРО ПОЖАЛОВАТЬ",
        "",
        "ПРИВЕТ, ПОЛЬЗОВАТЕЛЬ!",
        "ЗАЛ ОБЪЯВЛЕНИЙ ОТКРЫТ, ВХОД СВОБОДНЫЙ.",
        "ЗАВТРА В 22:00 СТАРТ ДЕМО-КОМПО.",
        "ПРИНОСИ СВОИ РАБОТЫ НА ДИСКЕТЕ 3.5,",
        "АРХИВЫ ZIP, НЕ ARJ - ПРОВЕРЬ СВОЙ ЗИП.",
        "",
        "ЕСЛИ НАШЁЛ БАГ В ДВЕРИ - ПИШИ В ПОЧТУ.",
        "ЗВОНИ ЧАЩЕ. ЛИНИЯ 3 НЕ ДЕРЖИТСЯ ДОЛГО.",
        "",
        "- SYSOP",
    };

    /// <summary>Паузы, с (при speed=1). Дозвон — случайная.</summary>
    private const float ModemPause = 0.6f, DialMin = 2.5f, DialRange = 1.5f, ConnectPause = 0.8f,
        LogoHold = 5f, MenuPause = 1.5f, KeyPause = 0.4f, FilesHold = 6f, MailLinePause = 1.2f,
        DoorHold = 3f, HangupHold = 3f;
    private const float NoiseStep = 0.05f;

    /// <summary>Этапы сеанса по порядку. Logo..Door — прогулка, которую обрывает sessionMax.</summary>
    private enum Stage { Modem, Dial, Connect, Logo, Menu, Choose, Files, Mail, Door, KeyG, Hangup }

    private Random _rng = new(0);
    private float _speed = 1f;
    private int _cols, _rows, _cx, _cy;
    private string _baudText = "2400";
    private float _cps = 240f;
    private int _sessionMax = 120;
    private string _logoName = Names[0];

    private Stage _stage, _next;
    private bool _firstWalk;
    private int _areaIdx, _mailIdx;
    private float _waitT, _acc, _sessT, _clock, _noiseT;

    // Виртуальный экран (_cols × _rows) и поток символов текущего шага, который печатается со скоростью модема.
    private Cell[] _scr = Array.Empty<Cell>();
    private Cell[] _stream = Array.Empty<Cell>();
    private int _sLen, _sPos;
    private char[] _noise = Array.Empty<char>();

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = Math.Max(1, Math.Min(ScreenCols, cols));
        _rows = Math.Max(1, rows);
        _rng = rng;
        _speed = MathF.Max(0.05f, options.Speed);
        _sessionMax = Math.Clamp(options.GetInt(SessionSpec.Name, (int)SessionSpec.Default), (int)SessionSpec.Min, (int)SessionSpec.Max);

        var opts = BaudSpec.Options!;
        int bi = Array.IndexOf(opts, options.GetString(BaudSpec.Name, opts[(int)BaudSpec.Default]));
        if (bi < 0) bi = (int)BaudSpec.Default;
        _baudText = opts[bi];
        _cps = int.Parse(_baudText, CultureInfo.InvariantCulture) / 10f;

        _scr = new Cell[_cols * _rows];
        _stream = new Cell[Math.Max(4096, _cols * _rows * 2)];
        _noise = new char[_cols];
        for (int i = 0; i < _noise.Length; i++) _noise[i] = NoiseChars[rng.Next(NoiseChars.Length)];

        _clock = 0f;
        _noiseT = 0f;
        _sessT = 0f;
        _acc = 0f;
        _firstWalk = true;
        _areaIdx = 0;
        _mailIdx = 0;
        _logoName = Names[0];
        ClearScreen();
        Enter(Stage.Modem);
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;
        _clock += dt;

        if (_stage == Stage.Dial)
        {
            _noiseT += dt;
            while (_noiseT >= NoiseStep)
            {
                _noiseT -= NoiseStep;
                ShiftNoise();
            }
        }

        if (_stage >= Stage.Connect && _stage < Stage.Hangup) _sessT += dt;

        // sessionMax истёк во время прогулки: пользователь жмёт G.
        if (_stage >= Stage.Logo && _stage <= Stage.Door && _sessT >= _sessionMax)
        {
            Enter(Stage.KeyG);
            return;
        }

        if (_sPos < _sLen)
        {
            PrintChars(dt);
            return;
        }

        _waitT -= dt;
        if (_waitT <= 0f) Next();
    }

    public void Draw(CharGrid grid)
    {
        grid.Clear(Black);

        // Виртуальный экран по центру сетки.
        int ox = Math.Max(0, (grid.Cols - _cols) / 2);
        int oy = Math.Max(0, (grid.Rows - _rows) / 2);
        int w = Math.Min(grid.Cols - ox, _cols), h = Math.Min(grid.Rows - oy, _rows);
        for (int y = 0; y < h; y++) Array.Copy(_scr, y * _cols, grid.Cells, (oy + y) * grid.Cols + ox, w);

        // Строка состояния «звука модема» на последней строке экрана, пока дозвон.
        if (_stage == Stage.Dial)
        {
            int y = oy + _rows - 1;
            grid.Text(ox, y, NoiseLabel, LCyan, Black);
            for (int x = NoiseLabel.Length; x < _cols; x++)
            {
                char ch = _noise[x];
                grid.Put(ox + x, y, ch, ch == '▓' ? White : ch == '▒' ? Gray : DarkGray, Black);
            }
        }
        else if (_clock % 1f < 0.5f)
        {
            // Курсор терминала.
            grid.Put(ox + Math.Min(_cx, _cols - 1), oy + _cy, '█', Gray, Black);
        }
    }

    // ----- шаги сеанса -----

    private float Pause(float sec) => sec / _speed;

    private void Next()
    {
        switch (_stage)
        {
            case Stage.Modem: Enter(Stage.Dial); break;
            case Stage.Dial: Enter(Stage.Connect); break;
            case Stage.Connect: Enter(Stage.Logo); break;
            case Stage.Logo: Enter(Stage.Menu); break;
            case Stage.Menu: Enter(Stage.Choose); break;
            case Stage.Choose: Enter(_next); break;
            case Stage.Files: Enter(Stage.Menu); break;
            case Stage.Mail: Enter(_mailIdx < MailLines.Length ? Stage.Mail : Stage.Menu); break;
            case Stage.Door: Enter(Stage.Menu); break;
            case Stage.KeyG: Enter(Stage.Hangup); break;
            default: Enter(Stage.Modem); break;
        }
    }

    private void Enter(Stage st)
    {
        _stage = st;
        _sLen = 0;
        _sPos = 0;
        _acc = 0f;
        if (st == Stage.Dial) _noiseT = 0f;

        switch (st)
        {
            case Stage.Modem:
                ClearScreen();
                Emit("ATZ", Gray); EmitLn();
                Emit("OK", Gray); EmitLn();
                _waitT = Pause(ModemPause);
                break;

            case Stage.Dial:
                Emit("ATDT 8-095-", Gray);
                EmitDigits(3); Emit('-', Gray);
                EmitDigits(2); Emit('-', Gray);
                EmitDigits(2); EmitLn();
                _waitT = Pause(DialMin + _rng.NextSingle() * DialRange);
                break;

            case Stage.Connect:
                _sessT = 0f;
                _firstWalk = true;
                Emit("CONNECT ", LGreen); Emit(_baudText, LGreen); Emit("/ARQ/V42BIS", LGreen);
                EmitLn(); EmitLn();
                _waitT = Pause(ConnectPause);
                break;

            case Stage.Logo:
                ClearScreen();
                EmitLn();
                _logoName = Names[_rng.Next(Names.Length)];
                EmitLogo(_logoName);
                _waitT = Pause(LogoHold);
                break;

            case Stage.Menu:
                EmitLn();
                EmitMenu();
                EmitLn(); EmitLn();
                Emit("ВЫБОР: ", Yellow);
                _waitT = Pause(MenuPause);
                break;

            case Stage.Choose:
                // Первый заход после логотипа — всегда файлы, потом случайно.
                if (_firstWalk)
                {
                    _next = Stage.Files;
                    _firstWalk = false;
                }
                else
                {
                    int k = _rng.Next(3);
                    _next = k == 0 ? Stage.Files : k == 1 ? Stage.Mail : Stage.Door;
                }
                if (_next == Stage.Mail) _mailIdx = 0;
                Emit(_next == Stage.Files ? 'F' : _next == Stage.Mail ? 'M' : 'D', Yellow);
                EmitLn();
                _waitT = Pause(KeyPause);
                break;

            case Stage.Files:
                // Без очистки экрана: листинги подряд уезжают вверх, как в терминале (прокрутка).
                _areaIdx = _rng.Next(AreaTitles.Length);
                Emit(AreaTitles[_areaIdx], LCyan); EmitLn(); EmitLn();
                Emit(FilesHeader, Yellow); EmitLn();
                var lines = AreaLines[_areaIdx];
                for (int i = 0; i < lines.Length; i++) { Emit(lines[i], Gray); EmitLn(); }
                EmitLn();
                _waitT = Pause(FilesHold);
                break;

            case Stage.Mail:
                if (_mailIdx == 0) ClearScreen();
                Emit(MailLines[_mailIdx], Gray); EmitLn();
                _mailIdx++;
                _waitT = Pause(MailLinePause);
                break;

            case Stage.Door:
                ClearScreen();
                Emit("LOADING DOOR...", Yellow); EmitLn();
                Emit('[', LCyan);
                for (int i = 0; i < 40; i++) Emit('░', LCyan);
                Emit(']', LCyan); EmitLn();
                _waitT = Pause(DoorHold);
                break;

            case Stage.KeyG:
                Emit('G', Yellow);
                EmitLn();
                _waitT = Pause(KeyPause);
                break;

            case Stage.Hangup:
                EmitLn();
                Emit("NO CARRIER", LRed); EmitLn();
                _waitT = Pause(HangupHold);
                break;
        }
    }

    private void PrintChars(float dt)
    {
        _acc += dt * _cps * _speed;
        int n = (int)_acc;
        _acc -= n;
        while (n-- > 0 && _sPos < _sLen) Put(_stream[_sPos++]);
        if (_sPos >= _sLen) _acc = 0f;
    }

    private void ShiftNoise()
    {
        Array.Copy(_noise, 1, _noise, 0, _cols - 1);
        _noise[_cols - 1] = NoiseChars[_rng.Next(NoiseChars.Length)];
    }

    // ----- содержимое экранов -----

    /// <summary>
    /// Логотип из PixelFont: буквы 5×7 на секстантах, 3 строки клеток, каждая буква своим CGA-цветом.
    /// Рамка двойной линией и полоса статистики — как на настоящей доске.
    /// </summary>
    private void EmitLogo(string name)
    {
        int lw = name.Length * PixelFont.Advance - 1;
        int inner = lw + 6;
        int pad = Math.Max(0, (_cols - inner - 2) / 2);

        EmitSpaces(pad, Gray); Emit('╔', LBlue); EmitRepeat('═', inner, LBlue); Emit('╗', LBlue); EmitLn();
        EmitFrameRow(pad, inner);
        for (int r = 0; r < PixelFont.CellsH; r++)
        {
            EmitSpaces(pad, Gray); Emit('║', LBlue);
            EmitSpaces(3, Gray);
            EmitLogoRow(name, lw, r);
            EmitSpaces(3, Gray); Emit('║', LBlue); EmitLn();
        }
        EmitFrameRow(pad, inner);
        EmitSpaces(pad, Gray); Emit('╚', LBlue); EmitRepeat('═', inner, LBlue); Emit('╝', LBlue); EmitLn();
        EmitLn();

        int sin = StatText.Length + 4;
        int spad = Math.Max(0, (_cols - sin - 2) / 2);
        EmitSpaces(spad, Gray); Emit('╔', LBlue); EmitRepeat('═', sin, LBlue); Emit('╗', LBlue); EmitLn();
        EmitSpaces(spad, Gray); Emit('║', LBlue); EmitSpaces(2, Gray); Emit(StatText, Yellow); EmitSpaces(2, Gray); Emit('║', LBlue); EmitLn();
        EmitSpaces(spad, Gray); Emit('╚', LBlue); EmitRepeat('═', sin, LBlue); Emit('╝', LBlue); EmitLn();
    }

    private void EmitFrameRow(int pad, int inner)
    {
        EmitSpaces(pad, Gray); Emit('║', LBlue);
        EmitSpaces(inner, Gray);
        Emit('║', LBlue); EmitLn();
    }

    /// <summary>Одна строка клеток логотипа: секстант каждой клетки, цвет — цвет буквы.</summary>
    private void EmitLogoRow(string name, int lw, int row)
    {
        for (int cx = 0; cx < lw; cx++)
        {
            int li = cx / PixelFont.Advance, within = cx % PixelFont.Advance;
            if (li >= name.Length || within >= PixelFont.CellsW)
            {
                Emit(' ', Gray);
                continue;
            }
            int mask = PixelFont.Bits(name[li])[row * PixelFont.CellsW + within];
            Emit(mask == 0 ? ' ' : GlyphSet.Sextant(mask), LetterColors[li % LetterColors.Length]);
        }
    }

    private void EmitMenu()
    {
        Emit("  ", Gray);
        for (int i = 0; i < MenuKeys.Length; i++)
        {
            Emit('[', LCyan); Emit(MenuKeys[i], Yellow); Emit("] ", LCyan);
            Emit(MenuLabels[i], Gray);
            Emit("  ", Gray);
        }
    }

    // ----- терминал -----

    private void ClearScreen()
    {
        Array.Fill(_scr, Blank);
        _cx = 0;
        _cy = 0;
    }

    private void Put(Cell c)
    {
        if (c.Ch == '\n')
        {
            NewLine();
            return;
        }
        if (_cx >= _cols) NewLine();
        _scr[_cy * _cols + _cx] = c;
        _cx++;
    }

    private void NewLine()
    {
        _cx = 0;
        if (_cy + 1 >= _rows)
        {
            Array.Copy(_scr, _cols, _scr, 0, _cols * (_rows - 1));
            Array.Fill(_scr, Blank, _cols * (_rows - 1), _cols);
        }
        else _cy++;
    }

    private void Emit(char ch, Rgba fg)
    {
        if (_sLen < _stream.Length) _stream[_sLen++] = new Cell(ch, fg, Black);
    }

    private void Emit(string s, Rgba fg)
    {
        foreach (char c in s) Emit(c, fg);
    }

    private void EmitSpaces(int n, Rgba fg)
    {
        for (int i = 0; i < n; i++) Emit(' ', fg);
    }

    private void EmitRepeat(char c, int n, Rgba fg)
    {
        for (int i = 0; i < n; i++) Emit(c, fg);
    }

    private void EmitDigits(int n)
    {
        for (int i = 0; i < n; i++) Emit((char)('0' + _rng.Next(10)), Gray);
    }

    private void EmitLn() => Emit('\n', Black);
}
