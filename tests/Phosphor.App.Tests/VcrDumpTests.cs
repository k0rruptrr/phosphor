using System.Drawing;
using System.Drawing.Imaging;
using Phosphor.Core;
using Phosphor.Overlays;

namespace Phosphor.App.Tests;

/// <summary>
/// Диагностика: рисует OSD видика на сетке 120×40 и сохраняет PNG (каждая клетка — 12×18 px, секстанты как подпиксели).
/// Не входит в обычный прогон. Запуск: PHOSPHOR_DUMP_DIR=<каталог> dotnet test ... --filter "Category=Diagnostics".
/// </summary>
[Trait("Category", "Diagnostics")]
public class VcrDumpTests
{
    private const int CellW = 12, CellH = 18;

    [Fact]
    public void Dump_vcr_grid_at_half_tape()
    {
        var dir = Environment.GetEnvironmentVariable("PHOSPHOR_DUMP_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        const int cols = 120, rows = 40;

        // Яркий фон, похожий на плазму: оранжево-жёлтый градиент; сцена без символов
        var g = new CharGrid(cols, rows);
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
                g.Put(x, y, ' ', Rgba.White, new Rgba(255, (byte)(120 + 135 * x / cols), (byte)(40 + 180 * y / rows)));

        var v = new VcrOverlay();
        v.Init(cols, rows, new Random(1));
        v.Update(90 * 60f + 0.2f);               // половина ленты, двоеточие вкл.
        v.SetOn(true);                           // видик включён — показ с этого момента
        v.Apply(g, new OverlayContext("plasma", "kassette", 0, 1, 59.9f, 0,
            new DateTime(2026, 10, 10, 14, 5, 0), AudioFrame.Silent, new Organism(0f, 0f, 0.7f, 0f)));

        using var bmp = new Bitmap(cols * CellW, rows * CellH, PixelFormat.Format24bppRgb);
        using (var gr = Graphics.FromImage(bmp))
        {
            gr.Clear(Color.Black);
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                {
                    var c = g[x, y];
                    int px = x * CellW, py = y * CellH;
                    using (var bg = new SolidBrush(Color.FromArgb(c.Bg.R, c.Bg.G, c.Bg.B)))
                        gr.FillRectangle(bg, px, py, CellW, CellH);
                    using var fg = new SolidBrush(Color.FromArgb(c.Fg.R, c.Fg.G, c.Fg.B));
                    if (c.Ch == '█') gr.FillRectangle(fg, px, py, CellW, CellH);
                    else if (c.Ch >= '' && c.Ch <= '')
                    {
                        int bits = c.Ch - '';
                        for (int dy = 0; dy < 3; dy++)
                            for (int dx = 0; dx < 2; dx++)
                                if ((bits & (1 << (dy * 2 + dx))) != 0)
                                    gr.FillRectangle(fg, px + dx * 6, py + dy * 6, 6, 6);
                    }
                }
        }
        Directory.CreateDirectory(dir);
        bmp.Save(Path.Combine(dir, "vcr-grid-half-tape.png"), ImageFormat.Png);
    }
}
