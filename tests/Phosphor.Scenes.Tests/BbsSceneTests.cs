using System.Text;
using System.Text.Json;
using Phosphor.Core;

namespace Phosphor.Scenes.Tests;

public sealed class BbsSceneTests
{
    private const int Cols = 100, Rows = 40;

    [Fact]
    public void Contract() => SceneContract.VerifyAll(() => new BbsScene(), "bbs");

    [Fact]
    public void SessionGoesConnectThenFrameThenNoCarrier()
    {
        var s = Make(11, "14400", 30);
        var g = new CharGrid(Cols, Rows);
        int connect = -1, frame = -1, carrier = -1;
        for (int i = 0; i < 60 * 60 && carrier < 0; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
            string t = Text(g);
            if (connect < 0 && t.Contains("CONNECT")) connect = i;
            if (frame < 0 && t.Contains('╔')) frame = i;
            if (carrier < 0 && t.Contains("NO CARRIER")) carrier = i;
        }
        Assert.True(connect >= 0, "за 60 с не появился CONNECT");
        Assert.True(frame > connect, $"рамка ╔ ({frame}) не появилась после CONNECT ({connect})");
        Assert.True(carrier > frame, $"NO CARRIER ({carrier}) не появился после рамки ({frame})");
    }

    /// <summary>Длина сеанса: NO CARRIER приходит через ~sessionMax секунд после CONNECT. При 30 и 300 разница в разы.</summary>
    [Fact]
    public void SessionMaxControlsLength()
    {
        int shortEnd = CarrierFrame(30);
        int longEnd = CarrierFrame(300);
        Assert.True(shortEnd > 0 && longEnd > 0, "NO CARRIER не появился");
        Assert.True(longEnd >= 3 * shortEnd,
            $"sessionMax 30: NO CARRIER на {shortEnd / 60.0:F1} с, sessionMax 300: на {longEnd / 60.0:F1} с — разницы в разы нет");
    }

    [Fact]
    public void SlowerBaudPrintsSlower()
    {
        int slow = FramesUntil("2400", "8-095-");
        int fast = FramesUntil("28800", "8-095-");
        Assert.True(slow > fast, $"2400: {slow} кадров до номера, 28800: {fast} кадров");
    }

    /// <summary>Текстовые дампы экранов по ключевым кадрам: дозвон, логотип, меню, файлы, почта, дверь.</summary>
    [Fact]
    [Trait("Category", "Diagnostics")]
    public void DumpScreens()
    {
        var s = Make(13, "2400", 120);
        var g = new CharGrid(Cols, Rows);
        var marks = new HashSet<int> { 150, 230, 260, 290, 330, 400, 650, 900, 1200, 1500, 1800 };
        for (int i = 1; i <= 1800; i++)
        {
            s.Update(SceneContract.Dt);
            if (!marks.Contains(i)) continue;
            s.Draw(g);
            SceneContract.Dump(g, $"bbs-{i}");
        }
    }

    /// <summary>Кадр, на котором впервые виден NO CARRIER (проверка каждые 10 кадров).</summary>
    private static int CarrierFrame(int sessionMax)
    {
        var s = Make(14, "14400", sessionMax);
        var g = new CharGrid(Cols, Rows);
        int limit = (sessionMax + 30) * 60;
        for (int i = 1; i <= limit; i++)
        {
            s.Update(SceneContract.Dt);
            if (i % 10 != 0) continue;
            s.Draw(g);
            if (Text(g).Contains("NO CARRIER")) return i;
        }
        return -1;
    }

    private static int FramesUntil(string baud, string needle)
    {
        var s = Make(12, baud, 120);
        var g = new CharGrid(Cols, Rows);
        for (int i = 1; i <= 600; i++)
        {
            s.Update(SceneContract.Dt);
            s.Draw(g);
            if (Text(g).Contains(needle)) return i;
        }
        return int.MaxValue;
    }

    private static IScene Make(int seed, string baud, int sessionMax)
    {
        var o = new SceneOptions
        {
            Params = JsonDocument.Parse($"{{\"baud\":\"{baud}\",\"sessionMax\":{sessionMax}}}").RootElement,
        };
        return SceneContract.Make(() => new BbsScene(), Cols, Rows, seed, o);
    }

    /// <summary>Текст сетки построчно, символы как есть.</summary>
    private static string Text(CharGrid g)
    {
        var sb = new StringBuilder();
        for (int y = 0; y < g.Rows; y++)
        {
            for (int x = 0; x < g.Cols; x++) sb.Append(g[x, y].Ch);
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
