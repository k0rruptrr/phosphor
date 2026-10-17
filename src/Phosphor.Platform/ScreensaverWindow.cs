using System.ComponentModel;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace Phosphor.Platform;

/// <summary>Команда от стрелок в заставке и оконном режиме: сцена или пресет (фильтры).</summary>
public enum UserCommand { NextScene, PrevScene, NextPreset, PrevPreset }

/// <summary>Что делать с клавишей: Close — выйти; PassThrough — отдать Windows (громкость, медиа); Command — команда.</summary>
public enum KeyAction { Close, PassThrough, Command }

/// <summary>
/// Окно Win32 с контекстом OpenGL 3.3 core. Чистый Win32 + WGL; Silk.NET используется только для объекта GL.
/// Режимы: полноэкранный (/s), превью (/p), оконный (/w), скрытый (--shot).
/// </summary>
public sealed class ScreensaverWindow : IDisposable
{
    private enum Kind { Fullscreen, Preview, Windowed, Hidden }

    private const string ClassName = "PhosphorScreensaverWindow";
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
    private const long MouseGraceMs = 500;

    // Делегат WndProc хранится в static поле, иначе GC соберёт его и процесс упадёт.
    private static readonly WndProcDelegate s_wndProc = WndProc;
    private static readonly Dictionary<nint, ScreensaverWindow> s_windows = new();
    private static bool s_classRegistered;

    private readonly Kind _kind;
    private readonly nint _parent;
    private readonly int _mouseThreshold;
    private readonly bool _ignoreMouseMove;
    private readonly bool _ignoreMediaKeys;
    private readonly long _createdTick;
    // Команды от стрелок; читает главный поток (PumpEvents и WndProc в одном потоке). Ограничен, чтобы зажатая клавиша не копила очередь.
    private readonly Queue<UserCommand> _commands = new();
    private const int MaxQueuedCommands = 32;
    private nint _hwnd;
    private WglContext? _wgl;
    private GL? _gl;
    private int _clientWidth;
    private int _clientHeight;
    private bool _hasMousePos;
    private int _mouseX;
    private int _mouseY;
    private bool _disposed;

    private ScreensaverWindow(Kind kind, nint hwnd, nint parent, int mouseThreshold, bool ignoreMouseMove, bool ignoreMediaKeys)
    {
        _kind = kind;
        _hwnd = hwnd;
        _parent = parent;
        _mouseThreshold = mouseThreshold;
        _ignoreMouseMove = ignoreMouseMove;
        _ignoreMediaKeys = ignoreMediaKeys;
        _createdTick = Environment.TickCount64;
    }

    /// <summary>
    /// Классификация клавиши. fullscreen/windowed — режим окна (превью и скрытое окно — оба false: всё отдать Windows).
    /// Стрелки — команды в обоих режимах. Медиа/громкость: в /s при ignoreMediaKeys — отдать Windows, иначе выход.
    /// В /s любая другая клавиша — выход; в /w выход только по Esc.
    /// </summary>
    public static KeyAction Classify(bool fullscreen, bool windowed, nint vk, bool ignoreMediaKeys, out UserCommand command)
    {
        command = default;
        if (!fullscreen && !windowed) return KeyAction.PassThrough;

        switch (vk)
        {
            case Win32.VK_RIGHT: command = UserCommand.NextScene; return KeyAction.Command;
            case Win32.VK_LEFT: command = UserCommand.PrevScene; return KeyAction.Command;
            case Win32.VK_UP: command = UserCommand.NextPreset; return KeyAction.Command;
            case Win32.VK_DOWN: command = UserCommand.PrevPreset; return KeyAction.Command;
        }

        if (vk >= Win32.VK_VOLUME_MUTE && vk <= Win32.VK_MEDIA_PLAY_PAUSE)
        {
            if (!fullscreen) return KeyAction.PassThrough;
            return ignoreMediaKeys ? KeyAction.PassThrough : KeyAction.Close;
        }

        if (fullscreen) return KeyAction.Close;
        return vk == Win32.VK_ESCAPE ? KeyAction.Close : KeyAction.PassThrough;
    }

    /// <summary>Полноэкранный режим /s на весь виртуальный экран. Закрытие по вводу; порог — пикселей смещения мыши.
    /// ignoreMouseMove — движение мыши не закрывает (клики закрывают); ignoreMediaKeys — медиаклавиши и громкость не закрывают.</summary>
    public static ScreensaverWindow CreateFullscreen(int mouseThresholdPx, bool ignoreMouseMove, bool ignoreMediaKeys)
    {
        if (mouseThresholdPx < 0)
            throw new ArgumentOutOfRangeException(nameof(mouseThresholdPx), "Порог не может быть отрицательным.");

        MonitorRect vs = Monitors.VirtualScreen();
        return Create(Kind.Fullscreen, Win32.WS_POPUP | Win32.WS_VISIBLE, Win32.WS_EX_TOPMOST,
            vs.X, vs.Y, vs.Width, vs.Height, 0, "PHOSPHOR", mouseThresholdPx, ignoreMouseMove, ignoreMediaKeys);
    }

