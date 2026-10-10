using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// Rotozoom: текстура 64×64 (шахматка C64, кольца, диагональные полосы, надпись PHOSPHOR) вращается и масштабируется
/// с повтором (wrap). Угол — накопленная фаза (непрерывное вращение) плюс лёгкое покачивание, масштаб пульсирует 0.5..3,
/// центр ходит по фигуре Лиссажу.
/// Звук: удар — «панч» масштаба ×0.8 (затухает за 150 мс), бас — добавка к скорости вращения,
/// каждый 4-й удар — смена направления вращения.
/// </summary>
public sealed class RotozoomScene : IScene, IAudioReactive, ISceneParams
{
    // Параметры для окна настроек.
    public static readonly SceneParamSpec BeatsSpec = new("beatsPerReverse", SceneParamKind.Int, 4f, 1f, 16f, "ударов до разворота");
    public static readonly SceneParamSpec PunchSpec = new("punchScale", SceneParamKind.Float, 0.8f, 0.3f, 1f, "сила удара: сжатие масштаба");
    private static readonly SceneParamSpec[] ParamList = { BeatsSpec, PunchSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    private int _beatsPerReverse;
    private float _punchScale;

    private const int TexSize = 64;
    private const int TexMask = TexSize - 1;
    private const float TickDt = 1f / 50f;           // логика на 50 Гц
    private const float PunchTime = 0.15f;           // затухание панча, с

    private static readonly float PunchMul = MathF.Exp(-TickDt / PunchTime);

    public string Id => "rotozoom";

    private RetroCanvas _canvas = null!;
    private RetroClock _clock = new(50f);
    private float _speed = 1f;
    private Rgba[] _tex = Array.Empty<Rgba>();

    private float _phase;       // накопленный угол вращения, рад
    private float _zphase;      // фаза пульсации масштаба и покачивания
    private float _punch;       // 1 в момент удара, затухает
    private int _dir = 1;       // направление вращения
    private int _beats;         // счётчик ударов

    private float _bass, _tBass;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _beatsPerReverse = Math.Clamp(options.GetInt(BeatsSpec.Name, (int)BeatsSpec.Default), (int)BeatsSpec.Min, (int)BeatsSpec.Max);
        _punchScale = Math.Clamp(options.GetFloat(PunchSpec.Name, PunchSpec.Default), PunchSpec.Min, PunchSpec.Max);
        _speed = MathF.Max(0f, options.Speed);
        _canvas = new RetroCanvas(cols, rows, CanvasMode.HalfBlock);
        _clock = new RetroClock(50f);
        _tex = BuildTexture();

        _phase = 0f;
        _zphase = 0f;
        _punch = 0f;
        _dir = 1;
        _beats = 0;
        _bass = _tBass = 0f;
    }

    /// <summary>Шахматка 8×8 из двух цветов C64, кольца, диагональные полосы и надпись; всё квантуется Amiga12.</summary>
    private static Rgba[] BuildTexture()
    {
        var c64 = RetroPalettes.C64;
        var tex = new Rgba[TexSize * TexSize];
        for (int y = 0; y < TexSize; y++)
        for (int x = 0; x < TexSize; x++)
        {
            int checker = ((x >> 3) + (y >> 3)) & 1;
            var c = checker == 0 ? c64[6] : c64[14];
            float dx = x - 32, dy = y - 32;
            if (((int)(MathF.Sqrt(dx * dx + dy * dy) / 4f) & 1) == 1) c = Rgba.Lerp(c, c64[1], 0.3f);
            if ((((x + y) >> 2) & 3) == 0) c = c.Scale(0.7f);
            tex[y * TexSize + x] = RetroPalettes.Amiga12(c);
        }
        DrawWord(tex);
        return tex;
    }

    // Пиксельный шрифт 5×7: по строке на байт, биты 4..0 — колонки слева направо.
    // Слово PHOSPHOR: P H O S P H O R (8 букв, по одной маске на позицию).
    private static readonly byte[][] Word =
    {
        new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000 }, // P
        new byte[] { 0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 }, // H
        new byte[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 }, // O
        new byte[] { 0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110 }, // S
        new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000 }, // P
        new byte[] { 0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 }, // H
        new byte[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 }, // O
        new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001 }, // R
    };

    /// <summary>Надпись по центру текстуры: тень чёрным (+1,+1), буквы белым. Шаг букв 6 текселей, слово 47×7.</summary>
    private static void DrawWord(Rgba[] tex)
    {
        const int x0 = (TexSize - 47) / 2, y0 = (TexSize - 7) / 2;
        var white = RetroPalettes.Amiga12(RetroPalettes.C64[1]);
        var black = RetroPalettes.Amiga12(RetroPalettes.C64[0]);
        for (int pass = 0; pass < 2; pass++)
        {
            int ox = pass == 0 ? 1 : 0;
            var col = pass == 0 ? black : white;
            for (int li = 0; li < Word.Length; li++)
            for (int r = 0; r < 7; r++)
            for (int c = 0; c < 5; c++)
            {
                if (((Word[li][r] >> (4 - c)) & 1) == 0) continue;
                int tx = (x0 + li * 6 + c + ox) & TexMask;
                int ty = (y0 + r + ox) & TexMask;
                tex[ty * TexSize + tx] = col;
            }
        }
    }

    public void OnAudio(in AudioFrame audio)
    {
        _tBass = audio.Active ? audio.Bass : 0f;
        if (!audio.Active || !audio.Beat) return;
        _punch = 1f;
        if (++_beats % _beatsPerReverse == 0) _dir = -_dir;
    }

    public void Update(float dt)
    {
        dt = Math.Clamp(dt, 0f, 0.1f);
        _bass += (_tBass - _bass) * (1f - MathF.Exp(-dt * 12f));

        int ticks = _clock.Advance(dt * _speed);
        for (int i = 0; i < ticks; i++)
        {
            _phase = (_phase + _dir * (0.4f + 0.4f * _bass) * TickDt) % MathF.Tau;
            _zphase += 0.7f * TickDt;
            _punch *= PunchMul;
        }

        float angle = _phase + 0.2f * MathF.Sin(_zphase);   // непрерывное вращение + лёгкое покачивание
        float zoom = (1.75f + 1.25f * MathF.Sin(_zphase)) * (1f - (1f - _punchScale) * _punch);

        float t = _clock.Time;
        int w = _canvas.Width, h = _canvas.Height;
        float cx = w * (0.5f + 0.3f * MathF.Sin(t * 0.31f));
        float cy = h * (0.5f + 0.3f * MathF.Sin(t * 0.47f + 1f));

        float cos = MathF.Cos(angle) / zoom, sin = MathF.Sin(angle) / zoom;
        var px = _canvas.Pixels;
        for (int y = 0; y < h; y++)
        {
            float dy = y + 0.5f - cy;
            for (int x = 0; x < w; x++)
            {
                float dx = x + 0.5f - cx;
                float u = dx * cos - dy * sin + TexSize * 0.5f;
                float v = dx * sin + dy * cos + TexSize * 0.5f;
                int tu = (int)MathF.Floor(u) & TexMask;
                int tv = (int)MathF.Floor(v) & TexMask;
                px[y * w + x] = _tex[tv * TexSize + tu];
            }
        }
    }

    public void Draw(CharGrid grid) => _canvas.Present(grid);
}
