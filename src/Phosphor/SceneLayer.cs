using System.Text.Json;
using Phosphor.Core;
using Phosphor.Gfx;

namespace Phosphor.App;

/// <summary>
/// Один конвейер: сцена → сетка → (оверлей) → текстура → цепочка фильтров.
/// Во время перехода у слота живут два слоя — уходящий и входящий.
/// </summary>
internal sealed class SceneLayer : IDisposable
{
    private readonly Shared _s;
    private readonly int _index;
    private readonly Font _font;
    private readonly CharGrid _grid;
    private readonly RenderTarget _rt;
    private FilterChain _chain = null!;
    private readonly Dictionary<string, long> _dataVersions = new();
    /// <summary>OSD видика поверх кассеты. Тикает всегда, рисуется только при _vcrOn (пресет с vhs).</summary>
    private readonly IOverlay? _vcr;
    private bool _vcrOn;

    public IScene Scene { get; }
    public IOverlay? Overlay { get; }
    public string PresetName { get; private set; } = "";
    public CharGrid Grid => _grid;
    public uint Output { get; private set; }
    /// <summary>Сколько секунд сцена на экране.</summary>
    public float Age { get; private set; }

    public SceneLayer(Shared s, int index, int vpW, int vpH, string sceneId, string preset, int defaultCellHeight,
        string? overlayId, CharGrid? handoffFrom)
    {
        _s = s;
        _index = index;
        Scene = s.Scenes.Create(sceneId);
        int cellH = Scene is IPreferredRows pr && pr.PreferredRows > 0
            ? Math.Clamp(vpH / pr.PreferredRows, 8, 64)
            : defaultCellHeight;
        _font = s.FontFor(cellH);
        int cols = Math.Max(1, vpW / _font.Atlas.CellWidth);
        int rows = Math.Max(1, vpH / _font.Atlas.CellHeight);
        _grid = new CharGrid(cols, rows);
        _rt = new RenderTarget(s.Gl, cols * _font.Atlas.CellWidth, rows * _font.Atlas.CellHeight);

        var rng = new Random(s.Rng.Next());
        Scene.Init(cols, rows, rng, s.Config.SceneOptionsFor(sceneId));
        if (handoffFrom is not null && Scene is IHandoffReceiver hr)
        {
            try { hr.Seed(handoffFrom); s.Log.Info($"[{index}] эстафета → {sceneId}"); }
            catch (Exception e) { s.Log.Error($"[{index}] {sceneId}.Seed: {e.Message}"); }
        }

        if (overlayId is not null && s.Overlays.TryGetValue(overlayId, out var make))
        {
            Overlay = make();
            Overlay.Init(cols, rows, new Random(s.Rng.Next()));
        }

        // OSD видика не берёт rng, чтобы не сдвигать последовательность для остальных слоёв
        if (s.Config.Overlay.Vcr && s.Overlays.TryGetValue("vcr", out var makeVcr))
        {
            _vcr = makeVcr();
            _vcr.Init(cols, rows, new Random(index));
        }

        SetPreset(preset);
        s.Log.Info($"[{index}] сцена {sceneId} {cols}x{rows} (ячейка {_font.Atlas.CellWidth}x{_font.Atlas.CellHeight}){(Overlay is null ? "" : $", оверлей {Overlay.Id}")}");
    }

