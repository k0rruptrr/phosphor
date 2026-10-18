using System.Text;
using System.Text.Json;
using Phosphor.Core;
using Xunit.Abstractions;

namespace Phosphor.Scenes.Tests;

/// <summary>
/// Параметры сцен (ISceneParams): спецификации корректны, Min и Max реально работают и меняют картинку.
/// Сценоспецифичные проверки — в своих файлах; здесь только общие правила для всех сцен с параметрами.
/// </summary>
public sealed class SceneParamsTests
{
    private const int Cols = 120, Rows = 40;
    private const int DefaultFrames = 120;

    /// <summary>
    /// Особые случаи. Ключ — «Сцена.параметр». Frames — сколько кадров симулировать (по умолчанию 120 = 2 с).
    /// Beats — подавать ли в сцену удары звука (IAudioReactive, раз в 30 кадров). Skip — эффект здесь не проверяется.
    /// </summary>
    private sealed record Special(int Frames = DefaultFrames, bool Beats = false, bool Skip = false, bool EmptyAtMinOk = false, string Reason = "");

    private static readonly Dictionary<string, Special> Specials = new()
    {
        // Медленные таймеры: события наступают через секунды, 120 кадров их не покажут.
        ["C64LoadScene.titleSeconds"] = new(Frames: 2400, Reason: "титульник после загрузки; 40 с симуляции"),
        ["C64LoadScene.loadMin"] = new(Frames: 2400, Reason: "конец загрузки зависит от loadMin; 40 с симуляции"),
        ["PlasmaScene.paletteInterval"] = new(Frames: 600, Reason: "смена палитры раз в paletteInterval; 10 с симуляции"),
        ["AnalogClockScene.sweep"] = new(Skip: true, Reason: "проверяется подставными часами: SweepChangesSecondHandMotion"),
        ["WorldClockScene.seconds"] = new(Skip: true, Reason: "без снимка времени секунд нет; проверяется WorldSecondsToggleChangesRows"),
        // density=0 — пустой экран по определению (существующие тесты MatrixSceneTests на это опираются).
        ["MatrixScene.density"] = new(EmptyAtMinOk: true, Reason: "Min=0 — дождь выключен, экран пуст"),
        // Удары: без ударов звука сцена не реагирует, поэтому подаём удары.
        ["RotozoomScene.beatsPerReverse"] = new(Beats: true, Reason: "разворот считается по ударам звука"),
        ["RotozoomScene.punchScale"] = new(Beats: true, Reason: "сжатие масштаба — на удар звука"),
        ["CracktroScene.bounce"] = new(Beats: true, Reason: "отскок логотипа — на удар звука"),
        ["BbsScene.baud"] = new(Frames: 900, Reason: "печать идёт baud/10 симв./с; разница видна, пока лого прорисовывается; 15 с симуляции"),
        ["BbsScene.sessionMax"] = new(Skip: true, Reason: "длина сеанса — проверяется BbsSceneTests.SessionMaxControlsLength"),
        ["ScopeScene.figures"] = new(Frames: 2400, Reason: "фигура раз в 20–40 с; 40 с симуляции"),
        // Данные сети: без DataSnapshot сцена не переходит к следующим кадрам/страницам. Проверяется в тестах сцены.
        ["TeletextScene.searchTime"] = new(Skip: true, Reason: "проверяется подставными данными: TeletextSearchTimeChangesTiming"),
        ["TeletextScene.holdMin"] = new(Skip: true, Reason: "проверяется подставными данными: TeletextHoldMinChangesPageChanges"),
        ["SatelliteScene.frameStep"] = new(Skip: true, Reason: "проверяется подставными данными: SatelliteFrameStepChangesTimelapse"),
        ["SatelliteScene.holdSec"] = new(Skip: true, Reason: "проверяется подставными данными: SatelliteHoldSecChangesLastFramePause"),
        ["TeletypeScene.idleSec"] = new(Skip: true, Reason: "проверяется подставными данными: TeletypeIdleSecChangesRestartDelay"),
        ["TeletypeScene.gapSec"] = new(Skip: true, Reason: "проверяется подставными данными: TeletypeGapSecChangesPauseBetweenItems"),
        ["WeatherBoardScene.stepSec"] = new(Skip: true, Reason: "проверяется подставными данными: WeatherStepSecChangesFlipSpeed"),
        ["SatTvScene.scrollSec"] = new(Skip: true, Reason: "проверяется подставными данными: ScrollSecParamSetsStepLength"),
        ["SatTvScene.maxChannels"] = new(Skip: true, Reason: "проверяется подставными данными: MaxChannelsLimitsChannelsPerEpisode"),
        ["SatTvScene.style"] = new(Skip: true, Reason: "проверяется подставными данными: ThreeStylesGiveDifferentBackgrounds"),
        ["FiresScene.flicker"] = new(Skip: true, Reason: "мерцание видно только на очагах: проверяется подставными данными FIRMS (FiresSceneTests)"),
        ["RadarScene.labels"] = new(Skip: true, Reason: "проверяется подставными данными: LabelsParamTogglesFormulary"),
    };

