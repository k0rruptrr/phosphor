using Phosphor.Core;
using Phosphor.Gfx;
using Phosphor.Platform;
using Phosphor.Text;
using Silk.NET.OpenGL;
using StbImageWriteSharp;

namespace Phosphor.App;

/// <summary>
/// --shot out.png [--scene id] [--preset name] [--frames N] [--size WxH] [--seed N]
/// Рендерит N кадров с фиксированным dt=1/60 в невидимом окне и сохраняет последний в PNG.
/// Детерминированно при одинаковом seed — для ревью и регрессий.
/// </summary>
internal static class Shot
{
    public static unsafe int Run(LaunchArgs args, PhosphorConfig config, Log log)
    {
        using var win = ScreensaverWindow.CreateHidden(args.Width, args.Height);
        var gl = win.Gl;
        log.Info($"shot: GL {gl.GetStringS(StringName.Version)} / {gl.GetStringS(StringName.Renderer)}");

        using var presenter = new Presenter(gl);
        var scenes = Catalog.Scenes();
        using var data = DataWiring.CreateHub(config, online: config.Network.Enabled, log);
        var overlays = Catalog.Overlays();
        using var shared = new Shared(gl, config, scenes, Catalog.Filters(), overlays, data, new Random(args.Seed), log) { GridPasses = Catalog.GridPasses() };

        string sceneId = args.Scene ?? scenes.Ids.First();
        string preset = args.Preset ?? config.Filters.Active;

        // Сцене с данными — дождаться их (кеш появляется сразу, сеть — до 25 с), чтобы снимок был с настоящими данными.
        if (scenes.Create(sceneId) is IDataScene ds)
        {
            var wait = System.Diagnostics.Stopwatch.StartNew();
            var keys = ds.DataKeys.Where(k => k != "clock").ToList();
            while (wait.Elapsed.TotalSeconds < 25 && keys.Any(k => !data.TryGet(k, out _, out _))) Thread.Sleep(200);
            log.Info($"shot: данные {string.Join(", ", keys.Select(k => k + (data.TryGet(k, out _, out _) ? "=есть" : "=нет")))} за {wait.Elapsed.TotalSeconds:F1} с");
        }

        // Оверлей в снимке — только явно (env PHOSPHOR_OVERLAY=hud), чтобы снимки были воспроизводимы.
        string? overlay = Environment.GetEnvironmentVariable("PHOSPHOR_OVERLAY") is { Length: > 0 } ov ? ov : null;
        if (overlay is not null) log.Info(overlays.ContainsKey(overlay) ? $"shot: оверлей {overlay}" : $"shot: оверлей '{overlay}' не найден (есть: {string.Join(", ", overlays.Keys)})");
        using var slot = new MonitorSlot(shared, 0, 0, 0, args.Width, args.Height, sceneId, preset, config.CellHeightPx, overlay);
        // Дирижёр зафиксирован (снимок детерминирован); смену можно вызвать на кадре N: env PHOSPHOR_SWITCH_AT/_TO/_KIND.
        // Синтетическая музыка (env PHOSPHOR_FAKE_BPM=120): ровный бит — для проверки реакции сцен и смен в такт.
        // С ней дирижёр сцену не фиксирует: смены по фразам должны идти, как под настоящую музыку.
        float fakeBpm = float.TryParse(Environment.GetEnvironmentVariable("PHOSPHOR_FAKE_BPM"), System.Globalization.CultureInfo.InvariantCulture, out var b) ? b : 0;
        var director = new Director(shared, new List<MonitorSlot> { slot }, lockScene: fakeBpm <= 0, lockPreset: true);
        var clock = new DateTime(2026, 10, 9, 4, 20, 0);

        const float dt = 1f / 60f;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int lastBeat = -1;
        for (int f = 0; f < args.Frames; f++)
        {
            var audio = AudioFrame.Silent;
            if (fakeBpm > 0)
            {
                float t = f * dt, period = 60f / fakeBpm;
                int beatNo = (int)(t / period);
                float since = t - beatNo * period, pulse = MathF.Exp(-since / 0.15f);
                audio = new AudioFrame(true, 0.6f, 0.3f + 0.5f * pulse, 0.4f, 0.3f, beatNo != lastBeat, pulse, fakeBpm);
                lastBeat = beatNo;
            }
            director.Update(dt, f, audio);
            slot.Tick(dt, f * dt, f, audio, director.OrganismFor(0, clock), 1);
        }
        gl.Finish();
        double ms = sw.Elapsed.TotalMilliseconds / Math.Max(1, args.Frames);

        using var final = new RenderTarget(gl, args.Width, args.Height);
        presenter.Draw(slot.Output, final.Fbo, 0, 0, args.Width, args.Height);

        var px = new byte[args.Width * args.Height * 4];
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, final.Fbo);
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        fixed (byte* p = px)
            gl.ReadPixels(0, 0, (uint)args.Width, (uint)args.Height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        var err = gl.GetError();
        if (err != GLEnum.NoError) throw new InvalidOperationException($"GL error после ReadPixels: {err}");

        // GL отдаёт строки снизу вверх — переворачиваем для PNG
        int stride = args.Width * 4;
        var flipped = new byte[px.Length];
        for (int y = 0; y < args.Height; y++)
            System.Buffer.BlockCopy(px, (args.Height - 1 - y) * stride, flipped, y * stride, stride);

        var outPath = Path.GetFullPath(args.ShotPath!);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using (var fs = File.Create(outPath))
            new ImageWriter().WritePng(flipped, args.Width, args.Height, ColorComponents.RedGreenBlueAlpha, fs);

        log.Info($"shot: {sceneId} / {preset} / {args.Frames} кадров, {ms:F2} мс/кадр (CPU+GPU) → {outPath}");
        return 0;
    }
}
