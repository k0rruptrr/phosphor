using System.Globalization;
using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>Стойка Hi-Fi: анализатор спектра на деке (spectrum).</summary>
public sealed class SpectrumScene : HifiScene { public SpectrumScene() : base(0) { } public override string Id => "spectrum"; }

/// <summary>Стойка Hi-Fi: стрелочные VU-метры (vumeter).</summary>
public sealed class VuMeterScene : HifiScene { public VuMeterScene() : base(1) { } public override string Id => "vumeter"; }

/// <summary>Стойка Hi-Fi: плеер в духе Winamp (mediaplayer).</summary>
public sealed class MediaPlayerScene : HifiScene { public MediaPlayerScene() : base(2) { } public override string Id => "mediaplayer"; }

/// <summary>
/// Стойка Hi-Fi 80–90-х, три отдельные сцены (раньше — одна «hifi» с лицами по кругу, и дирижёр уходил
/// раньше, чем доходила очередь второго лица): анализатор спектра, стрелочные VU-метры, плеер.
/// Без звука: анализатор едва шумит, стрелки на −20, плеер «STOPPED»; через 10 с тишины — слабое демо.
/// </summary>
public abstract class HifiScene : IScene, IAudioReactive, ISceneParams
{
    private const int Bands = AudioFrame.BandCount;
    private const int EqSliders = 10;
    private const int MiniCols = 16;
    private const float DemoAfterSec = 10f;
    private const float TransitionSec = 0.6f;
    private const float VuTauSec = 0.3f;          // инерция стрелки (баллистика VU ≈ 300 мс)
    private const float VuMinDb = -20f;
    private const float VuMaxDb = 3f;
    private const float VuMaxAngle = 0.87f;       // ±50° от вертикали
    private const float PeakFallPerSec = 0.5f;    // падение пика, единиц 0..1 в секунду
    private const float BeatLampSec = 0.3f;
    private const float MarqueeCharsPerSec = 6f;
    private const float LowHz = 30f;
    private const float HighHz = 16000f;
    private const float MaxSilenceSec = 1e6f;

    private static readonly Rgba Panel = new(12, 12, 14);
    private static readonly Rgba WindowFill = new(34, 34, 40);
    private static readonly Rgba Silver = new(150, 146, 136);
    private static readonly Rgba Cream = new(232, 222, 190);
    private static readonly Rgba Ink = new(40, 30, 22);
    private static readonly Rgba Green = new(70, 235, 100);
    private static readonly Rgba Yellow = new(250, 215, 40);
    private static readonly Rgba Red = new(255, 60, 40);
    private static readonly Rgba Unlit = new(26, 30, 26);
    private static readonly Rgba Dim = new(70, 72, 70);
    private static readonly Rgba PeakWhite = new(255, 250, 235);
    private static readonly Rgba Lcd = new(10, 20, 12);
    private static readonly Rgba LcdText = new(130, 255, 150);
    private static readonly Rgba TitleBar = new(30, 50, 120);