    /// <summary>Забрать следующую команду от стрелок (по одной за вызов). false — очередь пуста.</summary>
    public bool TryTakeCommand(out UserCommand command)
    {
        if (_commands.Count > 0)
        {
            command = _commands.Dequeue();
            return true;
        }
        command = default;
        return false;
    }

    /// <summary>Превью /p: дочернее окно внутри parentHwnd, размер — клиентская область родителя.</summary>
    public static ScreensaverWindow CreatePreview(nint parentHwnd)
    {
        if (parentHwnd == 0 || !Win32.IsWindow(parentHwnd))
            throw new ArgumentException("Родительское окно недействительно.", nameof(parentHwnd));
        if (!Win32.GetClientRect(parentHwnd, out RECT rc))
            throw new InvalidOperationException($"GetClientRect родителя не удался (ошибка {Marshal.GetLastWin32Error()}).");

        return Create(Kind.Preview, Win32.WS_CHILD | Win32.WS_VISIBLE, 0,
            0, 0, rc.Right - rc.Left, rc.Bottom - rc.Top, parentHwnd, "", 0);
    }

    /// <summary>Оконный режим /w: клиентская область ровно width×height, по центру основного монитора.</summary>
    public static ScreensaverWindow CreateWindowed(int width, int height, string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        var rc = new RECT { Left = 0, Top = 0, Right = width, Bottom = height };
        if (!Win32.AdjustWindowRectEx(ref rc, Win32.WS_OVERLAPPEDWINDOW, false, 0))
            throw new InvalidOperationException($"AdjustWindowRectEx не удался (ошибка {Marshal.GetLastWin32Error()}).");
        int ww = rc.Right - rc.Left;
        int wh = rc.Bottom - rc.Top;

        IReadOnlyList<MonitorRect> monitors = Monitors.Enumerate();
        MonitorRect mon = monitors.Count > 0 ? monitors[0] : new MonitorRect(0, 0, 1920, 1080, true, string.Empty);
        int x = mon.X + (mon.Width - ww) / 2;
        int y = mon.Y + (mon.Height - wh) / 2;

        return Create(Kind.Windowed, Win32.WS_OVERLAPPEDWINDOW | Win32.WS_VISIBLE, 0,
            x, y, ww, wh, 0, title, 0);
    }

    /// <summary>Скрытое окно для --shot: WS_POPUP без WS_VISIBLE, без событий закрытия.</summary>
    public static ScreensaverWindow CreateHidden(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        return Create(Kind.Hidden, Win32.WS_POPUP, 0, 0, 0, width, height, 0, "PHOSPHOR-shot", 0);
    }

    public GL Gl => _gl ?? throw new InvalidOperationException("Контекст OpenGL не создан или уже освобождён.");

    public nint Hwnd => _hwnd;

    /// <summary>Текущая ширина клиентской области, физические пиксели (обновляется по WM_SIZE).</summary>
    public int ClientWidth => _clientWidth;

    /// <summary>Текущая высота клиентской области, физические пиксели (обновляется по WM_SIZE).</summary>
    public int ClientHeight => _clientHeight;

    /// <summary>Экранная координата X левого-верхнего угла клиентской области.</summary>
    public int OriginX => ClientOrigin().X;

    /// <summary>Экранная координата Y левого-верхнего угла клиентской области.</summary>
    public int OriginY => ClientOrigin().Y;

    public bool ShouldClose { get; private set; }

