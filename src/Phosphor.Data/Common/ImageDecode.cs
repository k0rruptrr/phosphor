using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Phosphor.Data;

/// <summary>Изображение RGB построчно сверху вниз: Width × Height × 3 байта.</summary>
public sealed record RgbImage(int Width, int Height, byte[] Rgb);

/// <summary>Разбор PNG/JPEG через System.Drawing: декод в RGB, склейка плиток, уменьшение с усреднением.</summary>
public static class ImageDecode
{
    /// <summary>Декодирует картинку в RGB. Бросает исключение, если формат не распознан (в т.ч. XML-ошибка WMS вместо картинки).</summary>
    public static RgbImage Decode(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var src = new Bitmap(ms);
        int w = src.Width, h = src.Height;

        // Приводим к 24 bpp: LockBits у индексированных и 1-битных картинок не даёт прямой раскладки.
        using var rgb = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(rgb)) g.DrawImageUnscaled(src, 0, 0);

        var pixels = new byte[w * h * 3];
        var lock24 = rgb.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(IntPtr.Add(lock24.Scan0, y * lock24.Stride), pixels, y * w * 3, w * 3);
        }
        finally
        {
            rgb.UnlockBits(lock24);
        }

        // BGR → RGB
        for (int i = 0; i < pixels.Length; i += 3)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        return new RgbImage(w, h, pixels);
    }

    /// <summary>
    /// Уменьшение с усреднением (box filter): каждый пиксель результата — среднее своей области источника.
    /// Если цель не меньше источника по обеим осям — источник возвращается как есть.
    /// </summary>
    public static RgbImage Downscale(RgbImage src, int dstW, int dstH)
    {
        if (dstW >= src.Width && dstH >= src.Height) return src;
        dstW = Math.Max(1, dstW);
        dstH = Math.Max(1, dstH);

        var xs = Ranges(src.Width, dstW);
        var ys = Ranges(src.Height, dstH);
        var dst = new byte[dstW * dstH * 3];
        for (int dy = 0; dy < dstH; dy++)
        {
            var (y0, y1) = ys[dy];
            for (int dx = 0; dx < dstW; dx++)
            {
                var (x0, x1) = xs[dx];
                long r = 0, g = 0, b = 0;
                int n = 0;
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int i = (sy * src.Width + sx) * 3;
                        r += src.Rgb[i];
                        g += src.Rgb[i + 1];
                        b += src.Rgb[i + 2];
                        n++;
                    }
                int o = (dy * dstW + dx) * 3;
                dst[o] = (byte)((r + n / 2) / n);
                dst[o + 1] = (byte)((g + n / 2) / n);
                dst[o + 2] = (byte)((b + n / 2) / n);
            }
        }
        return new RgbImage(dstW, dstH, dst);
    }

    /// <summary>Склейка плиток: grid[row][col], все плитки одного размера.</summary>
    public static RgbImage Stitch(RgbImage[][] grid)
    {
        int rows = grid.Length, cols = grid[0].Length;
        int tw = grid[0][0].Width, th = grid[0][0].Height;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                if (grid[r][c].Width != tw || grid[r][c].Height != th)
                    throw new InvalidOperationException("плитки разного размера");

        int w = tw * cols, h = th * rows;
        var dst = new byte[w * h * 3];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                var t = grid[r][c];
                for (int y = 0; y < th; y++)
                    Buffer.BlockCopy(t.Rgb, y * tw * 3, dst, ((r * th + y) * w + c * tw) * 3, tw * 3);
            }
        return new RgbImage(w, h, dst);
    }

    /// <summary>Доля пикселей, у которых все каналы ≤ maxChannel (почти чёрные).</summary>
    public static double DarkFraction(RgbImage img, int maxChannel)
    {
        int n = img.Width * img.Height;
        if (n == 0) return 1;
        int dark = 0;
        for (int i = 0; i < n * 3; i += 3)
            if (img.Rgb[i] <= maxChannel && img.Rgb[i + 1] <= maxChannel && img.Rgb[i + 2] <= maxChannel) dark++;
        return (double)dark / n;
    }

    private static (int From, int To)[] Ranges(int src, int dst)
    {
        var res = new (int From, int To)[dst];
        for (int i = 0; i < dst; i++)
        {
            int a = (int)((long)i * src / dst);
            int b = (int)((long)(i + 1) * src / dst);
            if (b <= a) b = a + 1;
            res[i] = (a, b);
        }
        return res;
    }
}
