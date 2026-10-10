using Phosphor.Core;
using Phosphor.Gfx;
using Phosphor.Text;
using Silk.NET.OpenGL;

namespace Phosphor.App;

/// <summary>Прямоугольник в пикселях цели; начало — левый верх (как на экране).</summary>
internal readonly record struct BadgeRect(float X, float Y, float W, float H);

/// <summary>
/// Геометрия бирки — чистые функции без GL: размещение по якорю и защита от выгорания (дрейф).
/// </summary>
internal static class BadgeGeometry
{
    private const double DriftAmp1080 = 6.0;    // амплитуда по каждой оси, px на 1080 строк
    private const double DriftPeriod = 120.0;   // новая цель каждые 120 с
    private const double DriftEase = 20.0;      // переход к цели за 20 с

    /// <summary>
    /// Прямоугольник бирки в пикселях цели W×H. anchor: tl..br (верх/середина/низ × лево/центр/право).
    /// size — высота, доля H; margin — отступ от края, доля H; ширина — по пропорции текстуры texW×texH.
    /// Неизвестный якорь — br.
    /// </summary>
    public static BadgeRect Place(string anchor, float size, float margin, int texW, int texH, int targetW, int targetH)
    {
        float tH = targetH;
        float h = size * tH;
        float w = texH > 0 ? h * texW / texH : h;
        float m = margin * tH;
        string a = (anchor ?? string.Empty).Trim().ToLowerInvariant();
        if (a.Length != 2 || "tmb".IndexOf(a[0]) < 0 || "lcr".IndexOf(a[1]) < 0) a = "br";
        float x = a[1] switch { 'l' => m, 'c' => (targetW - w) / 2f, _ => targetW - m - w };
        float y = a[0] switch { 't' => m, 'm' => (tH - h) / 2f, _ => tH - m - h };
        return new BadgeRect(x, y, w, h);
    }

    /// <summary>
    /// Сдвиг бирки (dx, dy), px, в момент t (накопленное время, с). Зависит только от индекса монитора и t —
    /// детерминированно для --shot. Цели по осям — в ±6·H/1080; каждые 120 с новая цель, плавный переход за 20 с.
    /// </summary>
    public static (float Dx, float Dy) DriftOffset(int monitorIndex, double t, int targetH)
    {
        double amp = DriftAmp1080 * targetH / 1080.0;
        long k = (long)Math.Floor(t / DriftPeriod);
        double u = t - k * DriftPeriod;
        var (tx, ty) = DriftTarget(monitorIndex, k, amp);
        if (u >= DriftEase) return ((float)tx, (float)ty);
        var (fx, fy) = DriftTarget(monitorIndex, k - 1, amp);
        double p = Math.Max(0.0, u / DriftEase);
        double s = p * p * (3 - 2 * p);   // smoothstep
        return ((float)(fx + (tx - fx) * s), (float)(fy + (ty - fy) * s));
    }

    /// <summary>Цель дрейфа номер k для монитора. До первого сегмента (k &lt; 0) — начало координат.</summary>
    private static (double X, double Y) DriftTarget(int monitorIndex, long k, double amp)
    {
        if (k < 0) return (0.0, 0.0);
        ulong h = Mix(((ulong)(uint)monitorIndex << 32) ^ (ulong)k);
        return (Unit(h) * amp, Unit(Mix(h)) * amp);
    }

    /// <summary>splitmix64: детерминированная «случайность» без rng и без аллокаций.</summary>
    private static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Хэш → [-1, 1).</summary>
    private static double Unit(ulong h) => (h >> 11) * (1.0 / 9007199254740992.0) * 2.0 - 1.0;
}

/// <summary>
/// Квадрат с премножённой текстурой: прямоугольник передаётся uniform-ом, непрозрачность — uniform, смешивание (One, OneMinusSrcAlpha).
/// Вершины — из gl_VertexID (4 вершины, triangle strip), VBO нет.
/// </summary>
internal sealed class BadgeQuad : IDisposable
{
    private const string Vert = """
        #version 330 core
        uniform vec4 uRect;     // x, y (от левого НИЗА), w, h — пиксели цели
        uniform vec2 uTarget;   // W, H цели
        out vec2 vUv;
        void main() {
            vec2 c = vec2(float(gl_VertexID & 1), float(gl_VertexID >> 1));
            vUv = c;
            vec2 p = uRect.xy + c * uRect.zw;
            gl_Position = vec4(p / uTarget * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string Frag = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColor;
        uniform sampler2D uTex;
        uniform float uOpacity;
        void main() {
            fragColor = texture(uTex, vUv) * uOpacity;
        }
        """;

    private readonly GL _gl;
    private readonly uint _prog, _vao;
    private readonly int _locRect, _locTarget, _locOpacity, _locTex;

