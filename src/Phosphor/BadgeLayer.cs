using Phosphor.Core;
using Phosphor.Gfx;
using Phosphor.Overlays;
using Phosphor.Text;
using Silk.NET.OpenGL;

namespace Phosphor.App;

/// <summary>Прямоугольник в пикселях цели; начало — левый верх (как на экране).</summary>
internal readonly record struct BadgeRect(float X, float Y, float W, float H);

/// <summary>
/// Геометрия бирки — чистые функции без GL: размещение по якорю, поля холста под эффекты и защита от выгорания (дрейф).
/// Эффекты считаются в texel содержимого: радиусы и смещения — доля высоты содержимого th.
/// </summary>
internal static class BadgeGeometry
{
    private const double DriftAmp1080 = 6.0;    // амплитуда по каждой оси, px на 1080 строк
    private const double DriftPeriod = 120.0;   // новая цель каждые 120 с
    private const double DriftEase = 20.0;      // переход к цели за 20 с

    public const float PlateInset = 0.15f;      // поле подложки, доля высоты содержимого
    public const float ShadowOffset = 0.04f;    // смещение тени вниз-вправо, доля высоты
    public const float ShadowBlur = 0.06f;      // радиус размытия тени, доля высоты
    public const float GlowBlur = 0.12f;        // радиус размытия свечения, доля высоты

    /// <summary>
    /// Пиксели экрана → texel содержимого: screenH — высота бирки на экране, texH — высота текстуры.
    /// Масштаб текстуры = screenH / texH (пикселей экрана на texel); радиус в texel = screenPx · texH / screenH.
    /// </summary>
    public static float ToTexel(float screenPx, float screenH, int texH) =>
        screenH > 0f ? screenPx * texH / screenH : screenPx;

    /// <summary>Подложка включена (rounded или pill).</summary>
    public static bool IsPlate(string? plate) => plate is "rounded" or "pill";

    /// <summary>
    /// Прямоугольник бирки в пикселях цели W×H. anchor: tl..br (верх/середина/низ × лево/центр/право).
    /// size — высота содержимого, доля H; margin — отступ от края, доля H; ширина — по пропорции текстуры texW×texH.
    /// Неизвестный якорь — br.
    /// </summary>
    public static BadgeRect Place(string anchor, float size, float margin, int texW, int texH, int targetW, int targetH)
    {
        float h = size * targetH;
        float w = texH > 0 ? h * texW / texH : h;
        return PlaceBox(anchor, w, h, margin, targetW, targetH);
    }

    /// <summary>Размещение коробки w×h (пиксели цели) по якорю; отступ margin·H от краёв. Неизвестный якорь — br.</summary>
    public static BadgeRect PlaceBox(string anchor, float w, float h, float margin, int targetW, int targetH)
    {
        float tH = targetH;
        float m = margin * tH;
        string a = (anchor ?? string.Empty).Trim().ToLowerInvariant();
        if (a.Length != 2 || "tmb".IndexOf(a[0]) < 0 || "lcr".IndexOf(a[1]) < 0) a = "br";
        float x = a[1] switch { 'l' => m, 'c' => (targetW - w) / 2f, _ => targetW - m - w };
        float y = a[0] switch { 't' => m, 'm' => (tH - h) / 2f, _ => tH - m - h };
        return new BadgeRect(x, y, w, h);
    }

