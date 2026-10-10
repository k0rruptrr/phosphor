using Phosphor.Core;
using StbTrueTypeSharp;

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
    private const string ConsolasFile = "consola.ttf";
    private static readonly string[] FallbackFonts = { "msgothic.ttc", "seguisym.ttf" };

    /// <summary>
    /// Растеризовать все глифы GlyphSet.All при высоте ячейки cellHeightPx (8..64).
    /// font — имя семейства или путь к .ttf/.otf (см. FontCatalog); не найден — Consolas, причина в warn.
    /// </summary>
    public static AtlasBitmap Rasterize(int cellHeightPx, string? font = null, Action<string>? warn = null)
    {
        if (cellHeightPx < 8 || cellHeightPx > 64)
            throw new ArgumentOutOfRangeException(nameof(cellHeightPx), "Высота ячейки должна быть 8..64");

        var faces = new List<FontFile>();
        try
        {
            // Основной шрифт обязан быть первым: по нему считаются размеры ячейки.
            faces.Add(OpenPrimary(font, warn));
            foreach (var name in FallbackFonts)
            {
                string path = Path.Combine(FontDir, name);
                if (File.Exists(path)) faces.Add(FontFile.Load(path));
            }
            return Build(faces, cellHeightPx);
        }
        finally
        {
            foreach (var f in faces) f.Dispose();
        }
    }

    private static FontFile OpenPrimary(string? font, Action<string>? warn)
    {
        string want = string.IsNullOrWhiteSpace(font) ? FontCatalog.DefaultFamily : font.Trim();
        var entry = FontCatalog.Find(want);
        if (entry is not null)
        {
            try { return FontFile.Load(entry.Path); }
            catch (Exception e) when (e is IOException or InvalidDataException) { warn?.Invoke($"шрифт '{want}' не открылся ({e.Message}), использую Consolas"); }
        }
        else if (!string.Equals(want, FontCatalog.DefaultFamily, StringComparison.OrdinalIgnoreCase))
        {
            warn?.Invoke($"шрифт '{want}' не найден среди моноширинных, использую Consolas");
        }
        return FontFile.Load(Path.Combine(FontDir, ConsolasFile));
    }

    private static unsafe AtlasBitmap Build(List<FontFile> faces, int cellH)
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
    private static unsafe void DrawFontGlyph(FontFile face, bool isPrimary, char c, float primaryScale,
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
}
