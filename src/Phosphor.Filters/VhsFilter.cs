using Phosphor.Gfx;

namespace Phosphor.Filters;

/// <summary>
/// Эмуляция зажёванной VHS-кассеты: дрожание строк, полоса трекинга, растекание цветности,
/// шум, сдвиг RGB, шум головок внизу кадра, качка ленты, выпадения сигнала. Один проход.
/// </summary>
public sealed class VhsFilter : ShaderFilter
{
    private static readonly IReadOnlyList<ParamSpec> _params = new ParamSpec[]
    {
        new ParamSpec("jitter", 0.25f, 0f, 1f, null, "Дрожание строк: сдвиг по x для групп по 2 строки (0..3 px × jitter)", "дрожание строк"),
        new ParamSpec("tracking", 0.2f, 0f, 1f, null, "Автотрекинг: при >= 0.25 эпизоды полосы помех (1.5–3 с), пауза 25 с (0.25) … 6 с (1)", "трекинг"),
        new ParamSpec("bleed", 0.5f, 0f, 1f, null, "Растекание цветности вправо (~10 px × bleed)", "растекание цвета"),
        new ParamSpec("noise", 0.12f, 0f, 1f, null, "Зерно/снег, меняется каждый кадр", "снег"),
        new ParamSpec("rgbShift", 1.0f, 0f, 8f, null, "Сдвиг красного вправо и синего влево, в пикселях", "сдвиг RGB"),
        new ParamSpec("bottomSkew", 0.5f, 0f, 1f, null, "Шов переключения головок в нижних 2.5% кадра: надломы строк (до 45 px × bottomSkew у низа)", "шов головок"),
        new ParamSpec("wobble", 0.3f, 0f, 1f, null, "Медленная качка кадра по x (до 3 px × wobble)", "качка ленты"),
        new ParamSpec("saturation", 0.85f, 0f, 2f, null, "Насыщенность (VHS блёклый)", "насыщенность"),
        new ParamSpec("dropouts", 0.2f, 0f, 1f, null, "Частота белых горизонтальных выпадений сигнала", "выпадения"),
    };

    public override string Id => "vhs";

    protected override string FragmentResource => "Phosphor.Filters.Shaders.vhs.frag";

    public override IReadOnlyList<ParamSpec> Params => _params;
}