    private static readonly string[] FaceTitles =
    {
        " HF-9000 · SPECTRUM ANALYZER ",
        " HF-9000 · VU METERS ",
        " HF-9000 · MEDIA PLAYER ",
    };
    private static readonly int[] FreqHz = { 31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    private static readonly string[] FreqLabel = { "31", "63", "125", "250", "500", "1K", "2K", "4K", "8K", "16K" };
    private static readonly int[] DbScale = { 0, -6, -12, -18, -24, -30, -36, -42, -48, -54, -60 };
    private static readonly string[] DbLabel = { "0", "-6", "-12", "-18", "-24", "-30", "-36", "-42", "-48", "-54", "-60" };
    private static readonly float[] VuScale = { -20f, -15f, -10f, -7f, -5f, -3f, -1f, 0f, 1f, 2f, 3f };
    private static readonly float[] VuLabelDb = { -20f, -10f, -5f, 0f, 3f };
    private static readonly string[] VuLabel = { "-20", "-10", "-5", "0", "+3" };

    public abstract string Id { get; }

    // Параметры для окна настроек. Default — то же значение, что читает Init.
    public static readonly SceneParamSpec PeakHold = new("peakHold", SceneParamKind.Float, 1f, 0.2f, 3f, "сек, сколько держится пик над столбиком");
    private static readonly SceneParamSpec[] PeakParams = { PeakHold };

    /// <summary>Пик над столбиками рисует только анализатор; у VU-метров и плеера параметров нет.</summary>
    public IReadOnlyList<SceneParamSpec> Params => _face == 0 ? PeakParams : Array.Empty<SceneParamSpec>();

    /// <summary>Лицо по старому имени из hifi.face (тесты, миграция): spectrum, vu, player.</summary>
    public static HifiScene ForFace(string face) => face.ToLowerInvariant() switch
    {
        "vu" => new VuMeterScene(),
        "player" => new MediaPlayerScene(),
        _ => new SpectrumScene(),
    };

    protected HifiScene(int face) => _face = face;

    // Настройки и время.
    private int _cols = 1, _rows = 1;
    private float _speed = 1f, _peakHoldSec = 1f;
    private float _time, _marq;
    private readonly int _face;
    private float _phase1, _phase2;

    // Звук (копия последнего кадра).
    private bool _live, _beatNow;
    private float _level, _high, _mid, _bpm;
    private readonly float[] _in = new float[Bands];
    private float _silentSec, _playSec, _demoSec, _lampLeft;
    private bool _demo;

    // Отображение.
    private readonly float[] _spec = new float[Bands];
    private readonly float[] _peak = new float[Bands];
    private readonly float[] _peakHoldLeft = new float[Bands];
    private readonly float[] _eq = new float[EqSliders];
    private float _vuL = VuMinDb, _vuR = VuMinDb;
    private long _textKey = long.MinValue;
    private string _text = "STOPPED";
    private string _clock = "00:00";
    private string _status = "STOPPED";
    private CharGrid _gOld = new(1, 1), _gNew = new(1, 1);

    // Стрелочные приборы: маски подсетки секстантов (по клетке — биты 0..5), буферы в Init.
    private float _aspect = 1.8f;                 // высота / ширина клетки (SceneOptions.CellAspect)
    private int[] _inkMask = Array.Empty<int>();
    private int[] _redMask = Array.Empty<int>();

    /// <summary>Положение стрелок VU в дБ (диагностика и тесты).</summary>
    public float VuLeftDb => _vuL;
    public float VuRightDb => _vuR;

    /// <summary>Сейчас демо-режим (тишина дольше 10 с).</summary>
    public bool DemoMode => _demo;

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = Math.Max(1, cols);
        _rows = Math.Max(1, rows);
        _speed = MathF.Max(0f, options.Speed);
        _peakHoldSec = Math.Clamp(options.GetFloat(PeakHold.Name, PeakHold.Default), PeakHold.Min, PeakHold.Max);
        _phase1 = (float)rng.NextDouble() * 10f;
        _phase2 = (float)rng.NextDouble() * 10f;

        _time = 0f;
        _marq = 0f;
        Array.Clear(_in);
        Array.Clear(_spec);
        Array.Clear(_peak);
        Array.Clear(_peakHoldLeft);
        Array.Clear(_eq);
        _vuL = _vuR = VuMinDb;
        _live = false;
        _beatNow = false;
        _level = _high = _mid = _bpm = 0f;
        _silentSec = _playSec = _demoSec = _lampLeft = 0f;
        _demo = false;
        _textKey = long.MinValue;
        _text = "STOPPED";
        _clock = "00:00";
        _status = "STOPPED";
        _aspect = Math.Clamp(options.CellAspect, 0.5f, 4f);
        _inkMask = new int[_cols * _rows];
        _redMask = new int[_cols * _rows];
    }

