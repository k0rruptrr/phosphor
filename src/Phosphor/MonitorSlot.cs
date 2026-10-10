using Phosphor.Core;
using Phosphor.Data;
using Phosphor.Gfx;
using Phosphor.Overlays;
using Phosphor.Text;
using Silk.NET.OpenGL;

namespace Phosphor.App;

/// <summary>Атлас + рендерер сетки для одной высоты ячейки.</summary>
internal sealed record Font(GlyphAtlas Atlas, GridRenderer Renderer);

/// <summary>Общие для всех слотов ресурсы.</summary>
internal sealed class Shared(GL gl, PhosphorConfig config, SceneRegistry scenes, FilterRegistry filters,
    IReadOnlyDictionary<string, Func<IOverlay>> overlays, DataHub data, Random rng, Log log) : IDisposable
{
    public GL Gl { get; } = gl;
    public PhosphorConfig Config { get; } = config;
    public SceneRegistry Scenes { get; } = scenes;
    public FilterRegistry Filters { get; } = filters;
    public IReadOnlyDictionary<string, Func<IOverlay>> Overlays { get; } = overlays;
    public DataHub Data { get; } = data;
    public Random Rng { get; } = rng;
    public Log Log { get; } = log;
    /// <summary>Последний замер fps (для оверлея).</summary>
    public float Fps { get; set; } = 60f;
    public Transition Transition { get; } = new(gl);
    /// <summary>Шаги обработки сетки (до GPU-фильтров), по id.</summary>
    public IReadOnlyDictionary<string, Func<IGridPass>> GridPasses { get; init; } = new Dictionary<string, Func<IGridPass>>();

    private readonly Dictionary<int, Font> _fonts = new();

    /// <summary>Шрифт заданной высоты ячейки (8..64). Атласы кешируются: строятся один раз на размер.</summary>
    public Font FontFor(int cellHeightPx)
    {
        cellHeightPx = Math.Clamp(cellHeightPx, 8, 64);
        if (_fonts.TryGetValue(cellHeightPx, out var f)) return f;
        var atlas = GlyphAtlas.Build(Gl, cellHeightPx);
        if (atlas.Missing.Count > 0) Log.Warn($"атлас {cellHeightPx}px: нет глифов {new string(atlas.Missing.ToArray())}");
        f = new Font(atlas, new GridRenderer(Gl, atlas));
        _fonts[cellHeightPx] = f;
        Log.Info($"атлас {cellHeightPx}px: ячейка {atlas.CellWidth}x{atlas.CellHeight}");
        return f;
    }

    public void Dispose()
    {
        foreach (var f in _fonts.Values) { f.Renderer.Dispose(); f.Atlas.Dispose(); }
        _fonts.Clear();
        Transition.Dispose();
    }
}

/// <summary>
/// Один экран (монитор, окно превью или окно разработки). Держит текущий слой сцены и, во время смены, входящий;
/// смешивает их шейдером перехода. Viewport — в координатах фреймбуфера окна (от левого НИЗА).
/// </summary>
internal sealed class MonitorSlot : IDisposable
{
    private readonly Shared _s;
    private readonly int _defaultCellHeight;
    public int Index { get; }
    public int VpX { get; private set; }
    public int VpY { get; private set; }
    public int VpW { get; private set; }
    public int VpH { get; private set; }

    private SceneLayer _current;
    private SceneLayer? _incoming;
    private RenderTarget? _composite;
    /// <summary>Видик монитора: одна лента на весь монитор, от старта заставки через все сцены. Null — если видика нет в конфиге.</summary>
    private readonly VcrOverlay? _vcr;
    private OsdLayer? _osd;
    private TransitionKind _kind;
    private float _transT, _transDur;

    public SceneLayer Current => _current;
    public SceneLayer? Incoming => _incoming;
    public bool InTransition => _transDur > 0;
    /// <summary>0 вне перехода, 0→1→0 во время (для Organism.Transition).</summary>
    public float TransitionPulse => InTransition ? MathF.Sin(MathF.PI * Math.Clamp(_transT / _transDur, 0, 1)) : 0f;
    public uint Output { get; private set; }