    /// <summary>
    /// Поле холста текстуры (texel) с каждой стороны: вмещает подложку, тень, свечение и обводку.
    /// 0, если ничего из этого не включено. th — высота содержимого в texel, strokePx — радиус обводки.
    /// </summary>
    public static int CanvasPad(int th, bool plate, float shadow, float glow, float strokePx)
    {
        float e = 0f;
        if (plate) e = MathF.Max(e, PlateInset * th);
        if (shadow > 0f) e = MathF.Max(e, (ShadowOffset + ShadowBlur) * th);
        if (glow > 0f) e = MathF.Max(e, GlowBlur * th);
        if (strokePx > 0f) e = MathF.Max(e, strokePx);
        return e <= 0f ? 0 : (int)MathF.Ceiling(e) + 2;
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
/// Вершины — из gl_VertexID (4 вершины, triangle strip), VBO нет. tint — силуэт: rgb = цвет × альфа.
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
        uniform int uTintOn;
        uniform vec3 uTint;
        void main() {
            vec4 c = texture(uTex, vUv);
            if (uTintOn == 1) c = vec4(uTint * c.a, c.a);
            fragColor = c * uOpacity;
        }
        """;

    private readonly GL _gl;
    private readonly uint _prog, _vao;
    private readonly int _locRect, _locTarget, _locOpacity, _locTex, _locTintOn, _locTint;

    public BadgeQuad(GL gl)
    {
        _gl = gl;
        _prog = GlUtil.CreateProgram(gl, Vert, Frag, "badge");
        _vao = gl.GenVertexArray();
        _locRect = gl.GetUniformLocation(_prog, "uRect");
        _locTarget = gl.GetUniformLocation(_prog, "uTarget");
        _locOpacity = gl.GetUniformLocation(_prog, "uOpacity");
        _locTex = gl.GetUniformLocation(_prog, "uTex");
        _locTintOn = gl.GetUniformLocation(_prog, "uTintOn");
        _locTint = gl.GetUniformLocation(_prog, "uTint");
        gl.UseProgram(_prog);
        gl.Uniform1(_locTex, 0);
        gl.UseProgram(0);
    }

    /// <summary>
    /// Нарисовать tex в прямоугольник r (пиксели dst, сверху вниз) с непрозрачностью. blend=false — прямая копия.
    /// tint — силуэт этим цветом (0..1), без него текстура как есть.
    /// </summary>
    public void Draw(uint tex, RenderTarget dst, BadgeRect r, float opacity, bool blend, (float R, float G, float B)? tint = null)
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
        _gl.Uniform1(_locTintOn, tint is null ? 0 : 1);
        if (tint is { } t) _gl.Uniform3(_locTint, t.R, t.G, t.B);
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
/// GL-проходы эффектов бирки (всё premultiplied): подложка (SDF-скругление), сепарабельное размытие (9 тапов),
/// тень (чёрная, смещение), свечение (screen). Вызывается только при перерисовке бирки.
/// </summary>
internal sealed class BadgeFx : IDisposable
{
    private const string PlateFrag = """
        #version 330 core
        out vec4 fragColor;
        uniform vec4 uBox;      // x0, y0, x1, y1 — пиксели холста (от левого низа)
        uniform float uRadius;
        uniform vec3 uColor;
        uniform float uAlpha;
        void main() {
            vec2 c = (uBox.xy + uBox.zw) * 0.5;
            vec2 hs = (uBox.zw - uBox.xy) * 0.5;
            vec2 q = abs(gl_FragCoord.xy - c) - hs + uRadius;
            float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - uRadius;
            float a = clamp(0.5 - d, 0.0, 1.0) * uAlpha;
            fragColor = vec4(uColor * a, a);
        }
        """;

    // Гаусс σ≈2 тапа, 9 отсчётов: шаг uStep (uv), по осям — отдельными проходами H и V
    private const string BlurFrag = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColor;
        uniform sampler2D uSrc;
        uniform vec2 uStep;
        void main() {
            vec4 s = texture(uSrc, vUv) * 0.2042;
            s += (texture(uSrc, vUv + uStep) + texture(uSrc, vUv - uStep)) * 0.1802;
            s += (texture(uSrc, vUv + 2.0 * uStep) + texture(uSrc, vUv - 2.0 * uStep)) * 0.1238;
            s += (texture(uSrc, vUv + 3.0 * uStep) + texture(uSrc, vUv - 3.0 * uStep)) * 0.0663;
            s += (texture(uSrc, vUv + 4.0 * uStep) + texture(uSrc, vUv - 4.0 * uStep)) * 0.0276;
            fragColor = s;
        }
        """;

    private const string ShadowFrag = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColor;
        uniform sampler2D uSrc;   // размытая альфа содержимого
        uniform vec2 uOff;        // смещение тени, uv (тень сдвинута на −uOff в выборке)
        uniform float uStrength;
        void main() {
            float a = clamp(texture(uSrc, vUv - uOff).a * uStrength, 0.0, 1.0);
            fragColor = vec4(0.0, 0.0, 0.0, a);
        }
        """;

    private const string GlowFrag = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColor;
        uniform sampler2D uSrc;   // размытая альфа содержимого
        uniform vec3 uColor;
        uniform float uStrength;
        void main() {
            float k = clamp(texture(uSrc, vUv).a * uStrength, 0.0, 1.0);
            fragColor = vec4(uColor * k, k);
        }
        """;

    private readonly GL _gl;
    private readonly uint _plate, _blur, _shadow, _glow;
    private readonly int _pBox, _pRadius, _pColor, _pAlpha, _bStep, _sOff, _sStr, _gColor, _gStr;

    public BadgeFx(GL gl)
    {
        _gl = gl;
        _plate = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex, PlateFrag, "badge-plate");
        _blur = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex, BlurFrag, "badge-blur");
        _shadow = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex, ShadowFrag, "badge-shadow");
        _glow = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex, GlowFrag, "badge-glow");
        _pBox = gl.GetUniformLocation(_plate, "uBox");
        _pRadius = gl.GetUniformLocation(_plate, "uRadius");
        _pColor = gl.GetUniformLocation(_plate, "uColor");
        _pAlpha = gl.GetUniformLocation(_plate, "uAlpha");
        _bStep = gl.GetUniformLocation(_blur, "uStep");
        _sOff = gl.GetUniformLocation(_shadow, "uOff");
        _sStr = gl.GetUniformLocation(_shadow, "uStrength");
        _gColor = gl.GetUniformLocation(_glow, "uColor");
        _gStr = gl.GetUniformLocation(_glow, "uStrength");
        foreach (uint p in new[] { _blur, _shadow, _glow })
        {
            gl.UseProgram(p);
            gl.Uniform1(gl.GetUniformLocation(p, "uSrc"), 0);
        }
        gl.UseProgram(0);
    }

    /// <summary>Подложка: скруглённый прямоугольник (x0,y0)-(x1,y1) в пикселях холста, premultiplied поверх dst.</summary>
    public void Plate(RenderTarget dst, float x0, float y0, float x1, float y1, float radius, (float R, float G, float B) color, float alpha)
    {
        dst.Bind();
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        _gl.UseProgram(_plate);
        _gl.Uniform4(_pBox, x0, y0, x1, y1);
        _gl.Uniform1(_pRadius, radius);
        _gl.Uniform3(_pColor, color.R, color.G, color.B);
        _gl.Uniform1(_pAlpha, alpha);
        GlUtil.DrawFullscreen(_gl);
        _gl.Disable(EnableCap.Blend);
    }

    /// <summary>Размытие src в dst: ox, oy — шаг одного тапа в uv (H: (s,0), V: (0,s)). Без смешивания.</summary>
    public void Blur(uint src, RenderTarget dst, float ox, float oy)
    {
        dst.Bind();
        _gl.Disable(EnableCap.Blend);
        _gl.UseProgram(_blur);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, src);
        _gl.Uniform2(_bStep, ox, oy);
        GlUtil.DrawFullscreen(_gl);
    }

    /// <summary>Тень: чёрная альфа src со смещением off (uv) и силой strength, поверх dst.</summary>
    public void Shadow(uint src, RenderTarget dst, float offU, float offV, float strength)
    {
        dst.Bind();
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        _gl.UseProgram(_shadow);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, src);
        _gl.Uniform2(_sOff, offU, offV);
        _gl.Uniform1(_sStr, strength);
        GlUtil.DrawFullscreen(_gl);
        _gl.Disable(EnableCap.Blend);
    }

    /// <summary>Свечение: цвет color, альфа src × strength, screen поверх dst (rgb: src + dst·(1 − src)).</summary>
    public void Glow(uint src, RenderTarget dst, (float R, float G, float B) color, float strength)
    {
        dst.Bind();
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFuncSeparate(BlendingFactor.One, BlendingFactor.OneMinusSrcColor, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        _gl.UseProgram(_glow);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, src);
        _gl.Uniform3(_gColor, color.R, color.G, color.B);
        _gl.Uniform1(_gStr, strength);
        GlUtil.DrawFullscreen(_gl);
        _gl.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        if (_plate != 0) _gl.DeleteProgram(_plate);
        if (_blur != 0) _gl.DeleteProgram(_blur);
        if (_shadow != 0) _gl.DeleteProgram(_shadow);
        if (_glow != 0) _gl.DeleteProgram(_glow);
    }
}