    public void OnAudio(in AudioFrame audio)
    {
        _live = audio.Active;
        _level = audio.Level;
        _mid = audio.Mid;
        _high = audio.High;
        _bpm = audio.Bpm;
        _beatNow = audio.Beat;
        var src = audio.Bands;
        for (int b = 0; b < Bands; b++)
            _in[b] = audio.Active && src is not null ? Math.Clamp(src[b], 0f, 1f) : 0f;
    }

    public void Update(float dt)
    {
        float d = Math.Clamp(dt, 0f, 0.1f) * _speed;
        if (d <= 0f) return; // dt = 0 — состояние не меняется

        _time += d;
        _playSec = _live ? _playSec + d : 0f;
        _silentSec = _live ? 0f : Math.Min(_silentSec + d, MaxSilenceSec);
        _demo = !_live && _silentSec >= DemoAfterSec;
        _demoSec = _demo ? _demoSec + d : 0f;
        _marq += d * MarqueeCharsPerSec;

        UpdateBands(d);
        UpdateEq(d);
        UpdateVu(d);

        if (_beatNow && _live) _lampLeft = BeatLampSec;
        _beatNow = false;
        _lampLeft = MathF.Max(0f, _lampLeft - d);

        RebuildText();
    }

    public void Draw(CharGrid grid) => DrawFace(_face, grid);

    // ---------- состояние ----------

    private void UpdateBands(float d)
    {
        for (int b = 0; b < Bands; b++)
        {
            float u = b / (float)(Bands - 1);
            float v;
            if (_live) v = _in[b];
            else if (_demo) v = 0.12f + 0.18f * (0.5f + 0.5f * MathF.Sin(_time * 1.7f + b * 0.45f + _phase1)) * (1f - 0.5f * u);
            else v = 0.02f + 0.03f * (0.5f + 0.5f * MathF.Sin(_time * 2.3f + b * 1.1f + _phase2)) * (1f - u);
            _spec[b] = v;

            if (v >= _peak[b])
            {
                _peak[b] = v;
                _peakHoldLeft[b] = _peakHoldSec;
            }
            else
            {
                _peakHoldLeft[b] -= d;
                if (_peakHoldLeft[b] <= 0f) _peak[b] = MathF.Max(v, _peak[b] - PeakFallPerSec * d);
            }
        }
    }

    private void UpdateEq(float d)
    {
        float k = MathF.Min(1f, d * 12f);
        for (int g = 0; g < EqSliders; g++)
        {
            int s = g * Bands / EqSliders, e = (g + 1) * Bands / EqSliders;
            float sum = 0f;
            for (int b = s; b < e; b++) sum += _spec[b];
            _eq[g] += (sum / Math.Max(1, e - s) - _eq[g]) * k;
        }
    }

    private void UpdateVu(float d)
    {
        float tl, tr;
        if (_live)
        {
            float lv = Math.Clamp(_level, 0f, 1f);
            float rv = Math.Clamp(_level + 0.08f * (_high - _mid), 0f, 1f); // R — с разбросом от High/Mid
            tl = VuMinDb + (VuMaxDb - VuMinDb) * lv;
            tr = VuMinDb + (VuMaxDb - VuMinDb) * rv;
        }
        else if (_demo)
        {
            float lv = 0.35f + 0.25f * MathF.Sin(_time * 2.1f + _phase1);
            float rv = 0.35f + 0.25f * MathF.Sin(_time * 2.6f + _phase2);
            tl = VuMinDb + (VuMaxDb - VuMinDb) * lv;
            tr = VuMinDb + (VuMaxDb - VuMinDb) * rv;
        }
        else
        {
            tl = tr = VuMinDb;
        }
        float k = 1f - MathF.Exp(-d / VuTauSec);
        _vuL += (tl - _vuL) * k;
        _vuR += (tr - _vuR) * k;
    }