    public BadgeQuad(GL gl)
    {
        _gl = gl;
        _prog = GlUtil.CreateProgram(gl, Vert, Frag, "badge");
        _vao = gl.GenVertexArray();
        _locRect = gl.GetUniformLocation(_prog, "uRect");
        _locTarget = gl.GetUniformLocation(_prog, "uTarget");
        _locOpacity = gl.GetUniformLocation(_prog, "uOpacity");
        _locTex = gl.GetUniformLocation(_prog, "uTex");
        gl.UseProgram(_prog);
        gl.Uniform1(_locTex, 0);
        gl.UseProgram(0);
    }

    /// <summary>
    /// Нарисовать tex в прямоугольник r (пиксели dst, сверху вниз) с непрозрачностью. blend=false — прямая копия.
    /// </summary>
    public void Draw(uint tex, RenderTarget dst, BadgeRect r, float opacity, bool blend)
    {
        dst.Bind();
        if (blend)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        }
        else _gl.Disable(EnableCap.Blend);
        _gl.UseProgram(_prog);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.Uniform4(_locRect, r.X, dst.Height - (r.Y + r.H), r.W, r.H);
        _gl.Uniform2(_locTarget, (float)dst.Width, (float)dst.Height);
        _gl.Uniform1(_locOpacity, opacity);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        _gl.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        if (_prog != 0) _gl.DeleteProgram(_prog);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
    }
}

/// <summary>
/// Одна бирка монитора (часы или логотип): текстура (текст сетки или PNG), размещение, дрейф, композит.
/// Текстовая бирка: CharGrid Cols×Rows → глифы шрифтом Placement.Font → обводка OsdOutline. Перерисовка — только при смене Version или размера.
/// Картиночная: PNG грузится один раз (premultiplied, мипмапы). Файла нет — одно предупреждение и текст из Draw.
/// Битый файл — одно предупреждение, бирка не рисуется.
/// </summary>
internal sealed class BadgeLayer : IDisposable
{
    private enum Mode { None, Text, Image }

