using Phosphor.Core;
using Phosphor.Gfx;
using Silk.NET.OpenGL;

namespace Phosphor.Text;

/// <summary>
/// Рисует CharGrid одним полноэкранным проходом. Сетка уходит на GPU в трёх текстурах
/// (fg RGBA8, bg RGBA8, индекс глифа R16UI); атлас — из GlyphAtlas.
/// </summary>
public sealed unsafe class GridRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly GlyphAtlas _atlas;
    private readonly uint _program;
    private readonly int _locGridSize;
    private readonly int _locCellSize;
    private readonly int _locAtlasCols;
    private readonly int _locTargetHeight;

    private uint _fgTex, _bgTex, _glyphTex;
    private int _cols = -1, _rows = -1;
    private byte[] _fg = Array.Empty<byte>();
    private byte[] _bg = Array.Empty<byte>();
    private ushort[] _glyph = Array.Empty<ushort>();

    public GridRenderer(GL gl, GlyphAtlas atlas)
    {
        _gl = gl;
        _atlas = atlas;

        string fragSrc = GlUtil.LoadResource(typeof(GridRenderer).Assembly, "Phosphor.Text.Shaders.grid.frag");
        _program = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex, fragSrc, "grid");

        gl.UseProgram(_program);
        gl.Uniform1(gl.GetUniformLocation(_program, "uFg"), 0);
        gl.Uniform1(gl.GetUniformLocation(_program, "uBg"), 1);
        gl.Uniform1(gl.GetUniformLocation(_program, "uGlyph"), 2);
        gl.Uniform1(gl.GetUniformLocation(_program, "uAtlas"), 3);
        _locGridSize = gl.GetUniformLocation(_program, "uGridSize");
        _locCellSize = gl.GetUniformLocation(_program, "uCellSize");
        _locAtlasCols = gl.GetUniformLocation(_program, "uAtlasCols");
        _locTargetHeight = gl.GetUniformLocation(_program, "uTargetHeight");
        gl.UseProgram(0);
    }

    /// <summary>Нарисовать сетку в target (левый верхний угол). Остаток target — чёрный.</summary>
    public void Render(CharGrid grid, RenderTarget target)
    {
        EnsureTextures(grid.Cols, grid.Rows);
        FillBuffers(grid);
        UploadBuffers();

        target.Bind();
        _gl.Disable(EnableCap.Blend);
        _gl.UseProgram(_program);

        BindUnit(0, _fgTex);
        BindUnit(1, _bgTex);
        BindUnit(2, _glyphTex);
        BindUnit(3, _atlas.Texture);

        _gl.Uniform2(_locGridSize, grid.Cols, grid.Rows);
        _gl.Uniform2(_locCellSize, _atlas.CellWidth, _atlas.CellHeight);
        _gl.Uniform1(_locAtlasCols, _atlas.AtlasCols);
        _gl.Uniform1(_locTargetHeight, target.Height);

        GlUtil.DrawFullscreen(_gl);

        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    private void BindUnit(int unit, uint texture)
    {
        _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
        _gl.BindTexture(TextureTarget.Texture2D, texture);
    }

    private void EnsureTextures(int cols, int rows)
    {
        if (cols == _cols && rows == _rows) return;

        FreeTextures();
        _fgTex = NewTexture(InternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte, cols, rows);
        _bgTex = NewTexture(InternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte, cols, rows);
        _glyphTex = NewTexture(InternalFormat.R16ui, PixelFormat.RedInteger, PixelType.UnsignedShort, cols, rows);

        int n = cols * rows;
        _fg = new byte[n * 4];
        _bg = new byte[n * 4];
        _glyph = new ushort[n];
        _cols = cols;
        _rows = rows;
    }

    private uint NewTexture(InternalFormat format, PixelFormat pf, PixelType pt, int cols, int rows)
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, format, (uint)cols, (uint)rows, 0, pf, pt, null);
        return tex;
    }

    private void FreeTextures()
    {
        if (_fgTex != 0) _gl.DeleteTexture(_fgTex);
        if (_bgTex != 0) _gl.DeleteTexture(_bgTex);
        if (_glyphTex != 0) _gl.DeleteTexture(_glyphTex);
        _fgTex = _bgTex = _glyphTex = 0;
    }

    private void FillBuffers(CharGrid grid)
    {
        var cells = grid.Cells;
        for (int i = 0; i < cells.Length; i++)
        {
            ref Cell c = ref cells[i];
            int o = i * 4;
            _fg[o] = c.Fg.R; _fg[o + 1] = c.Fg.G; _fg[o + 2] = c.Fg.B; _fg[o + 3] = c.Fg.A;
            _bg[o] = c.Bg.R; _bg[o + 1] = c.Bg.G; _bg[o + 2] = c.Bg.B; _bg[o + 3] = c.Bg.A;
            _glyph[i] = _atlas.IndexOf(c.Ch);
        }
    }

    private void UploadBuffers()
    {
        // Строки сетки не выровнены по 4 байтам (R16UI при нечётном Cols) — выравнивание 1 обязательно.
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        uint cols = (uint)_cols, rows = (uint)_rows;

        _gl.BindTexture(TextureTarget.Texture2D, _fgTex);
        fixed (byte* p = _fg)
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, cols, rows, PixelFormat.Rgba, PixelType.UnsignedByte, p);

        _gl.BindTexture(TextureTarget.Texture2D, _bgTex);
        fixed (byte* p = _bg)
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, cols, rows, PixelFormat.Rgba, PixelType.UnsignedByte, p);

        _gl.BindTexture(TextureTarget.Texture2D, _glyphTex);
        fixed (ushort* p = _glyph)
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, cols, rows, PixelFormat.RedInteger, PixelType.UnsignedShort, p);
    }

    public void Dispose()
    {
        FreeTextures();
        if (_program != 0) _gl.DeleteProgram(_program);
    }
}