    private void RebuildText()
    {
        int mode = _demo ? 2 : _live ? 1 : 0;
        int secs = (int)(_demo ? _demoSec : _playSec);
        int bpm = _live ? (int)MathF.Round(_bpm) : 0;
        long key = ((long)mode << 56) ^ ((long)bpm << 28) ^ (uint)secs;
        if (key == _textKey) return;
        _textKey = key;
        _clock = $"{secs / 60:D2}:{secs % 60:D2}";
        if (mode == 0)
        {
            _text = "STOPPED";
            _status = "STOPPED";
        }
        else if (mode == 2)
        {
            _text = $"DEMO - PHOSPHOR - {_clock}   ";
            _status = "DEMO";
        }
        else
        {
            string bpmText = bpm > 0 ? bpm.ToString(CultureInfo.InvariantCulture) : "--";
            _text = $"PHOSPHOR - {bpmText} BPM - {_clock}   ";
            _status = "PLAYING";
        }
    }

    // ---------- отрисовка ----------

    private void DrawFace(int face, CharGrid g)
    {
        g.Clear(Panel);
        switch (face)
        {
            case 0: DrawSpectrum(g); break;
            case 1: DrawVu(g); break;
            default: DrawPlayer(g); break;
        }
    }

    private static void Frame(CharGrid g, int face)
    {
        int w = g.Cols, h = g.Rows;
        Box(g, 0, 0, w - 1, h - 1, Silver, Panel);
        g.Text(2, 0, FaceTitles[face], Cream, Panel);
    }

