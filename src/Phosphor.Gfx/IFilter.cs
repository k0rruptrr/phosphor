using Phosphor.Core;
using Silk.NET.OpenGL;

namespace Phosphor.Gfx;

/// <summary>
/// Описание числового параметра фильтра. ВСЕ параметры фильтров — float.
/// Перечисления (например цвет фосфора) задаются через Options: в конфиге строка, в шейдер идёт индекс (0, 1, 2...).
/// Булевы — Min=0, Max=1, в конфиге true/false.
/// Label — короткая подпись для окна настроек (1–3 слова); Description — полное пояснение (в подсказке).
/// </summary>
public sealed record ParamSpec(string Name, float Default, float Min, float Max, string[]? Options = null, string Description = "", string? Label = null);

/// <summary>Значения параметров на текущий кадр: уже с модуляцией от звука и зажатые в [Min, Max].</summary>
public sealed class FilterParams
{
    private readonly Dictionary<string, float> _v = new(StringComparer.Ordinal);
    public float this[string name]
    {
        get => _v.TryGetValue(name, out var x) ? x : throw new KeyNotFoundException($"Параметр '{name}' не объявлен в Params фильтра");
        set => _v[name] = value;
    }
    public IEnumerable<KeyValuePair<string, float>> All => _v;
}

/// <summary>
/// Фильтр пост-обработки: текстура на вход → RenderTarget на выход. Ничего не знает про сцены и звук
/// (звук приходит только через уже промодулированные параметры).
/// </summary>
public interface IFilter : IDisposable
{
    /// <summary>Id в нижнем регистре, как в конфиге ("crt", "phosphor", "vhs", "chroma").</summary>
    string Id { get; }

    /// <summary>Все настраиваемые параметры. Только они могут приходить из конфига и модулироваться.</summary>
    IReadOnlyList<ParamSpec> Params { get; }

    /// <summary>Создать GL-ресурсы. width/height — размер входной и выходной картинки.</summary>
    void Init(GL gl, int width, int height);

    /// <summary>Размер изменился: пересоздать внутренние буферы.</summary>
    void Resize(int width, int height);

    /// <summary>
    /// Обработать кадр. inputTex — текстура (RGBA, Linear, ClampToEdge). Фильтр ОБЯЗАН сам вызвать output.Bind()
    /// и закрасить ВЕСЬ output. Может использовать свои внутренние RenderTarget'ы.
    /// После вызова: blending выключен, активный текстурный юнит — 0.
    /// </summary>
    void Apply(uint inputTex, RenderTarget output, in FrameInfo frame, FilterParams p);
}