    private readonly ITestOutputHelper _out;

    public SceneParamsTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> SceneTypes() => ParamSceneTypes().Select(t => new object[] { t });

    public static IEnumerable<object[]> ParamCases() =>
        ParamSceneTypes().SelectMany(t => Specs(t).Select((_, i) => new object[] { t, i }));

    [Fact]
    public void AtLeastEightScenesDeclareParams() =>
        Assert.True(ParamSceneTypes().Count() >= 8, "Ожидалось не меньше 8 сцен с ISceneParams");

    [Theory]
    [MemberData(nameof(SceneTypes))]
    public void SpecsAreWellFormed(Type t)
    {
        var specs = Specs(t);
        Assert.InRange(specs.Count, 1, 4);
        Assert.Equal(specs.Count, specs.Select(p => p.Name).Distinct().Count());

        foreach (var p in specs)
        {
            string where = $"{t.Name}.{p.Name}";
            Assert.False(string.IsNullOrWhiteSpace(p.Name), $"{t.Name}: пустое имя параметра");
            Assert.True(p.Description.Length > 0 && p.Description.Length <= 50,
                $"{where}: описание {p.Description.Length} символов (нужно 1..50)");
            Assert.True(p.Min <= p.Default && p.Default <= p.Max,
                $"{where}: Min {p.Min} ≤ Default {p.Default} ≤ Max {p.Max} нарушено");

            switch (p.Kind)
            {
                case SceneParamKind.Choice:
                    Assert.True(p.Options is { Length: > 0 }, $"{where}: у Choice пустые Options");
                    Assert.Equal(0f, p.Min);
                    Assert.Equal((float)(p.Options!.Length - 1), p.Max);
                    Assert.True(p.Default == MathF.Floor(p.Default), $"{where}: индекс Default не целый");
                    break;
                case SceneParamKind.Bool:
                    Assert.Equal(0f, p.Min);
                    Assert.Equal(1f, p.Max);
                    Assert.True(p.Default == 0f || p.Default == 1f, $"{where}: Bool Default должен быть 0 или 1");
                    break;
                case SceneParamKind.Int:
                    Assert.True(p.Min == MathF.Floor(p.Min) && p.Max == MathF.Floor(p.Max) && p.Default == MathF.Floor(p.Default),
                        $"{where}: у Int границы и Default должны быть целыми");
                    break;
            }
        }
    }

    [Theory]
    [MemberData(nameof(SceneTypes))]
    public void ParamsListIsStable(Type t)
    {
        var s = (ISceneParams)NewScene(t);
        Assert.Same(s.Params, s.Params);
    }

