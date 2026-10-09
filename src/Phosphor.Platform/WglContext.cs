using System.Runtime.InteropServices;

namespace Phosphor.Platform;

/// <summary>Контекст OpenGL 3.3 core через WGL, привязанный к DC окна.</summary>
internal sealed class WglContext : IDisposable
{
    private const int WGL_CONTEXT_MAJOR_VERSION_ARB = 0x2091;
    private const int WGL_CONTEXT_MINOR_VERSION_ARB = 0x2092;
    private const int WGL_CONTEXT_PROFILE_MASK_ARB = 0x9126;
    private const int WGL_CONTEXT_CORE_PROFILE_BIT_ARB = 0x0001;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WglCreateContextAttribsARBDelegate(nint hdc, nint shareContext, int[] attribList);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int WglSwapIntervalEXTDelegate(int interval);

    private readonly nint _hwnd;
    private readonly nint _hdc;
    private nint _hglrc;
    private nint _opengl32;
    private WglSwapIntervalEXTDelegate? _swapInterval;
    private bool _disposed;

    private WglContext(nint hwnd, nint hdc)
    {
        _hwnd = hwnd;
        _hdc = hdc;
    }

    /// <summary>Создать контекст GL 3.3 core на окне. При ошибке все ресурсы освобождаются.</summary>
    public static WglContext Create(nint hwnd)
    {
        nint hdc = Win32.GetDC(hwnd);
        if (hdc == 0)
            throw new InvalidOperationException($"GetDC не вернул контекст устройства (ошибка {Marshal.GetLastWin32Error()}).");

        var ctx = new WglContext(hwnd, hdc);
        try
        {
            ctx.Initialize();
            return ctx;
        }
        catch
        {
            ctx.Dispose();
            throw;
        }
    }

    /// <summary>Адрес функции GL: сначала wglGetProcAddress, для core-функций 1.1 — из opengl32.dll.</summary>
    public nint GetProc(string name)
    {
        nint p = Win32.wglGetProcAddress(name);
        if (IsInvalidProc(p))
        {
            if (_opengl32 == 0)
                _opengl32 = Win32.LoadLibraryW("opengl32.dll");
            p = _opengl32 != 0 ? Win32.GetProcAddress(_opengl32, name) : 0;
        }
        return p;
    }

    public void MakeCurrent()
    {
        ThrowIfDisposed();
        if (!Win32.wglMakeCurrent(_hdc, _hglrc))
            throw new InvalidOperationException($"wglMakeCurrent не удался (ошибка {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>Установить интервал вертикальной синхронизации. Нет расширения — молча ничего.</summary>
    public void SetSwapInterval(int interval)
    {
        if (_swapInterval is null) return;
        _ = _swapInterval(interval);
    }

    public void SwapBuffers()
    {
        ThrowIfDisposed();
        if (!Win32.SwapBuffers(_hdc))
            throw new InvalidOperationException($"SwapBuffers не удался (ошибка {Marshal.GetLastWin32Error()}).");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hglrc != 0)
        {
            Win32.wglMakeCurrent(0, 0);
            Win32.wglDeleteContext(_hglrc);
            _hglrc = 0;
        }
        if (_hdc != 0)
        {
            Win32.ReleaseDC(_hwnd, _hdc);
        }
    }

    private void Initialize()
    {
        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
            nVersion = 1,
            dwFlags = Win32.PFD_DRAW_TO_WINDOW | Win32.PFD_SUPPORT_OPENGL | Win32.PFD_DOUBLEBUFFER,
            iPixelType = Win32.PFD_TYPE_RGBA,
            cColorBits = 32,
            cDepthBits = 24,
            cStencilBits = 8,
            iLayerType = Win32.PFD_MAIN_PLANE,
        };

        int format = Win32.ChoosePixelFormat(_hdc, ref pfd);
        if (format == 0)
            throw new InvalidOperationException($"ChoosePixelFormat не нашёл формат пикселей (ошибка {Marshal.GetLastWin32Error()}).");
        if (!Win32.SetPixelFormat(_hdc, format, ref pfd))
            throw new InvalidOperationException($"SetPixelFormat не удался (ошибка {Marshal.GetLastWin32Error()}).");

        // Временный legacy-контекст нужен только для получения wglCreateContextAttribsARB.
        nint temp = Win32.wglCreateContext(_hdc);
        if (temp == 0)
            throw new InvalidOperationException($"wglCreateContext (временный) не удался (ошибка {Marshal.GetLastWin32Error()}).");

        try
        {
            if (!Win32.wglMakeCurrent(_hdc, temp))
                throw new InvalidOperationException($"wglMakeCurrent (временный) не удался (ошибка {Marshal.GetLastWin32Error()}).");

            nint createPtr = Win32.wglGetProcAddress("wglCreateContextAttribsARB");
            if (IsInvalidProc(createPtr))
                throw new InvalidOperationException("wglCreateContextAttribsARB недоступна: драйвер не даёт создать контекст OpenGL 3.3 core.");

            var createAttribs = Marshal.GetDelegateForFunctionPointer<WglCreateContextAttribsARBDelegate>(createPtr);
            int[] attribs =
            {
                WGL_CONTEXT_MAJOR_VERSION_ARB, 3,
                WGL_CONTEXT_MINOR_VERSION_ARB, 3,
                WGL_CONTEXT_PROFILE_MASK_ARB, WGL_CONTEXT_CORE_PROFILE_BIT_ARB,
                0,
            };
            nint core = createAttribs(_hdc, 0, attribs);
            if (core == 0)
                throw new InvalidOperationException($"wglCreateContextAttribsARB вернул 0 для OpenGL 3.3 core (ошибка {Marshal.GetLastWin32Error()}).");
            _hglrc = core;

            Win32.wglMakeCurrent(0, 0);
            Win32.wglDeleteContext(temp);
            temp = 0;

            MakeCurrent();

            nint swapPtr = Win32.wglGetProcAddress("wglSwapIntervalEXT");
            if (!IsInvalidProc(swapPtr))
                _swapInterval = Marshal.GetDelegateForFunctionPointer<WglSwapIntervalEXTDelegate>(swapPtr);
        }
        finally
        {
            if (temp != 0)
            {
                Win32.wglMakeCurrent(0, 0);
                Win32.wglDeleteContext(temp);
            }
        }
    }

    private static bool IsInvalidProc(nint p) => p == 0 || p == 1 || p == 2 || p == 3 || p == -1;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
