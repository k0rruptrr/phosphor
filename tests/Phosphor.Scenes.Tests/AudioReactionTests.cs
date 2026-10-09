using System.Text;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

/// <summary>
/// Замер реакции сцен на музыку: двигается ли сама сцена в такт удара, а не только фильтр.
/// Метрики: D(t) — доля ячеек, изменившихся за кадр (Ch, Fg или Bg); Attack — D в окне 0–150 мс после удара
/// к D в окне 250–400 мс; Lift — средний D под музыкой к среднему D в тишине.
/// Для life и voxel Attack не подходит (см. их отдельные проверки состояния ниже).
/// </summary>
public sealed class AudioReactionTests
{
    private const int Cols = 192, Rows = 60, Seed = 1, Frames = 360;
    private const int BeatEvery = 30;           // удар каждые 0.5 с при 60 кадрах/с
    private const int FirstBeat = 30, LastBeat = 330;
    private const float PulseTau = 0.15f;       // BeatPulse = exp(-t/0.15)

    /// <summary>Все 11 сцен — для таблицы.</summary>
    public static readonly string[] SceneNames =
    {
        "matrix", "starfield", "pipes", "doomfire", "life", "dvd",
        "aquarium", "torus", "grayscott", "voxel", "boids",
    };

    /// <summary>Сцены с порогами Attack/Lift. life и voxel — отдельные проверки состояния.</summary>
    public static readonly string[] ThresholdScenes =
    {
        "matrix", "starfield", "pipes", "doomfire", "dvd",
        "aquarium", "torus", "boids",
    };

    private readonly ITestOutputHelper _out;

    public AudioReactionTests(ITestOutputHelper output) => _out = output;

    public readonly record struct Reaction(double Attack, double Lift, double DSilent, double DMusic);

