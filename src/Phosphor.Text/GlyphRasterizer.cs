using Phosphor.Core;
using StbTrueTypeSharp;
using System.Runtime.InteropServices;

namespace Phosphor.Text;

/// <summary>Атлас глифов на CPU: покрытие 0..255, row-major, строка 0 — верх картинки.</summary>
public sealed class AtlasBitmap
{
    public required byte[] Pixels { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int CellWidth { get; init; }
    public required int CellHeight { get; init; }
    /// <summary>Глиф i лежит в (i % AtlasCols, i / AtlasCols).</summary>
    public required int AtlasCols { get; init; }
    public required int GlyphCount { get; init; }
    /// <summary>Длина 65536. Для символа, которого нет в атласе, — индекс '?'.</summary>
    public required ushort[] CharToIndex { get; init; }
    /// <summary>Символы из GlyphSet.All, которые не нашлись ни в одном шрифте.</summary>
    public required IReadOnlyList<char> Missing { get; init; }
}

/// <summary>
/// Растеризация GlyphSet.All в атлас (CPU, без OpenGL).
/// Глиф i — символ GlyphSet.All[i]; глиф 0 — пробел (пустой).
/// </summary>
public static class GlyphRasterizer
{
    /// <summary>Сколько глифов в строке атласа.</summary>
    public const int AtlasCols = 32;

    private const string FontDir = @"C:\Windows\Fonts";
    private const string PrimaryFont = "consola.ttf";
    private static readonly string[] FallbackFonts = { "msgothic.ttc", "seguisym.ttf" };

    /// <summary>Растеризовать все глифы GlyphSet.All при высоте ячейки cellHeightPx (8..64).</summary>
    public static AtlasBitmap Rasterize(int cellHeightPx)
    {
        if (cellHeightPx < 8 || cellHeightPx > 64)
            throw new ArgumentOutOfRangeException(nameof(cellHeightPx), "Высота ячейки должна быть 8..64");

        var faces = new List<FontFace>();
        try
        {
            // Основной шрифт обязан быть первым: по нему считаются размеры ячейки.
            faces.Add(FontFace.Load(Path.Combine(FontDir, PrimaryFont)));
            foreach (var name in FallbackFonts)
            {
                string path = Path.Combine(FontDir, name);
                if (File.Exists(path)) faces.Add(FontFace.Load(path));
            }
            return Build(faces, cellHeightPx);
        }
        finally
        {
            foreach (var f in faces) f.Dispose();
        }
    }

    private static unsafe AtlasBitmap Build(List<FontFace> faces, int cellH)
    {
        var primary = faces[0];
        float scale = StbTrueType.stbtt_ScaleForPixelHeight(primary.Info, cellH);

        int adv, lsb;
        StbTrueType.stbtt_GetCodepointHMetrics(primary.Info, 'M', &adv, &lsb);
        int cellW = Math.Max(1, (int)Math.Round(adv * scale));
        int baseline = (int)Math.Round(primary.Ascent * scale);

        string all = GlyphSet.All;
        int count = all.Length;
        int cols = AtlasCols;
        int rows = (count + cols - 1) / cols;
        int W = cols * cellW, H = rows * cellH;
        var pixels = new byte[W * H];
        var map = new ushort[65536];
        int qIndex = all.IndexOf('?');
        Array.Fill(map, (ushort)qIndex);
        var missing = new List<char>();

        for (int gi = 0; gi < count; gi++)
        {
            char c = all[gi];
            int ox = (gi % cols) * cellW, oy = (gi / cols) * cellH;

            if (GlyphSet.IsBox(c) || GlyphSet.IsSextant(c))
            {
                BoxGlyphs.Draw(c, pixels, W, ox, oy, cellW, cellH);
                map[c] = (ushort)gi;
                continue;
            }

            int fi = -1;
            for (int i = 0; i < faces.Count; i++)
            {
                if (StbTrueType.stbtt_FindGlyphIndex(faces[i].Info, c) != 0) { fi = i; break; }
            }
            if (fi < 0)
            {
                missing.Add(c);
                continue;
            }

            map[c] = (ushort)gi;
            DrawFontGlyph(faces[fi], fi == 0, c, scale, cellW, cellH, baseline, pixels, W, ox, oy);
        }

        return new AtlasBitmap
        {
            Pixels = pixels,
            Width = W,
            Height = H,
            CellWidth = cellW,
            CellHeight = cellH,
            AtlasCols = cols,
            GlyphCount = count,
            CharToIndex = map,
            Missing = missing,
        };
    }

