using System.Text.Json;
using Phosphor.Core;
using Silk.NET.OpenGL;

namespace Phosphor.Gfx;

/// <summary>Реестр фильтров: id → фабрика.</summary>
public sealed class FilterRegistry
{
    private readonly Dictionary<string, Func<IFilter>> _f = new(StringComparer.OrdinalIgnoreCase);
    public void Register(string id, Func<IFilter> factory) => _f[id] = factory;
    public bool Has(string id) => _f.ContainsKey(id);
    public IReadOnlyCollection<string> Ids => _f.Keys;
    public IFilter Create(string id) =>
        _f.TryGetValue(id, out var f) ? f() : throw new KeyNotFoundException($"Неизвестный фильтр '{id}'. Есть: {string.Join(", ", _f.Keys)}");
}

/// <summary>Привязка параметра к источнику звука: value = base + audio[src] * amount.</summary>
public readonly record struct Modulation(string Param, string Source, float Amount);

/// <summary>
/// Цепочка фильтров с пинг-понгом между двумя RenderTarget. Порядок — как в конфиге.
/// Отвечает за параметры: база из конфига + модуляция звуком + зажим в [Min, Max].
/// </summary>
public sealed class FilterChain : IDisposable
{
    private sealed class Stage
    {
        public required IFilter Filter;
        public required Dictionary<string, float> Base;
        public required List<Modulation> Mods;
        public readonly FilterParams Resolved = new();
    }

    private readonly GL _gl;
    private readonly List<Stage> _stages = new();
    private RenderTarget? _a, _b;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public List<string> Warnings { get; } = new();
    public IEnumerable<string> FilterIds => _stages.Select(s => s.Filter.Id);

    /// <summary>
    /// stages — JSON-массив вида [{"id":"crt","curvature":0.1,"mod":{"bloom":{"src":"beatPulse","amount":1.5}}}, ...].
    /// Неизвестные фильтры/параметры/источники не роняют программу — попадают в Warnings.
    /// </summary>
    public FilterChain(GL gl, FilterRegistry registry, JsonElement stages, int width, int height)
    {
        _gl = gl;
        Width = width; Height = height;
        if (stages.ValueKind == JsonValueKind.Array)
            foreach (var st in stages.EnumerateArray()) TryAddStage(registry, st);
        if (_stages.Count > 0)
        {
            _a = new RenderTarget(gl, width, height);
            _b = new RenderTarget(gl, width, height);
        }
    }

    private void TryAddStage(FilterRegistry registry, JsonElement st)
    {
        string? id = st.ValueKind == JsonValueKind.Object && st.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (id is null) { Warnings.Add("Фильтр без \"id\" пропущен"); return; }
        if (!registry.Has(id)) { Warnings.Add($"Фильтр '{id}' не существует, пропущен"); return; }

        var filter = registry.Create(id);
        var specs = filter.Params.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var bas = filter.Params.ToDictionary(s => s.Name, s => s.Default, StringComparer.Ordinal);
        var mods = new List<Modulation>();

        foreach (var prop in st.EnumerateObject())
        {
            if (prop.Name is "id") continue;
            if (prop.Name is "mod")
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var m in prop.Value.EnumerateObject())
                {
                    if (!specs.ContainsKey(m.Name)) { Warnings.Add($"{id}: модуляция неизвестного параметра '{m.Name}'"); continue; }
                    string src = m.Value.TryGetProperty("src", out var s) ? s.GetString() ?? "" : "";
                    float amt = m.Value.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetSingle() : 1f;
                    if (!FrameInfo.IsSource(src)) { Warnings.Add($"{id}.{m.Name}: неизвестный источник '{src}'"); continue; }
                    mods.Add(new Modulation(m.Name, src, amt));
                }
                continue;
            }
            if (!specs.TryGetValue(prop.Name, out var spec)) { Warnings.Add($"{id}: неизвестный параметр '{prop.Name}'"); continue; }
            float? v = prop.Value.ValueKind switch
            {
                JsonValueKind.Number => prop.Value.GetSingle(),
                JsonValueKind.True => 1f,
                JsonValueKind.False => 0f,
                JsonValueKind.String when spec.Options is not null =>
                    Array.FindIndex(spec.Options, o => o.Equals(prop.Value.GetString(), StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : null,
                _ => null,
            };
            if (v is null) { Warnings.Add($"{id}.{prop.Name}: недопустимое значение {prop.Value}"); continue; }
            bas[prop.Name] = Math.Clamp(v.Value, spec.Min, spec.Max);
        }

        filter.Init(_gl, Width, Height);
        _stages.Add(new Stage { Filter = filter, Base = bas, Mods = mods });
    }

    public void Resize(int width, int height)
    {
        if (width == Width && height == Height) return;
        Width = width; Height = height;
        _a?.Resize(width, height);
        _b?.Resize(width, height);
        foreach (var s in _stages) s.Filter.Resize(width, height);
    }

    /// <summary>Прогнать цепочку. Возвращает текстуру с результатом (вход, если цепочка пуста). Живёт до следующего Process.</summary>
    public uint Process(uint inputTex, in FrameInfo frame)
    {
        if (_stages.Count == 0) return inputTex;
        uint src = inputTex;
        var dst = _a!;
        foreach (var st in _stages)
        {
            Resolve(st, frame);
            st.Filter.Apply(src, dst, frame, st.Resolved);
            src = dst.Texture;
            dst = ReferenceEquals(dst, _a) ? _b! : _a!;
        }
        return src;
    }

    private static void Resolve(Stage st, in FrameInfo frame)
    {
        foreach (var spec in st.Filter.Params)
        {
            float v = st.Base[spec.Name];
            foreach (var m in st.Mods)
                if (m.Param == spec.Name) v += frame.Get(m.Source) * m.Amount;
            st.Resolved[spec.Name] = Math.Clamp(v, spec.Min, spec.Max);
        }
    }

    /// <summary>Текущие значения параметров (для отладки/тестов).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, float>> Snapshot() =>
        _stages.ToDictionary(s => s.Filter.Id, s => (IReadOnlyDictionary<string, float>)s.Resolved.All.ToDictionary(k => k.Key, k => k.Value));

    public void Dispose()
    {
        foreach (var s in _stages) s.Filter.Dispose();
        _stages.Clear();
        _a?.Dispose(); _b?.Dispose();
    }
}