    [Theory]
    [MemberData(nameof(ParamCases))]
    public void MinAndMaxRenderVisibleGrid(Type t, int index)
    {
        var p = Specs(t)[index];
        var sp = SpecialFor(t, p);
        foreach (var v in new[] { p.Min, p.Max })
        {
            if (v == p.Min && sp.EmptyAtMinOk) continue; // Min означает «ничего не рисовать» (density=0)
            var g = Render(t, Opts((p, v)), sp.Frames, 11, sp.Beats);
            Assert.True(g.Cells.Any(Visible), $"{t.Name}.{p.Name}={v}: сетка после {sp.Frames} кадров пустая");
        }
    }

    [Theory]
    [MemberData(nameof(ParamCases))]
    public void MinAndMaxChangeTheOutput(Type t, int index)
    {
        var p = Specs(t)[index];
        var sp = SpecialFor(t, p);
        if (sp.Skip)
        {
            _out.WriteLine($"пропуск {t.Name}.{p.Name}: {sp.Reason}");
            return;
        }

        var a = Render(t, Opts((p, p.Min)), sp.Frames, 22, sp.Beats);
        var b = Render(t, Opts((p, p.Max)), sp.Frames, 22, sp.Beats);
        Assert.False(a.Cells.SequenceEqual(b.Cells),
            $"{t.Name}.{p.Name}: Min и Max дают одинаковую сетку через {sp.Frames} кадров — параметр не влияет");
    }