    /// <summary>
    /// Основной шрифт: origin по X = 0, baseline по Y. Фолбэк: масштаб по высоте Consolas,
    /// уменьшение, если шире ячейки, центрирование по горизонтали.
    /// </summary>
    private static unsafe void DrawFontGlyph(FontFace face, bool isPrimary, char c, float primaryScale,
        int cellW, int cellH, int baseline, byte[] dst, int dstW, int ox, int oy)
    {
        float sc = isPrimary ? primaryScale : cellH / (float)(face.Ascent - face.Descent);

        if (!isPrimary)
        {
            int bx0, by0, bx1, by1;
            StbTrueType.stbtt_GetCodepointBitmapBox(face.Info, c, sc, sc, &bx0, &by0, &bx1, &by1);
            int boxW = bx1 - bx0;
            if (boxW > cellW && boxW > 0) sc *= (float)cellW / boxW;
        }

        int w, h, xo, yo;
        byte* bmp = StbTrueType.stbtt_GetCodepointBitmap(face.Info, sc, sc, c, &w, &h, &xo, &yo);
        if (bmp == null) return;
        try
        {
            if (w <= 0 || h <= 0) return;
            int x0 = isPrimary ? xo : (cellW - w) / 2;
            int y0 = baseline + yo;
            for (int j = 0; j < h; j++)
            {
                int y = y0 + j;
                if ((uint)y >= (uint)cellH) continue;
                for (int i = 0; i < w; i++)
                {
                    int x = x0 + i;
                    if ((uint)x >= (uint)cellW) continue;
                    dst[(oy + y) * dstW + ox + x] = bmp[j * w + i];
                }
            }
        }
        finally
        {
            StbTrueType.stbtt_FreeBitmap(bmp, null);
        }
    }

    /// <summary>Загруженный файл шрифта. Байты закреплены в памяти до Dispose.</summary>
    private sealed class FontFace : IDisposable
    {
        private GCHandle _handle;

        public StbTrueType.stbtt_fontinfo Info { get; }
        /// <summary>hhea ascent/descent в единицах шрифта (descent отрицательный).</summary>
        public int Ascent { get; }
        public int Descent { get; }

        private FontFace(GCHandle handle, StbTrueType.stbtt_fontinfo info, int ascent, int descent)
        {
            _handle = handle;
            Info = info;
            Ascent = ascent;
            Descent = descent;
        }

        public static unsafe FontFace Load(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                byte* p = (byte*)handle.AddrOfPinnedObject();
                int offset = StbTrueType.stbtt_GetFontOffsetForIndex(p, 0);
                if (offset < 0) throw new InvalidDataException($"Не удалось найти шрифт в '{path}'");

                var info = new StbTrueType.stbtt_fontinfo();
                if (StbTrueType.stbtt_InitFont(info, p, offset) == 0)
                    throw new InvalidDataException($"stbtt_InitFont не смог разобрать '{path}'");

                int asc, desc, gap;
                StbTrueType.stbtt_GetFontVMetrics(info, &asc, &desc, &gap);
                return new FontFace(handle, info, asc, desc);
            }
            catch
            {
                handle.Free();
                throw;
            }
        }

        public void Dispose()
        {
            if (_handle.IsAllocated) _handle.Free();
        }
    }
}