    [Fact]
    public void AudioReactionTable()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Аудит реакции сцен на музыку (192x60, seed 1, 6 с, dt 1/60, удар каждые 0.5 с)");
        sb.AppendLine($"{"scene",-12}{"Attack",9}{"Lift",9}{"D(silent)",12}{"D(music)",11}");
        foreach (var name in SceneNames)
        {
            var r = Measure(name);
            sb.AppendLine($"{name,-12}{r.Attack,9:F2}{r.Lift,9:F2}{r.DSilent,12:F4}{r.DMusic,11:F4}");
        }
        var text = sb.ToString();
        _out.WriteLine(text);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "phosphor-audio-audit.txt"), text);
    }

    public static IEnumerable<object[]> ThresholdScenesData() => ThresholdScenes.Select(n => new object[] { n });

    /// <summary>Порог: под удар сцена заметно движется (Attack), и под музыку движется больше, чем в тишине (Lift).</summary>
    [Theory]
    [MemberData(nameof(ThresholdScenesData))]
    public void MeetsReactionThresholds(string name)
    {
        var r = Measure(name);
        _out.WriteLine($"{name}: Attack {r.Attack:F2}, Lift {r.Lift:F2}");
        Assert.True(r.Attack >= 1.4, $"{name}: Attack {r.Attack:F2} < 1.4");
        Assert.True(r.Lift >= 1.15, $"{name}: Lift {r.Lift:F2} < 1.15");
    }

    /// <summary>life: Lift как у остальных; плюс состояние — взрыв на ударе рождает ≥ 20 клеток.</summary>
    [Fact]
    public void LifeBurstBirthsOnBeat()
    {
        var r = Measure("life");
        _out.WriteLine($"life: Attack {r.Attack:F2} (не проверяется), Lift {r.Lift:F2}");
        Assert.True(r.Lift >= 1.15, $"life: Lift {r.Lift:F2} < 1.15");

        int musicBirths = -1, silentBirths = -1, musicLive = -1, silentLive = -1;
        Simulate(() => new LifeScene(), true, (f, s, g) =>
        {
            if (f == FirstBeat) { musicBirths = ((LifeScene)s).LastBurstBirths; musicLive = ((LifeScene)s).LiveCells; }
        });
        Simulate(() => new LifeScene(), false, (f, s, g) =>
        {
            if (f == FirstBeat) { silentBirths = ((LifeScene)s).LastBurstBirths; silentLive = ((LifeScene)s).LiveCells; }
        });
        _out.WriteLine($"life, кадр удара {FirstBeat}: взрыв родил {musicBirths} клеток; "
            + $"живых под музыкой {musicLive}, в тишине {silentLive} (тот же seed)");
        Assert.Equal(0, silentBirths);
        Assert.True(musicBirths >= 20, $"life: взрыв родил {musicBirths} < 20 клеток на ударе");
    }

    /// <summary>grayscott: Attack не проверяем (капля расплывается дольше 400 мс, метрика её не видит); Lift и счётчик капель на ударе.</summary>
    [Fact]
    public void GrayScottDropsOnBeat()
    {
        var r = Measure("grayscott");
        _out.WriteLine($"grayscott: Attack {r.Attack:F2} (не проверяется), Lift {r.Lift:F2}");
        Assert.True(r.Lift >= 1.15, $"grayscott: Lift {r.Lift:F2} < 1.15");

        int musicBefore = -1, musicAfter = -1, silentBefore = -1, silentAfter = -1;
        Simulate(() => new GrayScottScene(), true, (f, s, g) =>
        {
            if (f == FirstBeat - 1) musicBefore = ((GrayScottScene)s).BeatDrops;
            if (f == FirstBeat) musicAfter = ((GrayScottScene)s).BeatDrops;
        });
        Simulate(() => new GrayScottScene(), false, (f, s, g) =>
        {
            if (f == FirstBeat - 1) silentBefore = ((GrayScottScene)s).BeatDrops;
            if (f == FirstBeat) silentAfter = ((GrayScottScene)s).BeatDrops;
        });
        int musicAdded = musicAfter - musicBefore, silentAdded = silentAfter - silentBefore;
        _out.WriteLine($"grayscott, кадр удара {FirstBeat}: добавлено капель {musicAdded}; в тишине {silentAdded}");
        Assert.Equal(0, silentAdded);
        Assert.Equal(2, musicAdded);
    }

    /// <summary>
    /// voxel: Attack/Lift не требуем (сцена насыщена). Проверка состояния камеры: смещение горизонта
    /// в пике за 150 мс после удара ≥ 3× его среднего значения за 150 мс до удара (среднее по всем ударам).
    /// </summary>
    [Fact]
    public void VoxelHorizonKickOnBeat()
    {
        var shift = new float[Frames];
        Simulate(() => new VoxelScene(), true, (f, s, g) => shift[f] = ((VoxelScene)s).HorizonShiftPx);

        double sumRatio = 0;
        int n = 0;
        for (int b = FirstBeat; b <= LastBeat; b += BeatEvery)
        {
            double pre = 0;
            for (int k = 1; k <= 9; k++) pre += Math.Abs(shift[b - k]);
            pre /= 9;
            float post = 0;
            for (int k = 0; k <= 9; k++) post = MathF.Max(post, Math.Abs(shift[b + k]));
            sumRatio += post / Math.Max(pre, 1e-6);
            n++;
        }
        double ratio = sumRatio / n;
        _out.WriteLine($"voxel: горизонт пик после удара / среднее до удара = {ratio:F2}");
        Assert.True(ratio >= 3.0, $"voxel: горизонт в пике после удара только в {ratio:F2} раза больше, чем до удара (нужно ≥ 3)");
    }

    /// <summary>Дамп сетки сразу после удара (кадр 150) и через 300 мс (кадр 168) для трёх самых изменённых сцен.</summary>
    [Fact]
    [Trait("Category", "Diagnostics")]
    public void DumpBeatFrames()
    {
        var sb = new StringBuilder();
        foreach (var name in new[] { "matrix", "torus", "doomfire" })
        {
            // Дамп показывает только символы; для цветовых сцен (огонь) добавляем яркость по строкам.
            var rows = new StringBuilder();
            Simulate(Factory(name), true, (f, s, g) =>
            {
                if (f == 150) sb.AppendLine($"{name} сразу после удара: {SceneContract.Dump(g, $"audio-{name}-beat")}");
                if (f == 168) sb.AppendLine($"{name} через 300 мс: {SceneContract.Dump(g, $"audio-{name}-300ms")}");
                if (f == 150 || f == 168)
                {
                    rows.Append($"кадр {f}:");
                    for (int y = 0; y < g.Rows; y++) rows.Append($" {RowLuma(g, y)}");
                    rows.AppendLine();
                }
            });
            File.WriteAllText(Path.Combine(Path.GetTempPath(), $"phosphor-dump-audio-{name}-rows.txt"), rows.ToString());
        }
        _out.WriteLine(sb.ToString());
    }

    /// <summary>Прогон сцены под синтетическую музыку и в тишине, расчёт Attack и Lift.</summary>
    public static Reaction Measure(string name)
    {
        var make = Factory(name);
        var dMusic = Simulate(make, true);
        var dSilent = Simulate(make, false);

        // Attack: средний D в окне 0–150 мс (кадры 0..9 после удара) / в окне 250–400 мс (кадры 15..24).
        double early = 0, late = 0;
        int nEarly = 0, nLate = 0;
        for (int b = FirstBeat; b <= LastBeat; b += BeatEvery)
        {
            for (int k = 0; k <= 9; k++) { early += dMusic[b + k]; nEarly++; }
            for (int k = 15; k <= 24; k++) { late += dMusic[b + k]; nLate++; }
        }
        early /= nEarly;
        late /= nLate;
        double attack = early / Math.Max(late, 1e-9);

        double meanMusic = Mean(dMusic), meanSilent = Mean(dSilent);
        double lift = meanMusic / Math.Max(meanSilent, 1e-9);
        return new Reaction(attack, lift, meanSilent, meanMusic);
    }

    /// <summary>
    /// Возвращает D(t) для кадров 1..Frames-1 (кадр 0 — базовый).
    /// onFrame вызывается после Draw каждого кадра: индекс кадра, сцена и её сетка.
    /// </summary>
    private static double[] Simulate(Func<IScene> make, bool music, Action<int, IScene, CharGrid>? onFrame = null)
    {
        var s = SceneContract.Make(make, Cols, Rows, Seed);
        var audio = (IAudioReactive)s;
        var prev = new CharGrid(Cols, Rows);
        var cur = new CharGrid(Cols, Rows);
        s.Draw(prev);

        var d = new double[Frames];
        int lastBeat = -1000;
        for (int f = 0; f < Frames; f++)
        {
            var a = AudioFrame.Silent;
            if (music)
            {
                if (f >= FirstBeat && f % BeatEvery == 0) lastBeat = f;
                float pulse = MathF.Exp(-(f - lastBeat) / 60f / PulseTau);
                a = new AudioFrame(true, 0.6f, 0.3f + 0.5f * pulse, 0.4f, 0.3f,
                    f == lastBeat, pulse, 120f);
            }
            audio.OnAudio(a);
            s.Update(SceneContract.Dt);
            s.Draw(cur);
            onFrame?.Invoke(f, s, cur);
            d[f] = Changed(prev, cur);
            (prev, cur) = (cur, prev);
        }
        return d;
    }

    /// <summary>Средняя яркость строки (Fg и Bg, 0..255) — для дампа цветовых сцен.</summary>
    private static int RowLuma(CharGrid g, int y)
    {
        long sum = 0;
        for (int x = 0; x < g.Cols; x++)
        {
            var c = g[x, y];
            sum += (c.Fg.R + c.Fg.G + c.Fg.B + c.Bg.R + c.Bg.G + c.Bg.B) / 6;
        }
        return (int)(sum / g.Cols);
    }

    private static double Changed(CharGrid a, CharGrid b)
    {
        int n = 0;
        for (int i = 0; i < a.Cells.Length; i++)
        {
            var p = a.Cells[i];
            var c = b.Cells[i];
            if (p.Ch != c.Ch || p.Fg != c.Fg || p.Bg != c.Bg) n++;
        }
        return n / (double)a.Cells.Length;
    }

    private static double Mean(double[] d)
    {
        double sum = 0;
        for (int i = 1; i < d.Length; i++) sum += d[i];
        return sum / (d.Length - 1);
    }

    private static Func<IScene> Factory(string name) => name switch
    {
        "matrix" => () => new MatrixScene(),
        "starfield" => () => new StarfieldScene(),
        "pipes" => () => new PipesScene(),
        "doomfire" => () => new DoomFireScene(),
        "life" => () => new LifeScene(),
        "dvd" => () => new DvdScene(),
        "aquarium" => () => new AquariumScene(),
        "torus" => () => new TorusScene(),
        "grayscott" => () => new GrayScottScene(),
        "voxel" => () => new VoxelScene(),
        "boids" => () => new BoidsScene(),
        _ => throw new ArgumentException($"неизвестная сцена {name}"),
    };
}
