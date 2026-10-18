using Phosphor.Core;
using Phosphor.Gfx;
using Phosphor.Platform;
using Silk.NET.OpenGL;

namespace Phosphor.Filters.Tests;

/// <summary>
/// Настоящий GPU в тестах: скрытое окно с GL 3.3, входная картинка из массива float RGB → фильтр → пиксели обратно.
/// Контекст привязан к потоку: создавай харнесс внутри теста (using var gpu = new GpuHarness(w, h)).
/// Координаты массивов: row-major, строка 0 — ВЕРХ картинки (как на экране); переворот в GL-низ делает харнесс.
/// </summary>
public sealed unsafe class GpuHarness : IDisposable
{
    private readonly ScreensaverWindow _win;
    public GL Gl => _win.Gl;
    public int Width { get; }
    public int Height { get; }
    private readonly uint _input;
    /// <summary>Входная текстура (для прогона FilterChain).</summary>
    public uint InputTexture => _input;
    private readonly RenderTarget _output;

    public GpuHarness(int width, int height)
    {
        Width = width; Height = height;
        _win = ScreensaverWindow.CreateHidden(64, 64);
        _input = Gl.GenTexture();
        Gl.BindTexture(TextureTarget.Texture2D, _input);
        Gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.Float, null);
        Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _output = new RenderTarget(Gl, width, height);
    }

    /// <summary>Залить вход: rgb[y, x] → (r,g,b), строка 0 — верх.</summary>
    public void SetInput(Func<int, int, (float r, float g, float b)> rgb)
    {
        var data = new float[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            var (r, g, b) = rgb(x, y);
            int i = ((Height - 1 - y) * Width + x) * 4;
            data[i] = r; data[i + 1] = g; data[i + 2] = b; data[i + 3] = 1;
        }
        Gl.BindTexture(TextureTarget.Texture2D, _input);
        fixed (float* p = data)
            Gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)Width, (uint)Height, PixelFormat.Rgba, PixelType.Float, p);
    }

    /// <summary>Создать и инициализировать фильтр под размер харнесса.</summary>
    public T Create<T>() where T : IFilter, new()
    {
        var f = new T();
        f.Init(Gl, Width, Height);
        return f;
    }

    /// <summary>
    /// Прогнать кадр frame (время = frame/60) с параметрами по умолчанию из filter.Params плюс overrides
    /// (для Options — индекс, как в шейдере). Возвращает выход: [y][x] → rgb, строка 0 — верх.
    /// </summary>
    public float[,,] Run(IFilter filter, long frame, IReadOnlyDictionary<string, float>? overrides = null)
    {
        var p = new FilterParams();
        foreach (var s in filter.Params) p[s.Name] = s.Default;
        if (overrides is not null) foreach (var (k, v) in overrides) p[k] = v;
        var fi = new FrameInfo(frame / 60f, 1f / 60f, frame, Width, Height, 0, AudioFrame.Silent);
        filter.Apply(_input, _output, fi, p);
        Gl.Finish();
        return Read();
    }

    /// <summary>Выход последнего Run (float, без клампа сверху).</summary>
    public float[,,] Read()
    {
        var data = new float[Width * Height * 4];
        Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _output.Fbo);
        Gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
        fixed (float* p = data)
            Gl.ReadPixels(0, 0, (uint)Width, (uint)Height, PixelFormat.Rgba, PixelType.Float, p);
        var err = Gl.GetError();
        if (err != GLEnum.NoError) throw new InvalidOperationException($"GL error: {err}");
        var img = new float[Height, Width, 3];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int i = ((Height - 1 - y) * Width + x) * 4;
            img[y, x, 0] = data[i]; img[y, x, 1] = data[i + 1]; img[y, x, 2] = data[i + 2];
        }
        return img;
    }

    public static float Luma(float[,,] img, int x, int y) => 0.299f * img[y, x, 0] + 0.587f * img[y, x, 1] + 0.114f * img[y, x, 2];

    public void Dispose()
    {
        _output.Dispose();
        Gl.DeleteTexture(_input);
        _win.Dispose();
    }
}
