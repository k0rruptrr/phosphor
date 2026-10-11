using System.Diagnostics;
using System.Runtime.InteropServices;
using Phosphor.Core;
using Phosphor.Gfx;
using Phosphor.Platform;
using Phosphor.Text;
using Silk.NET.OpenGL;

namespace Phosphor.App;

internal static class Program
{
    private const float MaxDt = 0.1f;

    [STAThread]
    private static int Main(string[] argv)
    {
        using var log = new Log();
        LaunchArgs args;
        try { args = LaunchArgs.Parse(argv); }
        catch (ArgumentException e)
        {
            log.Error(e.Message);
            MessageBoxW(0, e.Message, "PHOSPHOR", 0x10);
            return 2;
        }
        log.Info($"PHOSPHOR {AppInfo.Display} {args.Mode} argv=[{string.Join(' ', argv)}]");
        // --overlay: hud — env PHOSPHOR_OVERLAY (его читают PickOverlay, Director и Shot); vcr — флаг видика (читает Shared)
        if (string.Equals(args.Overlay, "hud", StringComparison.Ordinal)) Environment.SetEnvironmentVariable("PHOSPHOR_OVERLAY", "hud");
        else if (string.Equals(args.Overlay, "vcr", StringComparison.Ordinal)) Environment.SetEnvironmentVariable("PHOSPHOR_VCR_FORCE", "1");
        // --overlay clock|logo — бирка включена принудительно (env PHOSPHOR_BADGE читает Shared)
        else if (args.Overlay is "clock" or "logo") Environment.SetEnvironmentVariable("PHOSPHOR_BADGE", args.Overlay);
        else if (args.Overlay is not null) log.Warn($"--overlay: неизвестный оверлей '{args.Overlay}' (есть: hud, vcr, clock, logo)");

        try
        {
            Dpi.EnsurePerMonitorAware();
            var warnings = new List<string>();
            // --config <путь> — вместо phosphor.json рядом с exe (превью окна настроек передаёт свой временный конфиг)
            var cfgPath = args.ConfigPath is { Length: > 0 } custom ? Path.GetFullPath(custom) : Path.Combine(AppContext.BaseDirectory, "phosphor.json");
            var config = PhosphorConfig.Load(cfgPath, warnings);
            foreach (var w in warnings) log.Warn(w);

            // Геометрия кинескопа ушла из пресетов в экран (screen): убираем старые ключи crt в памяти во всех режимах
            var notes = new List<string>();
            bool screenMigrated = config.MigrateScreen(notes);
            // signal → interference (SECAM): шкала инвертирована, пресеты переписываются в памяти во всех режимах
            bool tvMigrated = config.MigrateTvSignal(notes);
            // Старый конфиг: новые сцены из обновления включаются сами и запоминаются (не для --shot и превью).
            // Файл сохраняется только в этих режимах; миграция экрана сохраняется вместе с ними.
            if (args.Mode is LaunchMode.Screensaver or LaunchMode.Windowed or LaunchMode.Configure)
            {
                bool scenesMigrated = config.MigrateScenes(Catalog.Scenes().Ids, notes);
                if (scenesMigrated || screenMigrated || tvMigrated)
                {
                    foreach (var n in notes) log.Info("конфиг: " + n);
                    try { config.Save(cfgPath); log.Info("конфиг: сохранён " + cfgPath); }
                    catch (Exception e) { log.Warn($"конфиг: не удалось сохранить ({e.Message}), работаю с мигрированным в памяти"); }
                }
            }
            else foreach (var n in notes) log.Info("конфиг: " + n);

            return args.Mode switch
            {
                LaunchMode.Configure => Configure(args, config, cfgPath, log),
                LaunchMode.Shot => Shot.Run(args, config, log),
                LaunchMode.SettingsShot => Settings.SettingsForm.RenderShot(args.ShotPath!, args.SettingsTab ?? "scenes", config, Catalog.Scenes().Ids, log),
                _ => RunInteractive(args, config, log),
            };
        }
        catch (Exception e)
        {
            log.Error(e.ToString());
            // Окно с ошибкой — только в интерактивной разработке. --shot работает без людей (скрипты, CI): только лог и код выхода.
            if (args.Mode is LaunchMode.Windowed)
                MessageBoxW(0, e.Message + "\n\nЛог: " + log.Path, "PHOSPHOR упал", 0x10);
            return 1;
        }
    }

