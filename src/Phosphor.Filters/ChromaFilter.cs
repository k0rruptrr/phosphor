using Phosphor.Gfx;

namespace Phosphor.Filters;

/// <summary>
/// Хроматическая аберрация: каналы R, G, B смещаются относительно друг друга.
/// radial = 1 — радиальное смещение от центра (как у линзы), radial = 0 — равномерный сдвиг в направлении angle.
/// samples = 1 — классика (R в +offset, B в −offset), больше — спектральное размазывание.
/// Один проход.
/// </summary>
public sealed class ChromaFilter : ShaderFilter
{
    private static readonly IReadOnlyList<ParamSpec> _params = new ParamSpec[]
    {
        new ParamSpec("amount", 1f, 0f, 12f, null, "Величина расхождения каналов в пикселях", "расхождение цветов"),
        new ParamSpec("radial", 1f, 0f, 1f, null, "1 = радиально от центра, 0 = равномерный сдвиг в направлении angle", "радиальность"),
        new ParamSpec("angle", 0f, 0f, 360f, null, "Направление равномерного сдвига в градусах (0 = вправо)", "направление"),
        new ParamSpec("samples", 3f, 1f, 8f, null, "Число выборок спектра (1 = классика)", "спектр"),
    };

    public override string Id => "chroma";

    protected override string FragmentResource => "Phosphor.Filters.Shaders.chroma.frag";

    public override IReadOnlyList<ParamSpec> Params => _params;
}