    public MonitorSlot(Shared s, int index, int vpX, int vpY, int vpW, int vpH, string sceneId, string preset, int defaultCellHeight, string? overlayId)
    {
        _s = s;
        Index = index;
        _defaultCellHeight = defaultCellHeight;
        VpX = vpX; VpY = vpY; VpW = vpW; VpH = vpH;
        _current = new SceneLayer(s, index, vpW, vpH, sceneId, preset, defaultCellHeight, overlayId, null);

        // OSD видика не берёт rng, чтобы не сдвигать последовательность для остальных слоёв
        if (s.Config.Overlay.Vcr && s.Overlays.TryGetValue("vcr", out var makeVcr) && makeVcr() is VcrOverlay vcr)
        {
            _vcr = vcr;
            _osd = new OsdLayer(s, vpW, vpH);
            _vcr.Init(_osd.Cols, _osd.Rows, new Random(index));
        }
    }

    /// <summary>Начать смену сцены. Cut — мгновенно. handoff — передать новой сцене последний кадр старой.</summary>
    public void BeginSceneSwitch(string sceneId, string preset, TransitionKind kind, float duration, string? overlayId, bool handoff)
    {
        FinishTransition();
        var layer = new SceneLayer(_s, Index, VpW, VpH, sceneId, preset, _defaultCellHeight, overlayId, handoff ? _current.Grid : null);
        if (kind == TransitionKind.Cut || duration <= 0)
        {
            _current.Dispose();
            _current = layer;
            return;
        }
        _incoming = layer;
        _kind = kind;
        _transT = 0;
        _transDur = duration;
        _s.Log.Info($"[{Index}] переход {_current.Scene.Id} → {sceneId} ({kind}, {duration:F1} с)");
    }

    /// <summary>Сменить пресет на текущей сцене с коротким сбоем картинки.</summary>
    public void BeginPresetSwitch(string preset, float duration)
    {
        FinishTransition();
        _current.SetPreset(preset);
        _kind = TransitionKind.Glitch;
        _transT = 0;
        _transDur = duration;
    }

    private void FinishTransition()
    {
        if (_incoming is not null)
        {
            _current.Dispose();
            _current = _incoming;
            _incoming = null;
        }
        _transDur = 0;
    }

    public void Resize(int vpX, int vpY, int vpW, int vpH)
    {
        VpX = vpX; VpY = vpY;
        if (vpW == VpW && vpH == VpH) return;
        VpW = vpW; VpH = vpH;
        FinishTransition();
        var id = _current.Scene.Id; var preset = _current.PresetName; var ov = _current.Overlay?.Id;
        _current.Dispose();
        _current = new SceneLayer(_s, Index, vpW, vpH, id, preset, _defaultCellHeight, ov, null);
        _composite?.Dispose();
        _composite = null;
        // OSD — под новый размер окна; состояние ленты (_vcr) сохраняется
        if (_vcr is not null)
        {
            _osd?.Dispose();
            _osd = new OsdLayer(_s, vpW, vpH);
        }
    }

    public void Tick(float dt, float time, long frame, in AudioFrame audio, in Organism organism, int monitorCount)
    {
        _current.Render(dt, frame, audio, organism, monitorCount);
        if (InTransition) _incoming?.Render(dt, frame, audio, organism, monitorCount);

        // Видик — один на монитор: лента идёт всегда. OSD кладётся на тот слой, у которого видик включён
        // (во время перехода входящий — приоритетнее), один раз, до фильтров слоя.
        if (_vcr is not null && _osd is not null)
        {
            SceneLayer? owner = _incoming is { VcrOn: true } ? _incoming : _current.VcrOn ? _current : null;
            _vcr.SetOn(owner is not null);
            _vcr.Update(dt);
            if (owner is not null)
            {
                _s.Data.TryGet("clock", out var clk, out _);
                var ctx = new OverlayContext(owner.Scene.Id, owner.PresetName, Index, monitorCount, _s.Fps, frame,
                    (clk as ClockSnapshot)?.Local, audio, organism);
                _osd.Compose(owner.Target, _vcr, ctx);
            }
        }

        _current.Filter(time, dt, frame, audio, organism);
        if (!InTransition)
        {
            Output = _current.Output;
            return;
        }

        _incoming?.Filter(time, dt, frame, audio, organism);
        _transT += dt;
        _composite ??= new RenderTarget(_s.Gl, VpW, VpH);
        float p = Math.Clamp(_transT / _transDur, 0f, 1f);
        _s.Transition.Render(_current.Output, _incoming?.Output ?? _current.Output, _composite, _kind, p, time);
        Output = _composite.Texture;
        if (_transT >= _transDur) FinishTransition();
    }

    public void Dispose()
    {
        _incoming?.Dispose();
        _current.Dispose();
        _composite?.Dispose();
        _osd?.Dispose();
    }
}