    private static readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);

    private readonly Shared _s;
    private readonly IBadge _badge;
    private readonly int _index;
    private Mode _mode;
    private int _vpW, _vpH;
    private bool _dirty = true;
    private long _builtVersion = long.MinValue;
    private double _time;

    // Текст
    private CharGrid? _grid;
    private Font? _font;
    private int _fontCellH = -1;
    private string _fontFamily = string.Empty;
    private GridRenderer? _renderer;
    private RenderTarget? _glyphs, _outline;
    private OsdOutline? _stroke;
    private uint _textTex;
    private int _textW, _textH;

    // Картинка
    private uint _imageTex;
    private int _imageW, _imageH;

    /// <summary>Бирка рисуется поверх эффектов (Placement.Over).</summary>
    public bool Over => _badge.Placement.Over;

    public BadgeLayer(Shared s, IBadge badge, int index, int vpW, int vpH)
    {
        _s = s;
        _badge = badge;
        _index = index;
        _vpW = vpW; _vpH = vpH;
        (badge as IOverlayParams)?.Configure(s.Config.OverlayOptionsFor(badge.Id));

        bool textCapable = badge.Cols > 0 && badge.Rows > 0;
        string? path = badge.ImagePath;
        if (path is null)
        {
            // Логотип со своим PNG, которого нет на диске, сам переходит на текст — сообщаем один раз
            if (badge is Phosphor.Overlays.LogoBadge { FileMissing: true })
                WarnOnce("logo.file", $"бирка {badge.Id}: файла {s.Config.OverlayOptionsFor(badge.Id).GetString("file", "")} нет — показываю текст");
            _mode = textCapable ? Mode.Text : Mode.None;
        }
        else if (!File.Exists(path))
        {
            WarnOnce(path, $"бирка {badge.Id}: файла {path} нет — показываю текст");
            _mode = textCapable ? Mode.Text : Mode.None;
        }
        else if (TryLoadImage(path, out string? err))
        {
            _mode = Mode.Image;
        }
        else
        {
            WarnOnce(path, $"бирка {badge.Id}: не удалось прочитать картинку {path} ({err}) — бирка не рисуется");
            _mode = Mode.None;
        }
    }

    /// <summary>Размер вьюпорта слота. Смена размера — перерисовка текста (кегль зависит от высоты).</summary>
    public void Resize(int vpW, int vpH)
    {
        if (vpW == _vpW && vpH == _vpH) return;
        _vpW = vpW; _vpH = vpH;
        _dirty = true;
    }

    /// <summary>Шаг кадра: состояние бирки, время дрейфа, перерисовка текста — только если сменилась Version или размер.</summary>
    public void Tick(float dt, in OverlayContext ctx)
    {
        _badge.Update(dt, in ctx);
        _time += dt;
        if (_mode == Mode.Text && (_dirty || _badge.Version != _builtVersion)) BuildText();
    }

    /// <summary>
    /// Нарисовать бирку на dst (выход слоя или копия «поверх»). Координаты — в вьюпорте слота (vpW×vpH), dst может быть меньше — масштаб.
    /// Без готовой текстуры — ничего не делает.
    /// </summary>
    public void Compose(RenderTarget dst, BadgeQuad quad)
    {
        uint tex; int tw, th;
        if (_mode == Mode.Text && _textTex != 0) { tex = _textTex; tw = _textW; th = _textH; }
        else if (_mode == Mode.Image && _imageTex != 0) { tex = _imageTex; tw = _imageW; th = _imageH; }
        else return;

        var p = _badge.Placement;
        var r = BadgeGeometry.Place(p.Anchor, p.Size, p.Margin, tw, th, _vpW, _vpH);
        if (p.Drift)
        {
            var (dx, dy) = BadgeGeometry.DriftOffset(_index, _time, _vpH);
            r = r with { X = r.X + dx, Y = r.Y + dy };
        }
        float sx = (float)dst.Width / _vpW, sy = (float)dst.Height / _vpH;
        r = new BadgeRect(r.X * sx, r.Y * sy, r.W * sx, r.H * sy);
        quad.Draw(tex, dst, r, Math.Clamp(p.Opacity, 0f, 1f), blend: true);
    }

    private void BuildText()
    {
        var p = _badge.Placement;
        int cols = _badge.Cols, rows = _badge.Rows;
        // Кегль: высота клетки = Size·H / Rows, кламп 8..64 (растеризатор выше не умеет; крупнее — растянется при композите)
        int cellH = Math.Clamp((int)MathF.Round(p.Size * _vpH / rows), 8, 64);
        string family = p.Font ?? string.Empty;
        if (_font is null || _fontCellH != cellH || !string.Equals(_fontFamily, family, StringComparison.Ordinal))
        {
            _font = _s.FontFor(cellH, family, 64);
            _renderer?.Dispose();
            _renderer = new GridRenderer(_s.Gl, _font.Atlas);
            _fontCellH = cellH;
            _fontFamily = family;
        }

        if (_grid is null || _grid.Cols != cols || _grid.Rows != rows) _grid = new CharGrid(cols, rows);
        _grid.Clear(Rgba.Black);
        _badge.Draw(_grid);

        int cw = _font.Atlas.CellWidth, ch = _font.Atlas.CellHeight;
        int tw = cols * cw, th = rows * ch;
        _glyphs ??= new RenderTarget(_s.Gl, tw, th);
        _glyphs.Resize(tw, th);
        _renderer!.RenderGlyphs(_grid, _glyphs);

        float radius = p.Stroke * _vpH / 1080f;
        if (radius > 0f)
        {
            _outline ??= new RenderTarget(_s.Gl, tw, th);
            _outline.Resize(tw, th);
            _stroke ??= new OsdOutline(_s.Gl);
            _stroke.Stroke(_glyphs.Texture, _outline, radius);
            _textTex = _outline.Texture;
        }
        else _textTex = _glyphs.Texture;

        _textW = tw; _textH = th;
        _builtVersion = _badge.Version;
        _dirty = false;
    }

    /// <summary>PNG → premultiplied RGBA, строки снизу вверх (v=0 — низ), GL-текстура с мипмапами.</summary>
    private unsafe bool TryLoadImage(string path, out string? error)
    {
        error = null;
        try
        {
            using var bmp = new System.Drawing.Bitmap(path);
            int w = bmp.Width, h = bmp.Height;
            if (w <= 0 || h <= 0) { error = "пустая картинка"; return false; }
            using var argb = bmp.Clone(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bd = argb.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var px = new byte[w * h * 4];
            var row = new byte[w * 4];
            try
            {
                for (int y = 0; y < h; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, w * 4);
                    int dst = (h - 1 - y) * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        int i = x * 4;
                        int a = row[i + 3];
                        // BGRA → RGBA, премножение на альфу
                        px[dst + i] = (byte)((row[i + 2] * a + 127) / 255);
                        px[dst + i + 1] = (byte)((row[i + 1] * a + 127) / 255);
                        px[dst + i + 2] = (byte)((row[i] * a + 127) / 255);
                        px[dst + i + 3] = (byte)a;
                    }
                }
            }
            finally { argb.UnlockBits(bd); }

            var gl = _s.Gl;
            _imageTex = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, _imageTex);
            fixed (byte* p = px)
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, p);
            gl.GenerateMipmap(TextureTarget.Texture2D);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _imageW = w; _imageH = h;
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    private void WarnOnce(string key, string msg)
    {
        if (Warned.Add(key)) _s.Log.Warn(msg);
    }

    public void Dispose()
    {
        _renderer?.Dispose();
        _glyphs?.Dispose();
        _outline?.Dispose();
        _stroke?.Dispose();
        if (_imageTex != 0) _s.Gl.DeleteTexture(_imageTex);
        _imageTex = 0;
    }
}