    public void SetPreset(string name)
    {
        _chain?.Dispose();
        // Имя пресета из конфига; "+vhs,crt" или "crt" — цепочка из id фильтров на лету; "none" — без фильтров.
        JsonElement stages = default;
        var adHoc = name.StartsWith('+') ? name[1..] : name;
        var ids = adHoc.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (_s.Config.Filters.Presets.TryGetValue(name, out var st)) stages = st;
        else if (name == "none") { }
        else if (ids.Length > 0 && ids.All(id => _s.Filters.Has(id) || _s.GridPasses.ContainsKey(id)))
            stages = JsonDocument.Parse("[" + string.Join(",", ids.Select(id => $"{{\"id\":\"{id}\"}}")) + "]").RootElement;
        else
            _s.Log.Warn($"[{_index}] пресет '{name}' не найден и не является списком фильтров. Пресеты: {string.Join(", ", _s.Config.Filters.Presets.Keys)}; фильтры: {string.Join(", ", _s.Filters.Ids.Concat(_s.GridPasses.Keys))}. Рисую без фильтров.");

        // Шаги сетки (mono и т.п.) отделяются от GPU-фильтров по реестру и идут первыми, в порядке пресета.
        var warnings = new List<string>();
        var gridIds = new List<string>();
        _gridPasses.Clear();
        if (stages.ValueKind == JsonValueKind.Array)
        {
            var gpu = new List<string>();
            foreach (var stage in stages.EnumerateArray())
            {
                string? id = stage.ValueKind == JsonValueKind.Object && stage.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                if (id is not null && _s.GridPasses.TryGetValue(id, out var make))
                {
                    var pass = make();
                    pass.Configure(stage, warnings);
                    _gridPasses.Add(pass);
                    gridIds.Add(pass.Id);
                }
                else gpu.Add(stage.GetRawText());
            }
            stages = JsonDocument.Parse("[" + string.Join(",", gpu) + "]").RootElement;
        }
        if (_gridPasses.Count > 0 && _font.Atlas is not IGlyphCoverage)
        {
            warnings.Add("атлас не даёт покрытие глифов (IGlyphCoverage) — шаги сетки пропущены");
            _gridPasses.Clear();
            gridIds.Clear();
        }

        _chain = new FilterChain(_s.Gl, _s.Filters, stages, _rt.Width, _rt.Height);
        PresetName = name;
        // Проверка при каждой смене пресета: OSD видика только для цепочки с vhs
        _vcrOn = _vcr is not null && _s.Config.Overlay.VcrFor(_chain.FilterIds);
        foreach (var w in warnings.Concat(_chain.Warnings)) _s.Log.Warn($"[{_index}] пресет {name}: {w}");
        _s.Log.Info($"[{_index}] пресет {name}: {string.Join(" → ", gridIds.Concat(_chain.FilterIds))}");
    }

    private readonly List<IGridPass> _gridPasses = new();

    private void DeliverData()
    {
        if (Scene is not IDataScene ds) return;
        foreach (var key in ds.DataKeys)
        {
            if (!_s.Data.TryGet(key, out var snap, out var ver)) continue;
            if (_dataVersions.TryGetValue(key, out var seen) && seen == ver) continue;
            _dataVersions[key] = ver;
            try { ds.OnData(key, snap); }
            catch (Exception e) { _s.Log.Error($"[{_index}] {Scene.Id}.OnData({key}): {e.Message}"); }
        }
    }

    public void Tick(float dt, float time, long frame, in AudioFrame audio, in Organism organism, int monitorCount)
    {
        Age += dt;
        DeliverData();
        if (Scene is IAudioReactive ar) ar.OnAudio(audio);
        Scene.Update(dt);
        Scene.Draw(_grid);
        _vcr?.Update(dt);
        if (Overlay is not null || _vcrOn)
        {
            Overlay?.Update(dt);
            _s.Data.TryGet("clock", out var clk, out _);
            var ctx = new OverlayContext(Scene.Id, PresetName, _index, monitorCount, _s.Fps, frame,
                (clk as ClockSnapshot)?.Local, audio, organism);
            Overlay?.Apply(_grid, ctx);
            // После основного оверлея (HUD) — OSD видика
            if (_vcrOn) _vcr!.Apply(_grid, ctx);
        }
        // После оверлея: HUD тоже становится монохромным, как и всё на этом «экране»
        if (_gridPasses.Count > 0 && _font.Atlas is IGlyphCoverage cov)
            foreach (var pass in _gridPasses) pass.Apply(_grid, cov);
        _font.Renderer.Render(_grid, _rt);
        var fi = new FrameInfo(time, dt, frame, _rt.Width, _rt.Height, _index, audio, organism);
        Output = _chain.Process(_rt.Texture, fi);
    }

    public void Dispose()
    {
        _chain?.Dispose();
        _rt.Dispose();
    }
}
