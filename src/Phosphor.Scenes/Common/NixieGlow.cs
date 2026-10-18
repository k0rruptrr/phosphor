namespace Phosphor.Scenes;

/// <summary>Свечение газоразрядных ламп (оптика сцен nixie и divergence): плотное ядро и широкий тёплый ореол.</summary>
internal static class NixieGlow
{
    public const string Stages = """[ { "id": "glow", "strength": 1.2, "radius": 22, "core": 0.8, "threshold": 0.15, "saturation": 1.3 } ]""";
}
