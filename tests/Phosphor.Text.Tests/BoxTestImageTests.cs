using Phosphor.Core;
using Phosphor.Text;
using StbImageWriteSharp;

namespace Phosphor.Text.Tests;

/// <summary>Собирает картинку 30×6 ячеек из глифов атласа и сохраняет в %TEMP%\phosphor-boxtest.png.</summary>
public class BoxTestImageTests
{
    private const int Cols = 30;
    private const int Rows = 6;

    [Fact]
    public void ComposeBoxTestImage()
    {
        var atlas = GlyphRasterizer.Rasterize(18);
        int cw = atlas.CellWidth, ch = atlas.CellHeight;
        int W = Cols * cw, H = Rows * ch;
        var canvas = new byte[W * H];

        void Put(int col, int row, string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ') continue;
                int g = atlas.CharToIndex[c];
                int gx = (g % atlas.AtlasCols) * cw, gy = (g / atlas.AtlasCols) * ch;
                int dx = (col + i) * cw, dy = row * ch;
                for (int y = 0; y < ch; y++)
                    for (int x = 0; x < cw; x++)
                        canvas[(dy + y) * W + dx + x] = atlas.Pixels[(gy + y) * atlas.Width + gx + x];
            }
        }

        // Двойная рамка 5×3
        Put(0, 0, "╔═══╗");
        Put(0, 1, "║   ║");
        Put(0, 2, "╚═══╝");

        // Одинарные линии справа (строки ┌─┐ и └┘│ как заданы в ТЗ)
        Put(6, 0, "┌─┐");
        Put(6, 1, "└┘│");

        // Строка с кириллицей, полуширинной катаканой и штриховками
        Put(0, 3, "Матрица ﾊﾟｲﾌﾟ ░▒▓█");

        string path = Path.Combine(Path.GetTempPath(), "phosphor-boxtest.png");
        using (var fs = File.Create(path))
        {
            new ImageWriter().WritePng(canvas, W, H, ColorComponents.Grey, fs);
        }
        Assert.True(new FileInfo(path).Length > 0);
    }
}
