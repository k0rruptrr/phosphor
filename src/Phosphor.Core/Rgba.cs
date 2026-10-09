namespace Phosphor.Core;

/// <summary>Цвет 8 бит на канал. Неизменяемый, 4 байта, совместим с RGBA8 на GPU.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static readonly Rgba Black = new(0, 0, 0);
    public static readonly Rgba White = new(255, 255, 255);
    public static readonly Rgba Transparent = new(0, 0, 0, 0);

    /// <summary>Из 0xRRGGBB.</summary>
    public static Rgba Hex(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>Яркость RGB умножается на k (с насыщением). Альфа не меняется.</summary>
    public Rgba Scale(float k) => new(Sat(R * k), Sat(G * k), Sat(B * k), A);

    /// <summary>Линейная интерполяция a→b, t в 0..1.</summary>
    public static Rgba Lerp(Rgba a, Rgba b, float t) => new(
        Sat(a.R + (b.R - a.R) * t), Sat(a.G + (b.G - a.G) * t),
        Sat(a.B + (b.B - a.B) * t), Sat(a.A + (b.A - a.A) * t));

    /// <summary>Из HSV: h в градусах (любое число), s и v в 0..1.</summary>
    public static Rgba Hsv(float h, float s, float v)
    {
        h = ((h % 360f) + 360f) % 360f / 60f;
        int i = (int)h; float f = h - i;
        float p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        var (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return new(Sat(r * 255), Sat(g * 255), Sat(b * 255));
    }

    private static byte Sat(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);
}