    /// <summary>/c[:HWND] — окно настроек (WinForms). HWND — владелец (модальность к панели управления).</summary>
    private static int Configure(LaunchArgs args, PhosphorConfig config, string cfgPath, Log log)
    {
        if (!File.Exists(cfgPath)) File.WriteAllText(cfgPath, PhosphorConfig.DefaultJson);
        Settings.SettingsForm.Open(config, Catalog.Scenes().Ids, cfgPath, args.ParentHwnd, log);
        return 0;
    }

    private static int RunInteractive(LaunchArgs args, PhosphorConfig config, Log log)
    {
        using var win = args.Mode switch
        {
            LaunchMode.Screensaver => ScreensaverWindow.CreateFullscreen(config.Exit.MouseThresholdPx),
            LaunchMode.Preview => ScreensaverWindow.CreatePreview(args.ParentHwnd),
            _ => ScreensaverWindow.CreateWindowed(args.Width, args.Height, "PHOSPHOR"),
        };
        var gl = win.Gl;
        log.Info($"GL {gl.GetStringS(StringName.Version)} / {gl.GetStringS(StringName.Renderer)}; окно {win.ClientWidth}x{win.ClientHeight} @({win.OriginX},{win.OriginY})");

        int cellH = args.Mode == LaunchMode.Preview ? Math.Max(8, win.ClientHeight / 14) : config.CellHeightPx;
        using var presenter = new Presenter(gl);
        // Превью — без сети: только кеш (окошко в панели управления не должно ходить в интернет).
        using var data = DataWiring.CreateHub(config, online: config.Network.Enabled && args.Mode != LaunchMode.Preview, log);
        // Датчики ПК — не в превью (маленькое окно): там ключ "system" просто пуст
        using var probe = args.Mode == LaunchMode.Preview ? null : new SystemProbe(m => log.Warn(m)).Start();
        PhosphorSnapshot? phSnap = null;
        long phVer = 0;
        double phAt = 0;
        data.RegisterLive("system", () => probe?.TryGetLive());
        data.RegisterLive("phosphor", () => phSnap is null ? null : (phSnap, phVer));
        var overlays = Catalog.Overlays();
        log.Info($"оверлеи: {(overlays.Count == 0 ? "нет" : string.Join(", ", overlays.Keys))}");
        using var shared = new Shared(gl, config, Catalog.Scenes(), Catalog.Filters(), overlays, data, new Random(args.Seed ^ Environment.TickCount), log) { GridPasses = Catalog.GridPasses(), Badges = Catalog.Badges() };

        var slots = BuildSlots(win, args, shared, cellH);
        // Шлейф (screen.trail): свой буфер на слот; при trail = 0 ничего не выделяется и не рисуется
        var trails = slots.Select(_ => new TrailBuffer(gl)).ToList();
        // Корпус монитора (screen.*): свой проход на слот, после шлейфа
        var screens = slots.Select(_ => new ScreenPass(gl)).ToList();
        var outs = new uint[slots.Count];
        // --scene / --preset в /w фиксируют сцену/пресет (разработка); в /s и /p дирижёр свободен.
        var director = new Director(shared, slots, lockScene: args.Scene is not null, lockPreset: args.Preset is not null);

        // Диммер заставки (только /s): подсветка по DDC/CI там, где монитор отвечает; остальные — затемнение картинки.
        // Журнал подсветки открываем и при выключенном диммере: если прошлый запуск упал с погашенным монитором, восстановим.
        var dimmer = config.Dimmer;
        string journalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Phosphor", "backlight-journal.json");
        // Имена устройств в том же порядке, что и слоты (BuildSlots берёт Monitors.Enumerate в том же порядке)
        var devNames = args.Mode == LaunchMode.Screensaver ? Monitors.Enumerate().Select(m => m.Name).ToArray() : Array.Empty<string>();
        var gains = new float[slots.Count];
        bool dimLogged = false, dimStarted = false;
        // Dispose восстанавливает яркость: using — и при выходе, и при исключении.
        using var backlight = args.Mode == LaunchMode.Screensaver && ((dimmer.Enabled && dimmer.Hardware) || File.Exists(journalPath))
            ? Backlight.Start(journalPath, m => log.Info(m))
            : null;

        // Превью в панели управления — без звука: незачем держать захват ради окошка 150×110.
        using var audio = config.Audio.Enabled && args.Mode != LaunchMode.Preview
            ? Phosphor.Audio.AudioService.Start(config.Audio, m => log.Info("audio: " + m))
            : null;
        bool wasActive = false;
        int beats = 0;
        float lastBpm = 0;
        var sw = Stopwatch.StartNew();
        double last = 0, fpsT = 0; int fpsN = 0; long frame = 0;
        int lastW = win.ClientWidth, lastH = win.ClientHeight;

        while (true)
        {
            win.PumpEvents();
            if (win.ShouldClose) break;

            double now = sw.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(MaxDt, now - last);
            last = now;

            if (args.Mode == LaunchMode.Windowed && (win.ClientWidth != lastW || win.ClientHeight != lastH) && win.ClientWidth > 0 && win.ClientHeight > 0)
            {
                lastW = win.ClientWidth; lastH = win.ClientHeight;
                slots[0].Resize(0, 0, lastW, lastH);
            }
            if (win.ClientWidth <= 0 || win.ClientHeight <= 0) { Thread.Sleep(16); continue; }

            var af = audio?.Tick(dt) ?? AudioFrame.Silent;
            if (af.Beat) beats++;
            if (af.Bpm > 0) lastBpm = af.Bpm;
            if (af.Active != wasActive)
            {
                log.Info(af.Active ? "музыка: есть" : "музыка: тишина");
                wasActive = af.Active;
            }
            director.Update(dt, frame, af);
            var wall = DateTime.Now;
            // Самочувствие заставки для ключа "phosphor": раз в 500 мс, в главном потоке (дёшево)
            if (phSnap is null || now - phAt >= 0.5)
            {
                phSnap = director.Report(shared.Fps, af.Active ? af.Bpm : 0, TimeSpan.FromSeconds(now), frame, wall);
                phVer++;
                phAt = now;
            }
            for (int i = 0; i < slots.Count; i++)
                slots[i].Tick(dt, (float)now, frame, af, director.OrganismFor(i, wall), slots.Count);

            // Диммер: прогресс по времени заставки. Подсветка — заявки Request (только когда пора, progress > 0);
            // мониторы без DDC — множитель картинки.
            float dimProgress = args.Mode == LaunchMode.Screensaver ? DimmerCurve.Progress(dimmer, now) : 0f;
            if (dimProgress > 0 && !dimStarted) { dimStarted = true; log.Info("диммер: гашу"); }
            if (backlight is { Ready: true } bl && !dimLogged)
            {
                dimLogged = true;
                var hw = new List<string>();
                var soft = new List<string>();
                for (int i = 0; i < slots.Count; i++)
                {
                    string dev = i < devNames.Length ? devNames[i] : "?";
                    if (i < devNames.Length && bl.IsHardware(dev)) hw.Add(dev); else soft.Add(dev);
                }
                log.Info($"диммер: подсветка DDC — {(hw.Count == 0 ? "нет" : string.Join(", ", hw))}; затемнение картинки — {(soft.Count == 0 ? "нет" : string.Join(", ", soft))}");
            }
            float softGain = DimmerCurve.SoftwareGain(dimmer, dimProgress);
            for (int i = 0; i < slots.Count; i++)
            {
                gains[i] = 1f;
                string? dev = i < devNames.Length ? devNames[i] : null;
                if (backlight is { Ready: true } hwBl && dev is not null && hwBl.IsHardware(dev))
                {
                    if (dimProgress > 0)
                        hwBl.Request(dev, DimmerCurve.HardwareLevel(dimmer, OriginalLevel01(hwBl, dev), dimProgress));
                }
                else gains[i] = softGain;
            }

            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.Viewport(0, 0, (uint)win.ClientWidth, (uint)win.ClientHeight);
            gl.ClearColor(0, 0, 0, 1);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                trails[i].Resize(s.VpW, s.VpH);
                outs[i] = trails[i].Apply(s.Output, config.Screen.Trail);
                outs[i] = s.ComposeOver(outs[i], s.VpW, s.VpH);   // бирки «поверх» — после шлейфа, до корпуса
                outs[i] = screens[i].Apply(outs[i], s.VpW, s.VpH, config.Screen);
                presenter.Draw(outs[i], 0, s.VpX, s.VpY, s.VpW, s.VpH, gains[i]);
            }
            win.SwapBuffers();
            frame++;

            fpsN++;
            if (now - fpsT >= 5)
            {
                shared.Fps = (float)(fpsN / (now - fpsT));
                log.Info(audio is null
                    ? $"fps {fpsN / (now - fpsT):F1}"
                    : $"fps {fpsN / (now - fpsT):F1} · ударов {beats} за {now - fpsT:F0} с · bpm {lastBpm:F0}");
                fpsT = now; fpsN = 0; beats = 0;
            }
        }

