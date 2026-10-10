using Silk.NET.OpenGL;

namespace Phosphor.Gfx;

/// <summary>
/// Шлейф (моушн-блюр) поверх готовой картинки слота: out = mix(current, prevOut, trail).
/// Два RenderTarget (пинг-понг) размера слота. trail ≤ 0.001 — вход возвращается как есть, ничего не рисуется,
/// буферы не выделяются до первого включения. Первый кадр после включения или ресайза: prev = current (без вспышки).
/// </summary>
public sealed class TrailBuffer : IDisposable
{
    private const float Off = 0.001f;
    private const float MaxTrail = 0.9f;

    private readonly GL _gl;
    private uint _prog;
    private int _locCur, _locPrev, _locTrail;
    private RenderTarget? _a, _b;
    private int _w, _h;
    /// <summary>Какой из буферов хранит предыдущий выход (0 — _a, 1 — _b).</summary>
    private int _prevIdx;
    /// <summary>false — следующий кадр берёт prev = current.</summary>
    private bool _hasPrev;

    private const string Frag = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColor;
        uniform sampler2D uCur;
        uniform sampler2D uPrev;
        uniform float uTrail;
        void main() { fragColor = mix(texture(uCur, vUv), texture(uPrev, vUv), uTrail); }
        """;

    public TrailBuffer(GL gl) => _gl = gl;

    /// <summary>Размер слота. Смена размера сбрасывает историю.</summary>
    public void Resize(int width, int height)
    {
        if (width == _w && height == _h) return;
        _w = width; _h = height;
        _hasPrev = false;
        if (_a is null) return;
        if (width <= 0 || height <= 0) { FreeBuffers(); return; }
        _a.Resize(width, height);
        _b!.Resize(width, height);
    }

    /// <summary>Вернуть текстуру с шлейфом. tex — текущий кадр слота. Результат живёт до следующего вызова.</summary>
    public uint Apply(uint tex, float trail)
    {
        if (trail <= Off || _w <= 0 || _h <= 0)
        {
            _hasPrev = false;
            return tex;
        }
        trail = Math.Min(trail, MaxTrail);
        EnsureAllocated();

        var prev = _prevIdx == 0 ? _a! : _b!;
        var dst = _prevIdx == 0 ? _b! : _a!;
        float t = _hasPrev ? trail : 0f;

        dst.Bind();
        _gl.Disable(EnableCap.Blend);
        _gl.UseProgram(_prog);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, prev.Texture);
        _gl.Uniform1(_locCur, 0);
        _gl.Uniform1(_locPrev, 1);
        _gl.Uniform1(_locTrail, t);
        GlUtil.DrawFullscreen(_gl);

        _prevIdx = _prevIdx == 0 ? 1 : 0;
        _hasPrev = true;
        return dst.Texture;
    }

    private void EnsureAllocated()
    {
        if (_a is not null) return;
        if (_prog == 0)
        {
            _prog = GlUtil.CreateProgram(_gl, GlUtil.FullscreenVertex, Frag, "trail");
            _locCur = _gl.GetUniformLocation(_prog, "uCur");
            _locPrev = _gl.GetUniformLocation(_prog, "uPrev");
            _locTrail = _gl.GetUniformLocation(_prog, "uTrail");
        }
        _a = new RenderTarget(_gl, _w, _h);
        _b = new RenderTarget(_gl, _w, _h);
        _prevIdx = 0;
        _hasPrev = false;
    }

    private void FreeBuffers()
    {
        _a?.Dispose(); _b?.Dispose();
        _a = null; _b = null;
    }

    public void Dispose()
    {
        FreeBuffers();
        if (_prog != 0) _gl.DeleteProgram(_prog);
        _prog = 0;
    }
}