    [Fact]
    [Trait("Category", "Diagnostics")]
    public void PrintParamsTable()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Сцена | Параметр | Тип | Диапазон | По умолчанию | Описание |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var t in ParamSceneTypes())
        {
            string id = NewScene(t).Id;
            foreach (var p in Specs(t))
                sb.AppendLine($"| {id} | `{p.Name}` | {KindName(p)} | {Range(p)} | {DefaultText(p)} | {p.Description} |");
        }

        var path = Path.Combine(Path.GetTempPath(), "phosphor-scene-params.md");
        File.WriteAllText(path, sb.ToString());
        _out.WriteLine(sb.ToString());
        _out.WriteLine(path);
    }

    // ---------- параметры сцен с данными: эффект проверяется подставными DataSnapshot ----------

    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TeletextSearchTimeChangesTiming()
    {
        // 2 страницы, пауза страницы 2 с (минимум): 15 с симуляции проходят несколько переключений.
        var a = Trace(() => new TeletextScene(), FeedTeletext, 40, 25, 900, 3, (TeletextScene.HoldSpec, 2f), (TeletextScene.SearchSpec, 0f));
        var b = Trace(() => new TeletextScene(), FeedTeletext, 40, 25, 900, 3, (TeletextScene.HoldSpec, 2f), (TeletextScene.SearchSpec, 5f));
        Assert.False(a.SequenceEqual(b), "searchTime не меняет показ страниц за 15 с");
    }

    [Fact]
    public void TeletextHoldMinChangesPageChanges()
    {
        // Короткая пауза (2 с) и длинная (60 с): за 15 с первая листает страницы, вторая стоит на первой.
        var a = Trace(() => new TeletextScene(), FeedTeletext, 40, 25, 900, 3, (TeletextScene.HoldSpec, 2f), (TeletextScene.SearchSpec, 1.2f));
        var b = Trace(() => new TeletextScene(), FeedTeletext, 40, 25, 900, 3, (TeletextScene.HoldSpec, 60f), (TeletextScene.SearchSpec, 1.2f));
        Assert.False(a.SequenceEqual(b), "holdMin не меняет переключение страниц за 15 с");
    }

    [Fact]
    public void SatelliteFrameStepChangesTimelapse()
    {
        // 4 кадра: шаг 0.1 с против 3 с за 30 с симуляции.
        var a = Trace(() => new SatelliteScene(), FeedSatellite, 120, 40, 1800, 6, (SatelliteScene.FrameSpec, 0.1f), (SatelliteScene.HoldSpec, 2f));
        var b = Trace(() => new SatelliteScene(), FeedSatellite, 120, 40, 1800, 6, (SatelliteScene.FrameSpec, 3f), (SatelliteScene.HoldSpec, 2f));
        Assert.False(a.SequenceEqual(b), "frameStep не меняет таймлапс за 30 с");
    }

    [Fact]
    public void SatelliteHoldSecChangesLastFramePause()
    {
        // Пауза на последнем кадре: 0 против 10 с за 30 с симуляции.
        var a = Trace(() => new SatelliteScene(), FeedSatellite, 120, 40, 1800, 6, (SatelliteScene.FrameSpec, 0.6f), (SatelliteScene.HoldSpec, 0f));
        var b = Trace(() => new SatelliteScene(), FeedSatellite, 120, 40, 1800, 6, (SatelliteScene.FrameSpec, 0.6f), (SatelliteScene.HoldSpec, 10f));
        Assert.False(a.SequenceEqual(b), "holdSec не меняет паузу таймлапса за 30 с");
    }

    [Fact]
    public void TeletypeIdleSecChangesRestartDelay()
    {
        // Все новости напечатаны, затем простой. Простой 5 с — лента повторяется; 120 с — ещё молчит.
        var a = Trace(() => new TeletypeScene(), FeedTeletype, 80, 25, 2400, 6, (TeletypeScene.IdleSpec, 5f), (TeletypeScene.GapSpec, 1.5f));
        var b = Trace(() => new TeletypeScene(), FeedTeletype, 80, 25, 2400, 6, (TeletypeScene.IdleSpec, 120f), (TeletypeScene.GapSpec, 1.5f));
        Assert.False(a.SequenceEqual(b), "idleSec не меняет повтор ленты за 40 с");
    }

    [Fact]
    public void TeletypeGapSecChangesPauseBetweenItems()
    {
        // Пауза между служебными строками (нет новостей, лента печатает служебные записи): 0 против 10 с за 40 с.
        var a = Trace(() => new TeletypeScene(), FeedTeletypeEmpty, 80, 25, 2400, 6, (TeletypeScene.IdleSpec, 30f), (TeletypeScene.GapSpec, 0f));
        var b = Trace(() => new TeletypeScene(), FeedTeletypeEmpty, 80, 25, 2400, 6, (TeletypeScene.IdleSpec, 30f), (TeletypeScene.GapSpec, 10f));
        Assert.False(a.SequenceEqual(b), "gapSec не меняет паузу между новостями за 40 с");
    }

    [Fact]
    public void WeatherStepSecChangesFlipSpeed()
    {
        // Перелистывание табло после данных: шаг 0.01 с против 0.2 с, первые 4 с.
        var a = Trace(() => new WeatherBoardScene(), FeedWeather, 120, 36, 240, 2, (WeatherBoardScene.StepSpec, 0.01f));
        var b = Trace(() => new WeatherBoardScene(), FeedWeather, 120, 36, 240, 2, (WeatherBoardScene.StepSpec, 0.2f));
        Assert.False(a.SequenceEqual(b), "stepSec не меняет перелистывание табло за 4 с");
    }

    private static void FeedTeletext(IScene s)
    {
        var d = (IDataScene)s;
        d.OnData("clock", new ClockSnapshot(new DateTime(2026, 10, 9, 4, 20, 15)));
        d.OnData("teletext", new TeletextSnapshot(new[] { TtPage(101, "PAGE ONE"), TtPage(102, "PAGE TWO") }, T0, false));
    }

    /// <summary>Страница 40×25: заголовок в строке 0 на синем фоне, текст в строках 2..10.</summary>
    private static TeletextPage TtPage(int page, string title)
    {
        const int R = 25, C = 40;
        var chars = new char[R * C];
        var fg = new byte[R * C];
        var bg = new byte[R * C];
        var mosaic = new byte[R * C];
        Array.Fill(chars, ' ');
        Array.Fill(fg, (byte)7);
        for (int x = 0; x < C; x++)
        {
            bg[x] = 4;
            if (x < title.Length) chars[x] = title[x];
        }
        for (int r = 2; r <= 10; r++)
        {
            string line = $"PAGE {page} LINE {r}";
            for (int x = 0; x < line.Length && x < C; x++) chars[r * C + x] = line[x];
        }
        return new TeletextPage("NOS", "NL", page, 0, R, C, chars, fg, bg, mosaic);
    }

    private static void FeedSatellite(IScene s)
    {
        var area = new SatelliteArea(SatelliteProjection.Equirect, -180, -90, 180, 90, 0, 1, 55.75, 37.62, "Москва");
        var frames = new SatelliteFrame[4];
        for (int i = 0; i < frames.Length; i++)
        {
            var rgb = new byte[8 * 4 * 3];
            Array.Fill(rgb, (byte)(40 + i * 50));
            frames[i] = new SatelliteFrame(T0.AddMinutes(10 * i), 8, 4, rgb);
        }
        ((IDataScene)s).OnData("satellite", new SatelliteSetSnapshot(
            new[] { new SatelliteFeed("viirs", "VIIRS", area, frames, T0, false) }, T0, false));
    }

    [Fact]
    public void AquariumFishDensityScalesAutoCount()
    {
        // Авто на 120×40: max(3, 4800/900) = 5 рыб. Множитель 2 → 10; старый абсолютный ключ fish=7 важнее множителя.
        static int Fish(SceneOptions o)
        {
            var s = (AquariumScene)SceneContract.Make(() => new AquariumScene(), 120, 40, 3, o);
            return s.FishPositions().Count();
        }
        Assert.Equal(5, Fish(Opts((AquariumScene.FishDensitySpec, 1f))));
        Assert.Equal(10, Fish(Opts((AquariumScene.FishDensitySpec, 2f))));
        Assert.Equal(7, Fish(new SceneOptions { Params = JsonSerializer.SerializeToElement(new { fish = 7, fishDensity = 2f }) }));
    }

    private static void FeedTeletypeEmpty(IScene s)
    {
        ((IDataScene)s).OnData("clock", new ClockSnapshot(new DateTime(2026, 10, 9, 4, 20, 15)));
        ((IDataScene)s).OnData("news", new NewsSnapshot(Array.Empty<NewsItem>(), T0, false));
    }

    private static void FeedTeletype(IScene s)
    {
        var d = (IDataScene)s;
        d.OnData("clock", new ClockSnapshot(new DateTime(2026, 10, 9, 4, 20, 15)));
        d.OnData("news", new NewsSnapshot(new[]
        {
            new NewsItem("ТАСС", "Правительство утвердило план развития регионов", T0, "ru"),
            new NewsItem("BBC", "Talks on the new trade deal resume in Geneva", T0, "en"),
            new NewsItem("REUTERS", "Markets close higher after a volatile session", T0, "en"),
        }, T0, false));
    }

    private static void FeedWeather(IScene s)
    {
        var d = (IDataScene)s;
        d.OnData("clock", new ClockSnapshot(new DateTime(2026, 10, 9, 4, 20, 0)));
        var start = new DateOnly(2026, 10, 9);
        var days = new List<WeatherDay>();
        for (int i = 0; i < 7; i++)
            days.Add(new WeatherDay(start.AddDays(i), -2 + i, 8 + i, 0.5 * i, 3));
        d.OnData("weather", new WeatherSnapshot("Москва", 55.75, 37.62,
            new WeatherNow(7.4, 5.4, 45, 3), days, T0, false));
    }

    /// <summary>Прогон сцены с заданными параметрами: хэш сетки каждые every кадров. Одинаковые последовательности = параметр не влияет.</summary>
    private static int[] Trace(Func<IScene> make, Action<IScene> feed, int cols, int rows, int frames, int every,
        params (SceneParamSpec Spec, float Value)[] p)
    {
        var s = SceneContract.Make(make, cols, rows, 31, Opts(p));
        feed(s);
        var list = new List<int>();
        for (int i = 0; i < frames; i++)
        {
            s.Update(SceneContract.Dt);
            if (i % every == 0) list.Add(Hash(SceneContract.Snap(s, cols, rows)));
        }
        return list.ToArray();
    }

    private static int Hash(CharGrid g)
    {
        var h = new HashCode();
        foreach (var c in g.Cells)
        {
            h.Add(c.Ch);
            h.Add(c.Fg.R); h.Add(c.Fg.G); h.Add(c.Fg.B);
            h.Add(c.Bg.R); h.Add(c.Bg.G); h.Add(c.Bg.B);
        }
        return h.ToHashCode();
    }

    // ---------- помощники ----------

    private static IEnumerable<Type> ParamSceneTypes() => typeof(MatrixScene).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false }
                    && typeof(IScene).IsAssignableFrom(t)
                    && typeof(ISceneParams).IsAssignableFrom(t)
                    && t.GetConstructor(Type.EmptyTypes) != null
                    // Сцена-наследник может не иметь своих параметров (vumeter, mediaplayer)
                    && ((ISceneParams)Activator.CreateInstance(t)!).Params.Count > 0)
        .OrderBy(t => t.FullName, StringComparer.Ordinal);

    private static IReadOnlyList<SceneParamSpec> Specs(Type t) => ((ISceneParams)NewScene(t)).Params;

    private static IScene NewScene(Type t) => (IScene)Activator.CreateInstance(t)!;

    private static SceneOptions Opts(params (SceneParamSpec Spec, float Value)[] items)
    {
        var dict = new Dictionary<string, object>();
        foreach (var (spec, v) in items) dict[spec.Name] = JsonValue(spec, v);
        return new SceneOptions { Params = JsonSerializer.SerializeToElement(dict) };
    }

    private static object JsonValue(SceneParamSpec spec, float v) => spec.Kind switch
    {
        SceneParamKind.Int => (int)v,
        SceneParamKind.Bool => v >= 0.5f,
        SceneParamKind.Choice => spec.Options![(int)v],
        _ => v,
    };

    private static Special SpecialFor(Type t, SceneParamSpec p) =>
        Specials.TryGetValue($"{t.Name}.{p.Name}", out var sp) ? sp : new Special();

    private static CharGrid Render(Type t, SceneOptions o, int frames, int seed, bool beats)
    {
        var s = SceneContract.Make(() => NewScene(t), Cols, Rows, seed, o);
        var audio = beats ? s as IAudioReactive : null;
        for (int i = 0; i < frames; i++)
        {
            if (audio != null)
            {
                bool beat = i % 30 == 0;
                audio.OnAudio(new AudioFrame(true, 0.5f, 0.5f, 0.5f, 0.5f, beat, beat ? 1f : 0f, 120f));
            }
            s.Update(SceneContract.Dt);
        }
        return SceneContract.Snap(s, Cols, Rows);
    }

    private static bool Visible(Cell c) =>
        (c.Ch != ' ' && (c.Fg.R | c.Fg.G | c.Fg.B) != 0) || (c.Bg.R | c.Bg.G | c.Bg.B) != 0;

    private static string KindName(SceneParamSpec p) => p.Kind switch
    {
        SceneParamKind.Float => "число",
        SceneParamKind.Int => "целое",
        SceneParamKind.Bool => "вкл/выкл",
        _ => "выбор",
    };

    private static string Range(SceneParamSpec p) => p.Kind switch
    {
        SceneParamKind.Bool => "вкл/выкл",
        SceneParamKind.Choice => string.Join(" / ", p.Options!),
        _ => $"{Fmt(p.Min)}..{Fmt(p.Max)}",
    };

    private static string DefaultText(SceneParamSpec p) => p.Kind switch
    {
        SceneParamKind.Bool => p.Default >= 0.5f ? "вкл" : "выкл",
        SceneParamKind.Choice => p.Options![(int)p.Default],
        _ => Fmt(p.Default),
    };

    private static string Fmt(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