    /// <summary>Прямоугольник одинарной линией с заливкой.</summary>
    private static void Box(CharGrid g, int x0, int y0, int x1, int y1, Rgba line, Rgba fill)
    {
        if (x1 <= x0 || y1 <= y0) return;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                bool top = y == y0, bot = y == y1, lft = x == x0, rgt = x == x1;
                char ch = (top && lft) ? '┌' : (top && rgt) ? '┐' : (bot && lft) ? '└' : (bot && rgt) ? '┘'
                        : (top || bot) ? '─' : (lft || rgt) ? '│' : ' ';
                g.Put(x, y, ch, line, fill);
            }
        }
    }

    private static Rgba Seg(float frac) => frac <= 0.6f ? Green : frac <= 0.85f ? Yellow : Red;

    /// <summary>Индекс полосы, содержащей частоту (те же геометрические границы, что в BeatAnalyzer).</summary>
    private static int BandOf(float hz)
    {
        double ratio = Math.Pow(HighHz / LowHz, 1.0 / Bands);
        int idx = (int)Math.Floor(Math.Log(hz / LowHz) / Math.Log(ratio));
        return Math.Clamp(idx, 0, Bands - 1);
    }

    private void DrawSpectrum(CharGrid g)
    {
        int w = g.Cols, h = g.Rows;
        Frame(g, 0);
        for (int x = 1; x < w - 1; x++) g.Put(x, 2, '─', Dim, Panel);

        int x0 = 6, x1 = w - 2;                 // столбики растянуты до правой рамки
        int span = x1 - x0 + 1;
        int segTop = 3, segBot = h - 5;
        int segRows = segBot - segTop + 1;
        if (span < Bands || segRows < 2) return;

        // Шкала дБ слева.
        for (int i = 0; i < DbScale.Length; i++)
        {
            float n = (DbScale[i] + 60f) / 60f;
            int y = segBot - (int)MathF.Round(n * (segRows - 1));
            g.Text(4 - DbLabel[i].Length, y, DbLabel[i], Dim, Panel);
        }

        for (int b = 0; b < Bands; b++)
        {
            int c0 = x0 + b * span / Bands;
            int c1 = Math.Max(c0, x0 + (b + 1) * span / Bands - 2); // зазор между столбиками — 1 клетка
            float v = Math.Clamp(_spec[b], 0f, 1f);
            int lit = (int)MathF.Round(v * segRows);
            int peakRow = (int)MathF.Round(Math.Clamp(_peak[b], 0f, 1f) * segRows) - 1;
            for (int r = 0; r < segRows; r++)
            {
                int y = segBot - r;
                // Сегмент — строка сетки, нижняя половина клетки; верхняя — тёмная щель между сегментами.
                // Цвет — по уровню дБ верхней границы сегмента, а не по высоте столбика.
                float segDb = -60f + 60f * (r + 1f) / segRows;
                Rgba on = segDb < -12f ? Green : segDb < -3f ? Yellow : Red;
                Rgba fg = r == peakRow ? PeakWhite : r < lit ? on : Unlit;
                for (int x = c0; x <= c1; x++) g.Put(x, y, '▄', fg, Panel);
            }
        }

        // Частоты под столбиками; без наложения.
        int fy = h - 3, lastEnd = -1;
        for (int i = 0; i < FreqHz.Length; i++)
        {
            int cx = x0 + (BandOf(FreqHz[i]) * span + span / 2) / Bands;
            int lx = cx - FreqLabel[i].Length / 2;
            if (lx <= lastEnd) continue;
            g.Text(lx, fy, FreqLabel[i], Cream, Panel);
            lastEnd = lx + FreqLabel[i].Length;
        }
    }

    private void DrawVu(CharGrid g)
    {
        int w = g.Cols, h = g.Rows;
        Frame(g, 1);
        int top = 3, bot = h - 8;
        if (bot - top < 6 || w < 24) return;

        int iw = w - 2;
        int bw2 = (iw - 1) / 2;
        int lx0 = 1, lx1 = lx0 + bw2 - 1;
        int rx0 = lx1 + 2, rx1 = rx0 + bw2 - 1;
        DrawMeter(g, lx0, top, lx1, bot, _vuL);
        DrawMeter(g, rx0, top, rx1, bot, _vuR);

        int labelY = bot + 1;
        g.Text((lx0 + lx1) / 2 - 2, labelY, "LEFT", Cream, Panel);
        g.Text((rx0 + rx1) / 2 - 2, labelY, "RIGHT", Cream, Panel);

        // Лампа PEAK: вспыхивает на ударах.
        int py = bot + 3, cx = w / 2;
        bool lamp = _lampLeft > 0f;
        g.Put(cx - 4, py, '●', lamp ? Red : Unlit, Panel);
        g.Text(cx - 2, py, "PEAK", lamp ? Red : Dim, Panel);
    }

    /// <summary>Стрелочный прибор: кремовая шкала, дуга делений, стрелка на опоре внизу.</summary>
    private void DrawMeter(CharGrid g, int x0, int y0, int x1, int y1, float vuDb)
    {
        Box(g, x0, y0, x1, y1, Silver, Cream);
        int cx = (x0 + x1) / 2;
        int py = y1 - 2;

        // Подсетка секстантов: 2 столбца × 3 строки на клетку. Единица — подстолбец (½ ширины клетки).
        // Подстрока по высоте = (высота клетки)/3 = (2·aspect/3) подстолбца, поэтому дуга — окружность в пикселях.
        float ky = 3f / (2f * _aspect);                         // подстрок на единицу радиуса (по вертикали)
        float radius = MathF.Min((py - y0 - 1) * 2f * _aspect, (x1 - x0) - 2f);
        if (radius < 6f) return;
        int sxp = 2 * cx + 1, syp = 3 * py + 2;                 // опора — низ клетки опоры
        Array.Clear(_inkMask);
        Array.Clear(_redMask);

        // Дуга шкалы −20…+3 VU: тонкая линия, красная зона — от 0 VU.
        float ta = VuAngle(VuMinDb);
        Pol(ta, radius, sxp, syp, ky, out int px0, out int py0);
        for (float vu = VuMinDb + 0.1f; vu <= VuMaxDb + 0.001f; vu += 0.1f)
        {
            Pol(VuAngle(vu), radius, sxp, syp, ky, out int px1, out int py1);
            Line(vu >= 0f ? _redMask : _inkMask, px0, py0, px1, py1);
            px0 = px1;
            py0 = py1;
        }

        // Риски шкалы: короткие радиальные отрезки внутри дуги.
        for (int i = 0; i < VuScale.Length; i++)
        {
            float th = VuAngle(VuScale[i]);
            Pol(th, radius - 10f, sxp, syp, ky, out int ax, out int ay);
            Pol(th, radius - 2f, sxp, syp, ky, out int bx, out int by);
            Line(VuScale[i] >= 0f ? _redMask : _inkMask, ax, ay, bx, by);
        }

        // Стрелка: непрерывная линия от опоры до шкалы, толщина 2 подстолбца.
        float a = VuAngle(vuDb);
        Pol(a, radius - 14f, sxp, syp, ky, out int nx, out int ny);
        Line(_inkMask, sxp, syp, nx, ny);
        Line(_inkMask, sxp + 1, syp, nx + 1, ny);

        // Вывод клеток: чернила приоритетнее красной зоны.
        for (int y = y0 + 1; y < y1; y++)
        {
            for (int x = x0 + 1; x < x1; x++)
            {
                int i = y * _cols + x;
                if (_inkMask[i] != 0) g.Put(x, y, GlyphSet.Sextant(_inkMask[i]), Ink, Cream);
                else if (_redMask[i] != 0) g.Put(x, y, GlyphSet.Sextant(_redMask[i]), Red, Cream);
            }
        }

        // Подписи шкалы текстом поверх дуги.
        for (int i = 0; i < VuLabel.Length; i++)
        {
            float th = VuAngle(VuLabelDb[i]);
            Pol(th, radius - 22f, sxp, syp, ky, out int lx, out int ly);
            int cxText = (int)MathF.Round(lx / 2f) - VuLabel[i].Length / 2;
            int cyText = (int)MathF.Round(ly / 3f);
            g.Text(cxText, cyText, VuLabel[i], VuLabelDb[i] >= 0f ? Red : Ink, Cream);
        }

        g.Text(x0 + 1, y1 - 1, "VU", Ink, Cream);
        g.Put(cx, py, '●', Ink, Cream); // ось стрелки — кружок
    }

    /// <summary>Точка подсетки на радиусе r (единицы подстолбца) под углом th от вертикали; опора (sxp, syp).</summary>
    private static void Pol(float th, float r, int sxp, int syp, float ky, out int sx, out int sy)
    {
        sx = (int)MathF.Round(sxp + MathF.Sin(th) * r);
        sy = (int)MathF.Round(syp - MathF.Cos(th) * r * ky);
    }

    /// <summary>Зажечь подпиксель подсетки (sx — подстолбец, sy — подстрока), в маску ячейки.</summary>
    private void Plot(int[] m, int sx, int sy)
    {
        if (sx < 0 || sy < 0) return;
        int cx = sx >> 1, cy = sy / 3;
        if (cx >= _cols || cy >= _rows) return;
        // Бит секстанта: 0 — верх-лево, 1 — верх-право, 2 — середина-лево, 3 — середина-право, 4 — низ-лево, 5 — низ-право.
        m[cy * _cols + cx] |= 1 << ((sx & 1) | ((sy % 3) << 1));
    }

    /// <summary>Линия Брезенхема по подсетке.</summary>
    private void Line(int[] m, int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x1 - x0), stepX = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), stepY = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            Plot(m, x0, y0);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += stepX; }
            if (e2 <= dx) { err += dx; y0 += stepY; }
        }
    }

    /// <summary>Угол стрелки от вертикали (рад): −VuMaxAngle на −20 VU, +VuMaxAngle на +3 VU.</summary>
    private static float VuAngle(float vuDb)
    {
        float f = Math.Clamp((vuDb - VuMinDb) / (VuMaxDb - VuMinDb), 0f, 1f);
        return -VuMaxAngle + 2f * VuMaxAngle * f;
    }

    private void DrawPlayer(CharGrid g)
    {
        int w = g.Cols, h = g.Rows;
        Frame(g, 2);
        if (w < 24 || h < 14) return;

        int pw = Math.Clamp(w - 6, 24, 66);
        int ph = Math.Clamp(h - 2, 20, 26);
        int px = (w - pw) / 2, py = Math.Max(1, (h - ph) / 2);
        int ix0 = px + 1, ix1 = px + pw - 2;             // внутренняя ширина окна
        Box(g, px, py, px + pw - 1, py + ph - 1, Silver, WindowFill);

        // Строка заголовка окна.
        for (int x = ix0; x <= ix1; x++) g.Put(x, py + 1, ' ', PeakWhite, TitleBar);
        g.Text(px + 2, py + 1, "PHOSPHOR PLAYER", PeakWhite, TitleBar);

        // LCD: бегущая строка и время/статус.
        int lx0 = px + 2, lx1 = px + pw - 3, ly0 = py + 2, ly1 = py + 5;
        for (int y = ly0; y <= ly1; y++)
            for (int x = lx0; x <= lx1; x++)
                g.Put(x, y, ' ', LcdText, Lcd);
        int innerW = lx1 - lx0 - 1;
        int offset = _live || _demo ? (int)_marq : 0;
        for (int i = 0; i < innerW && _text.Length > 0; i++)
            g.Put(lx0 + 1 + i, ly0 + 1, _text[(offset + i) % _text.Length], LcdText, Lcd);
        g.Text(lx0 + 1, ly0 + 2, _clock, LcdText, Lcd);
        g.Text(lx0 + 9, ly0 + 2, _status, LcdText, Lcd);

        // Мини-спектр: 16 столбиков.
        int miniTop = py + 7;
        int miniBot = ph >= 24 ? py + ph - 14 : py + 9;
        int mh = miniBot - miniTop + 1;
        int iw = lx1 - lx0 - 1;
        int colW = Math.Max(1, iw / MiniCols);
        int mbw = colW >= 2 ? colW - 1 : 1;
        for (int c = 0; c < MiniCols; c++)
        {
            float v = (Math.Clamp(_spec[2 * c], 0f, 1f) + Math.Clamp(_spec[2 * c + 1], 0f, 1f)) * 0.5f;
            int lit = (int)MathF.Round(v * mh);
            for (int r = 0; r < mh; r++)
            {
                Rgba fg = r < lit ? Seg((r + 1f) / mh) : Unlit;
                for (int k = 0; k < mbw; k++)
                    g.Put(lx0 + 1 + c * colW + k, miniBot - r, '█', fg, WindowFill);
            }
        }

        // Эквалайзер: 10 ползунков, «дышат» под полосы.
        int eqLabelY = miniBot + 2, eqTop = miniBot + 3, eqBot = py + ph - 5;
        g.Text(lx0 + 1, eqLabelY, "EQ", Cream, WindowFill);
        if (eqBot > eqTop)
        {
            int span = Math.Max(1, (lx1 - lx0 - 2) / EqSliders);
            for (int i = 0; i < EqSliders; i++)
            {
                int ex = lx0 + 2 + i * span;
                for (int y = eqTop; y <= eqBot; y++) g.Put(ex, y, '│', Dim, WindowFill);
                int knob = eqTop + (int)MathF.Round((1f - Math.Clamp(_eq[i], 0f, 1f)) * (eqBot - eqTop));
                g.Put(ex - 1, knob, '■', LcdText, WindowFill);
                g.Put(ex, knob, '■', LcdText, WindowFill);
            }
        }

        // Кнопки транспорта: ► светится при воспроизведении, II — при стопе.
        int by = py + ph - 3;
        bool playing = _live || _demo;
        DrawButton(g, px + 2, by, "◄◄", false);
        DrawButton(g, px + 9, by, "►", playing);
        DrawButton(g, px + 16, by, "II", !playing);
        DrawButton(g, px + 23, by, "►►", false);
    }

    private static void DrawButton(CharGrid g, int x, int y, string label, bool lit)
    {
        Rgba fg = lit ? LcdText : Cream;
        Rgba bg = lit ? Lcd : WindowFill;
        g.Put(x, y, '[', Silver, WindowFill);
        g.Text(x + 1, y, label, fg, bg);
        g.Put(x + 1 + label.Length, y, ']', Silver, WindowFill);
    }
}
