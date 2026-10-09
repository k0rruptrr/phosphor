using Phosphor.Core;
using Silk.NET.OpenGL;

namespace Phosphor.Text;

/// <summary>Атлас глифов на GPU: R8, NEAREST, ClampToEdge. Строка 0 — верх атласа.</summary>
public sealed class GlyphAtlas : IDisposable, IGlyphCoverage
{
    private readonly GL? _gl;
    private readonly AtlasBitmap _bmp;
    /// <summary>Покрытие глифа i: средняя альфа пикселей ячейки, 0..1. Считается один раз при построении.</summary>
    private readonly float[] _coverage;

    private GlyphAtlas(GL? gl, AtlasBitmap bmp, uint texture)
    {
        _gl = gl;
        _bmp = bmp;
        Texture = texture;
        _coverage = ComputeCoverage(bmp);
    }

    public int CellWidth => _bmp.CellWidth;
    public int CellHeight => _bmp.CellHeight;
    public int AtlasCols => _bmp.AtlasCols;
    public int GlyphCount => _bmp.GlyphCount;
    public uint Texture { get; private set; }
    public IReadOnlyList<char> Missing => _bmp.Missing;

    /// <summary>Растеризовать и загрузить на GPU.</summary>
    public static unsafe GlyphAtlas Build(GL gl, int cellHeightPx)
    {
        var bmp = GlyphRasterizer.Rasterize(cellHeightPx);

        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        // Строки атласа не выровнены по 4 байтам (R8, ширина в пикселях) — без этого картинка перекосится.
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = bmp.Pixels)
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8, (uint)bmp.Width, (uint)bmp.Height, 0,
                PixelFormat.Red, PixelType.UnsignedByte, p);
        }
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        return new GlyphAtlas(gl, bmp, tex);
    }

    /// <summary>Атлас только на CPU (без GL, без текстуры): для покрытия и тестов.</summary>
    public static GlyphAtlas FromBitmap(AtlasBitmap bmp) => new(null, bmp, 0);

    /// <summary>Индекс глифа для символа; для символа вне атласа — индекс '?'.</summary>
    public ushort IndexOf(char c) => _bmp.CharToIndex[c];

    /// <summary>Покрытие символа 0..1 (пробел = 0, '█' ≈ 1). Символ вне атласа — покрытие его замены ('?').</summary>
    public float Coverage(char c) => _coverage[_bmp.CharToIndex[c]];

    public void Dispose()
    {
        if (_gl is not null && Texture != 0)
        {
            _gl.DeleteTexture(Texture);
            Texture = 0;
        }
    }

    private static float[] ComputeCoverage(AtlasBitmap bmp)
    {
        var result = new float[bmp.GlyphCount];
        int cw = bmp.CellWidth, ch = bmp.CellHeight, cols = bmp.AtlasCols, W = bmp.Width;
        float norm = 1f / (255f * cw * ch);
        for (int gi = 0; gi < result.Length; gi++)
        {
            int ox = (gi % cols) * cw, oy = (gi / cols) * ch;
            long sum = 0;
            for (int y = 0; y < ch; y++)
            {
                int row = (oy + y) * W + ox;
                for (int x = 0; x < cw; x++) sum += bmp.Pixels[row + x];
            }
            result[gi] = sum * norm;
        }
        return result;
    }
}