    /// <summary>Неблокирующая обработка очереди сообщений потока.</summary>
    public void PumpEvents()
    {
        if (_disposed) return;

        if (_kind == Kind.Preview && !Win32.IsWindow(_parent))
            ShouldClose = true;

        while (Win32.PeekMessageW(out MSG msg, 0, 0, 0, Win32.PM_REMOVE))
        {
            if (msg.message == Win32.WM_QUIT)
            {
                ShouldClose = true;
                continue;
            }
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);
        }
    }

    public void SwapBuffers()
    {
        ThrowIfDisposed();
        _wgl!.SwapBuffers();
    }

    /// <summary>Вертикальная синхронизация. Нет расширения wglSwapIntervalEXT — ничего не делаем.</summary>
    public void SetVSync(bool on)
    {
        ThrowIfDisposed();
        _wgl!.SetSwapInterval(on ? 1 : 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        (_gl as IDisposable)?.Dispose();
        _gl = null;

        _wgl?.Dispose();
        _wgl = null;

        if (_hwnd != 0)
        {
            s_windows.Remove(_hwnd);
            Win32.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
        ShouldClose = true;
    }

    private static ScreensaverWindow Create(Kind kind, uint style, uint exStyle,
        int x, int y, int width, int height, nint parent, string title, int mouseThreshold,
        bool ignoreMouseMove = false, bool ignoreMediaKeys = false)
    {
        Dpi.EnsurePerMonitorAware();
        EnsureClassRegistered();

        nint hInstance = Win32.GetModuleHandleW(null);
        nint hwnd = Win32.CreateWindowExW(exStyle, ClassName, title, style,
            x, y, width, height, parent, 0, hInstance, 0);
        if (hwnd == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW не удался.");

        var window = new ScreensaverWindow(kind, hwnd, parent, mouseThreshold, ignoreMouseMove, ignoreMediaKeys);
        s_windows[hwnd] = window;
        try
        {
            window.Initialize();
            return window;
        }
        catch
        {
            window.Dispose();
            throw;
        }
    }

    private void Initialize()
    {
        if (!Win32.GetClientRect(_hwnd, out RECT rc))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetClientRect не удался.");
        _clientWidth = rc.Right - rc.Left;
        _clientHeight = rc.Bottom - rc.Top;

        _wgl = WglContext.Create(_hwnd);
        _wgl.MakeCurrent();

        WglContext wgl = _wgl;
        Func<string, nint> loader = name => wgl.GetProc(name);
        _gl = GL.GetApi(loader);

        SetVSync(true);
    }

    private static void EnsureClassRegistered()
    {
        if (s_classRegistered) return;

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = Win32.CS_OWNDC | Win32.CS_HREDRAW | Win32.CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
            hInstance = Win32.GetModuleHandleW(null),
            hbrBackground = 0,
            lpszClassName = ClassName,
        };
        if (Win32.RegisterClassExW(ref wc) == 0)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != ERROR_CLASS_ALREADY_EXISTS)
                throw new Win32Exception(err, "RegisterClassExW не удался.");
        }
        s_classRegistered = true;
    }

    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (s_windows.TryGetValue(hwnd, out ScreensaverWindow? window))
            return window.HandleMessage(msg, wParam, lParam);
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private nint HandleMessage(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Win32.WM_ERASEBKGND:
                return 1;

            case Win32.WM_SIZE:
                _clientWidth = Win32.LoWord(lParam);
                _clientHeight = Win32.HiWord(lParam);
                break;

            case Win32.WM_DESTROY:
                ShouldClose = true;
                break;

            case Win32.WM_CLOSE:
                if (_kind is Kind.Fullscreen or Kind.Windowed)
                {
                    ShouldClose = true;
                    return 0;
                }
                break;

            case Win32.WM_KEYDOWN:
            case Win32.WM_SYSKEYDOWN:
                switch (Classify(_kind == Kind.Fullscreen, _kind == Kind.Windowed, wParam, _ignoreMediaKeys, out UserCommand userCmd))
                {
                    case KeyAction.Close:
                        ShouldClose = true;
                        break;
                    case KeyAction.Command:
                        if (_commands.Count < MaxQueuedCommands) _commands.Enqueue(userCmd);
                        break;
                    // PassThrough: ниже — DefWindowProcW, Windows сама меняет громкость и шлёт медиаклавиши
                }
                break;

            case Win32.WM_LBUTTONDOWN:
            case Win32.WM_RBUTTONDOWN:
            case Win32.WM_MBUTTONDOWN:
                if (_kind == Kind.Fullscreen)
                    ShouldClose = true;
                break;

            case Win32.WM_ACTIVATEAPP:
                if (_kind == Kind.Fullscreen && wParam == 0)
                    ShouldClose = true;
                break;

            case Win32.WM_MOUSEMOVE:
                if (_kind == Kind.Fullscreen && !_ignoreMouseMove)
                    OnFullscreenMouseMove(lParam);
                break;

            case Win32.WM_SETCURSOR:
                if (_kind == Kind.Fullscreen)
                {
                    Win32.SetCursor(0);
                    return 1;
                }
                break;

            case Win32.WM_SYSCOMMAND:
                if (_kind == Kind.Fullscreen)
                {
                    uint cmd = unchecked((uint)(long)wParam) & Win32.SC_MASK;
                    if (cmd == Win32.SC_SCREENSAVE || cmd == Win32.SC_MONITORPOWER)
                        return 0;
                }
                break;
        }

        return Win32.DefWindowProcW(_hwnd, msg, wParam, lParam);
    }

    private void OnFullscreenMouseMove(nint lParam)
    {
        // Первые 500 мс после создания Windows присылает фантомные WM_MOUSEMOVE — игнорируем полностью.
        if (Environment.TickCount64 - _createdTick < MouseGraceMs)
            return;

        int x = Win32.SignedLoWord(lParam);
        int y = Win32.SignedHiWord(lParam);
        if (!_hasMousePos)
        {
            _mouseX = x;
            _mouseY = y;
            _hasMousePos = true;
            return;
        }

        long dx = x - _mouseX;
        long dy = y - _mouseY;
        long limit = (long)_mouseThreshold * _mouseThreshold;
        if (dx * dx + dy * dy > limit)
            ShouldClose = true;
    }

    private POINT ClientOrigin()
    {
        var p = new POINT { X = 0, Y = 0 };
        if (_hwnd != 0)
            Win32.ClientToScreen(_hwnd, ref p);
        return p;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