        foreach (var t in trails) t.Dispose();
        foreach (var sp in screens) sp.Dispose();
        foreach (var s in slots) s.Dispose();
        log.Info("выход");
        return 0;
    }

    /// <summary>Исходная яркость монитора как доля диапазона [Min..Max], 0..1.</summary>
    private static float OriginalLevel01(Backlight bl, string dev)
    {
        foreach (var m in bl.Monitors)
            if (string.Equals(m.DeviceName, dev, StringComparison.OrdinalIgnoreCase))
                return (m.Original - m.Min) / (float)(m.Max - m.Min);
        return 0f;
    }

    private static List<MonitorSlot> BuildSlots(ScreensaverWindow win, LaunchArgs args, Shared s, int cellH)
    {
        foreach (var id in s.Config.Scenes.Enabled)
            if (!s.Scenes.Has(id)) s.Log.Warn($"Сцена '{id}' в конфиге, но не реализована — пропускаю");

        // Стартовые сцены: веса из Selection; на разных мониторах — разные сцены, если хватает
        var scenePool = Selection.ScenePool(s.Config, s.Scenes.Ids);
        var chosen = new List<string>();
        string PickScene(int i)
        {
            if (args.Scene is not null) return args.Scene;
            var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            double r = 0;
            if (s.Config.Shuffle.PerMonitor)
            {
                foreach (var c in chosen) exclude.Add(c);
                r = s.Rng.NextDouble();
            }
            var id = Selection.Pick(scenePool, r, exclude);
            chosen.Add(id);
            return id;
        }
        // Стартовый пресет: "shuffle" — случайный из доступных (взвешенно), иначе — указанный
        string preset = args.Preset ?? (string.Equals(s.Config.Filters.Active, FiltersConfig.ShuffleActive, StringComparison.OrdinalIgnoreCase)
            ? Selection.Pick(Selection.PresetPool(s.Config), s.Rng.NextDouble())
            : s.Config.Filters.Active);
        // Стартовый оверлей — по тем же правилам, что и дирижёр (env PHOSPHOR_OVERLAY — принудительно)
        string? PickOverlay(string sceneId) =>
            Environment.GetEnvironmentVariable("PHOSPHOR_OVERLAY") is { Length: > 0 } ov ? ov
            : s.Config.Overlay.Enabled && s.Overlays.ContainsKey(s.Config.Overlay.Id)
              && !(sceneId is "boids" or "voxel" or "teletext" or "teletype" or "weatherboard")
              && s.Rng.NextDouble() < s.Config.Overlay.Probability ? s.Config.Overlay.Id : null;

        var slots = new List<MonitorSlot>();
        if (args.Mode == LaunchMode.Screensaver)
        {
            var mons = Monitors.Enumerate();
            for (int i = 0; i < mons.Count; i++)
            {
                var m = mons[i];
                int x = m.X - win.OriginX;
                int top = m.Y - win.OriginY;
                int y = win.ClientHeight - (top + m.Height);
                s.Log.Info($"монитор {i} {m.Name} {m.Width}x{m.Height} @({m.X},{m.Y}) primary={m.Primary} → viewport ({x},{y})");
                var sc = PickScene(i);
                slots.Add(new MonitorSlot(s, i, x, y, m.Width, m.Height, sc, preset, cellH, PickOverlay(sc)));
            }
        }
        else
        {
            var sc = PickScene(0);
            slots.Add(new MonitorSlot(s, 0, 0, 0, win.ClientWidth, win.ClientHeight, sc, preset, cellH, PickOverlay(sc)));
        }
        return slots;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
}