/// <summary>
/// Одна бирка монитора (часы или логотип): содержимое (текст сетки или PNG), эффекты, размещение, дрейф, композит.
/// Сборка (холст): содержимое → подложка → тень → свечение → обводка → содержимое. Перерисовка — только при смене Version или размера.
/// Размещение и размер — по подложке (если она есть). Картиночная: PNG грузится один раз (premultiplied, мипмапы).
/// Файла нет — одно предупреждение и текст из Draw. Битый файл — одно предупреждение, бирка не рисуется.
/// </summary>
internal sealed class BadgeLayer : IDisposable
{
    private enum Mode { None, Text, Image }

    private static readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);

    private readonly Shared _s;
    private readonly IBadge _badge;
    private readonly int _index;
    private readonly BadgeEffects _fx;
    private Mode _mode;
    private int _vpW, _vpH;
    private bool _dirty = true;
    private long _builtVersion = long.MinValue;
    private double _time;

    // Текст (глифы сетки)
    private CharGrid? _grid;
    private Font? _font;
    private int _fontCellH = -1;
    private string _fontFamily = string.Empty;
    private GridRenderer? _renderer;
    private RenderTarget? _glyphs;

    // Картинка
    private uint _imageTex;
    private int _imageW, _imageH;
    private float _imageR = 1f, _imageG = 1f, _imageB = 1f;   // средний цвет картинки (для свечения)

    // Содержимое текущей сборки: текстура сетки или PNG, texel
    private uint _contentTex;
    private int _contentW, _contentH;

    // Холст сборки и проходы эффектов (размер холста = содержимое + поля)
    private RenderTarget? _canvas, _cont, _strokeRt, _blurA, _blurB;
    private OsdOutline? _stroke;
    private BadgeFx? _fxPass;
    private BadgeQuad? _quad;
    private int _canvasW, _canvasH;
    private float _k;            // пиксели цели на texel содержимого
    private float _boxW, _boxH;  // коробка размещения (подложка или содержимое), пиксели цели
    private float _insetPx;      // отступ содержимого от коробки (поле подложки), пиксели цели
    private float _padPx;        // поле холста, пиксели цели

    /// <summary>Бирка рисуется поверх эффектов (Placement.Over).</summary>
    public bool Over => _badge.Placement.Over;

    public BadgeLayer(Shared s, IBadge badge, int index, int vpW, int vpH)
    {
        _s = s;
        _badge = badge;
        _index = index;
        _vpW = vpW; _vpH = vpH;
        (badge as IOverlayParams)?.Configure(s.Config.OverlayOptionsFor(badge.Id));
        _fx = badge is ClockBadge c ? c.Effects : badge is LogoBadge l ? l.Effects : default;

        bool textCapable = badge.Cols > 0 && badge.Rows > 0;
        string? path = badge.ImagePath;
        if (path is null)
        {
            // Логотип со своим PNG, которого нет на диске, сам переходит на текст — сообщаем один раз
            if (badge is LogoBadge { FileMissing: true })
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

    /// <summary>Размер вьюпорта слота. Смена размера — перерисовка (кегль и обводка зависят от высоты).</summary>
    public void Resize(int vpW, int vpH)
    {
        if (vpW == _vpW && vpH == _vpH) return;
        _vpW = vpW; _vpH = vpH;
        _dirty = true;
    }

    /// <summary>Шаг кадра: состояние бирки, время дрейфа, сборка — только если сменилась Version или размер.</summary>
    public void Tick(float dt, in OverlayContext ctx)
    {
        _badge.Update(dt, in ctx);
        _time += dt;
        if (_mode != Mode.None && (_dirty || _badge.Version != _builtVersion)) Build();
    }

    /// <summary>
    /// Нарисовать бирку на dst (выход слоя или копия «поверх»). Координаты — в вьюпорте слота (vpW×vpH), dst может быть меньше — масштаб.
    /// Без готовой сборки — ничего не делает.
    /// </summary>
    public void Compose(RenderTarget dst, BadgeQuad quad)
    {
        if (_canvas is null) return;

        var p = _badge.Placement;
        // opacity 0 — бирка не рисуется (вместе с подложкой)
        if (p.Opacity <= 0f) return;
        var r = BadgeGeometry.PlaceBox(p.Anchor, _boxW, _boxH, p.Margin, _vpW, _vpH);
        if (p.Drift)
        {
            var (dx, dy) = BadgeGeometry.DriftOffset(_index, _time, _vpH);
            r = r with { X = r.X + dx, Y = r.Y + dy };
        }
        // Холст: угол содержимого = коробка + поле подложки, минус поле холста
        var c = new BadgeRect(r.X + _insetPx - _padPx, r.Y + _insetPx - _padPx, _canvasW * _k, _canvasH * _k);
        float sx = (float)dst.Width / _vpW, sy = (float)dst.Height / _vpH;
        c = new BadgeRect(c.X * sx, c.Y * sy, c.W * sx, c.H * sy);
        quad.Draw(_canvas.Texture, dst, c, Math.Clamp(p.Opacity, 0f, 1f), blend: true);
    }

    /// <summary>
    /// Сборка бирки: содержимое → холст с подложкой, тенью, свечением, обводкой и содержимым; коробка размещения.
    /// </summary>
    private void Build()
    {
        if (_mode == Mode.Text) BuildGlyphs();
        else
        {
            _contentTex = _imageTex;
            _contentW = _imageW;
            _contentH = _imageH;
        }
        if (_contentTex == 0 || _contentW <= 0 || _contentH <= 0) return;

        var p = _badge.Placement;
        var fx = _fx;
        int tw = _contentW, th = _contentH;
        float hc = p.Size * _vpH;                  // высота содержимого, пиксели цели
        _k = hc / th;
        // Обводка задана в пикселях экрана (stroke·H/1080) — переводим в texel текстуры
        float strokePx = BadgeGeometry.ToTexel(p.Stroke * _vpH / 1080f, hc, th);
        bool plate = BadgeGeometry.IsPlate(fx.Plate);
        int pad = BadgeGeometry.CanvasPad(th, plate, fx.Shadow, fx.Glow, strokePx);
        int cw = tw + 2 * pad, ch = th + 2 * pad;

        float e = plate ? BadgeGeometry.PlateInset * th : 0f;   // поле подложки, texel
        _boxW = (tw + 2 * e) * _k;
        _boxH = (th + 2 * e) * _k;
        _insetPx = e * _k;
        _padPx = pad * _k;
        _canvasW = cw; _canvasH = ch;

        _quad ??= new BadgeQuad(_s.Gl);
        _fxPass ??= new BadgeFx(_s.Gl);
        _canvas = Target(_canvas, cw, ch);
        _cont = Target(_cont, cw, ch);
        _blurA = Target(_blurA, cw, ch);
        _blurB = Target(_blurB, cw, ch);

        // Содержимое отдельно: для силуэта — цвет бирки × альфа, иначе как есть
        _cont.Clear(0, 0, 0, 0);
        (float R, float G, float B)? tint = fx.Silhouette ? RgbOf(fx.Tint) : null;
        _quad.Draw(_contentTex, _cont, new BadgeRect(pad, pad, tw, th), 1f, blend: false, tint: tint);

        // 1. подложка
        _canvas.Clear(0, 0, 0, 0);
        if (plate)
        {
            float ph = th + 2 * e;
            float radius = fx.Plate == "pill" ? ph / 2f : 0.25f * ph;
            _fxPass.Plate(_canvas, pad - e, pad - e, pad + tw + e, pad + th + e, radius,
                PlateRgb(fx.PlateColor), Math.Clamp(fx.PlateOpacity, 0f, 1f));
        }

        // 2. тень: размытая альфа, смещение 0.04·h вниз-вправо, чёрная
        if (fx.Shadow > 0f)
        {
            BlurContent(BadgeGeometry.ShadowBlur * th);
            float off = BadgeGeometry.ShadowOffset * th;
            // Вниз-вправо на экране = (+off, −off) в GL-координатах холста; выборка идёт с обратным смещением
            // shadow=1 → альфа тени 0.9 под содержимым
            _fxPass.Shadow(_blurB!.Texture, _canvas, off / cw, -off / ch, 0.9f * Math.Clamp(fx.Shadow, 0f, 1f));
        }

        // 3. свечение: screen; цвет — цвет бирки (силуэт, текст) или средний цвет картинки
        if (fx.Glow > 0f)
        {
            BlurContent(BadgeGeometry.GlowBlur * th);
            var glowRgb = (fx.Silhouette || _mode == Mode.Text) ? RgbOf(fx.Tint) : (_imageR, _imageG, _imageB);
            // Усиление ×2.5: размытая альфа тонких штрихов логотипа мала, без него ореол не виден
            _fxPass.Glow(_blurB!.Texture, _canvas, glowRgb, Math.Clamp(fx.Glow, 0f, 1f) * 2.5f);
        }

        // 4. обводка по альфе, чёрная — и для текста, и для картинки
        if (strokePx > 0f)
        {
            _stroke ??= new OsdOutline(_s.Gl);
            _strokeRt = Target(_strokeRt, cw, ch);
            _stroke.Stroke(_cont.Texture, _strokeRt, strokePx);
            _quad.Draw(_strokeRt.Texture, _canvas, Full(), 1f, blend: true);
        }

        // 5. содержимое
        _quad.Draw(_cont.Texture, _canvas, Full(), 1f, blend: true);

        _builtVersion = _badge.Version;
        _dirty = false;
    }

    /// <summary>
    /// Размытие альфы содержимого в _blurB: три прохода сепарабельного гаусса 9 тапов (H и V), шаг r/12 texel.
    /// Суммарно ±r без «ступенек» от редких тапов на большой текстуре.
    /// </summary>
    private void BlurContent(float radiusTex)
    {
        float step = radiusTex / 12f;
        var fx = _fxPass!;
        var a = _blurA!;
        var b = _blurB!;
        fx.Blur(_cont!.Texture, a, step / _canvasW, 0f);
        fx.Blur(a.Texture, b, 0f, step / _canvasH);
        for (int i = 0; i < 2; i++)
        {
            fx.Blur(b.Texture, a, step / _canvasW, 0f);
            fx.Blur(a.Texture, b, 0f, step / _canvasH);
        }
    }

    /// <summary>Текст сетки → глифы в _glyphs (содержимое сборки). Кегль — Size·H / Rows, кламп 8..64.</summary>
    private void BuildGlyphs()
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
        _contentTex = _glyphs.Texture;
        _contentW = tw;
        _contentH = th;
    }

    private RenderTarget Target(RenderTarget? rt, int w, int h)
    {
        if (rt is null) return new RenderTarget(_s.Gl, w, h);
        rt.Resize(w, h);
        return rt;
    }

    private BadgeRect Full() => new(0, 0, _canvasW, _canvasH);

    private static (float R, float G, float B) RgbOf(Rgba c) => (c.R / 255f, c.G / 255f, c.B / 255f);

    private static (float R, float G, float B) PlateRgb(string name) => name switch
    {
        "white" => (240f / 255f, 240f / 255f, 240f / 255f),
        "grey" => (128f / 255f, 128f / 255f, 128f / 255f),
        _ => (0f, 0f, 0f),
    };

    /// <summary>PNG → premultiplied RGBA, строки снизу вверх (v=0 — низ), GL-текстура с мипмапами. Заодно — средний цвет.</summary>
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
            double sr = 0, sg = 0, sb = 0, sa = 0;   // средний цвет, взвешенный альфой
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
                        sr += row[i + 2] * (double)a;
                        sg += row[i + 1] * (double)a;
                        sb += row[i] * (double)a;
                        sa += a;
                    }
                }
            }
            finally { argb.UnlockBits(bd); }
            if (sa > 0)
            {
                _imageR = (float)(sr / (sa * 255));
                _imageG = (float)(sg / (sa * 255));
                _imageB = (float)(sb / (sa * 255));
            }

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
        _canvas?.Dispose();
        _cont?.Dispose();
        _strokeRt?.Dispose();
        _blurA?.Dispose();
        _blurB?.Dispose();
        _stroke?.Dispose();
        _fxPass?.Dispose();
        _quad?.Dispose();
        if (_imageTex != 0) _s.Gl.DeleteTexture(_imageTex);
        _imageTex = 0;
    }
}
