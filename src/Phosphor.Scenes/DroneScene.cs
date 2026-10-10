using System.Runtime.CompilerServices;
using Phosphor.Core;

[assembly: InternalsVisibleTo("Phosphor.Scenes.Tests")]

namespace Phosphor.Scenes;

/// <summary>
/// FPV-фид дрона над вечерним городом: камера в очках пилота, аналоговый сигнал 5.8 ГГц и OSD Betaflight.
/// Город — квартальная сетка 864×864 клетки на торе (1 клетка ≈ 1 м, квартал 24×24: дома внутри, улицы 10 м шириной).
/// Рендер — рейкаст в буфер пикселей W×H (W = cols, H = rows × 2): 2D DDA по клеткам, пустые кварталы пропускаются
/// целиком, земля — аналитическая плоскость z = 0. Маршрут строится заранее из манёвров; каждая точка проверяется
/// по сетке, включая зазор до стен не меньше 4 м. Дрон уходит на 100–400 м от пилота и возвращается.
/// Draw не меняет состояние и не использует rng: срывы сигнала считаются хешем от кадра.
/// </summary>
public sealed class DroneScene : IScene, IAudioReactive, ISceneParams
{
    // Параметры для окна настроек (не больше четырёх).
    public static readonly SceneParamSpec FlySpec = new("flySpeed", SceneParamKind.Float, 1f, 0.3f, 2f, "темп полёта");
    public static readonly SceneParamSpec BreakupSpec = new("breakup", SceneParamKind.Float, 0.6f, 0f, 1f, "сила аналоговых срывов");
    public static readonly SceneParamSpec OsdSpec = new("osd", SceneParamKind.Bool, 1f, 0f, 1f, "OSD Betaflight");
    public static readonly SceneParamSpec TimeSpec = new("time", SceneParamKind.Choice, 0f, 0f, 2f, "освещение",
        new[] { "dusk", "night", "day" });
    private static readonly SceneParamSpec[] ParamList = { FlySpec, BreakupSpec, OsdSpec, TimeSpec };

    public IReadOnlyList<SceneParamSpec> Params => ParamList;

    public string Id => "drone";

    private const int N = 864;                     // сторона мира, клеток (тор; 800+ м, чтобы 400 м не сворачивались)
    private const int Period = 24;                 // квартал: дома внутри [5,19), улица — свободные колонки 19..4
    private const int Blocks = N / Period;         // кварталов по стороне (36)
    private const float Ds = 0.25f;                // шаг точек маршрута, м
    private const int MaxSamples = 12000;
    private const float RouteLen = 420f;           // длина маршрута до перепланировки, м
    private const float V0 = 14f;                  // крейсерская скорость, м/с при flySpeed = 1
    private const float Gravity = 9.8f;
    private const float MaxRay = 120f;             // дальность рейкаста, м
    private const float FogStart = 84f;            // туман — последние 30% дальности
    private const float FogMax = 0.85f;
    private const float MaxWallH = 110f;
    private const float AngMax = 1.0472f;          // 60° до края кадра: FOV 120° по горизонтали, бочка
    private const float MountTilt = 0.44f;         // камера смотрит вверх на ~25°
    private const float Head = 1.7f;               // высота головы пилота
    private const float PilotX = 0.5f, PilotY = 0.5f;
    private const float Half = 0.35f;              // полуразмер дрона для проверки по сетке
    private const int ClearCells = 4;              // окрестность проверки зазора, клеток
    private const int Macro = Period;              // плитка для пропуска пустого пространства в DDA: квартал целиком
    private const float MinWall = 4f;              // минимальный зазор до стены, м
    private const float Radius = 11.5f;            // орбита вокруг высотки: зазор ≥ 4 м до граней и углов
    private const float SignalRange = 320f;        // дальность, на которой сигнал падает почти в ноль
    private static readonly float[] CornerX = { 0f, -Half, Half, -Half, Half };
    private static readonly float[] CornerY = { 0f, -Half, -Half, Half, Half };

    // Типы кварталов (бит маски = 1 << тип).
    private const byte BEmpty = 0, BTower = 1, BPanel = 2, BGap = 3, BHouses = 4;

    // Манёвры.
    private const int KOrbit = 0, KDive = 1, KCorridor = 2, KGap = 3, KCandle = 4, KTransit = 5;
    private static readonly string[] KindNames = { "orbit", "dive", "corridor", "gap", "candle", "transit" };

    // Палитры: тёмные силуэты домов в сумерках, серые в дневном свете.
    private static readonly Rgba[] DuskPalette =
    {
        Rgba.Hex(0x1E1E26), Rgba.Hex(0x26262F), Rgba.Hex(0x2F2B36),
        Rgba.Hex(0x262B38), Rgba.Hex(0x35302C), Rgba.Hex(0x2A3140),
    };
    private static readonly Rgba[] DayPalette =
    {
        Rgba.Hex(0x8C9099), Rgba.Hex(0x7A8088), Rgba.Hex(0x9AA0A8),
        Rgba.Hex(0x6E747C), Rgba.Hex(0xA0A4A8), Rgba.Hex(0x80868E),
    };
    private static readonly Rgba DarkBlade = Rgba.Hex(0x0A0A0C);

    private int _cols, _rows, _w, _h;
    private Random _rng = new(0);
    private float _speed = 1f, _flySpeed = 1f, _breakup = 0.6f, _cellAspect = 1.8f;
    private bool _osd = true;
    private int _timeMode;
    private float _litP;
    private bool _lampOn;
    private Rgba _warm, _glass, _horizon, _zenith, _glow, _asphalt, _yard, _lamp, _fogCol;
    private float _skyTop;
    private Rgba[] _matBase = Array.Empty<Rgba>(), _matLit = Array.Empty<Rgba>();
    private Rgba[] _skyTab = Array.Empty<Rgba>();

    // Город: высота (м), материал и максимум высоты квартала по клеткам.
    private byte[] _height = Array.Empty<byte>();
    private byte[] _mat = Array.Empty<byte>();
    private byte[] _macroMax = Array.Empty<byte>();
    private readonly byte[] _block = new byte[Blocks * Blocks];
    private int _towerA, _towerB;

    // Маршрут: точки и их атрибуты.
    private readonly float[] _sx = new float[MaxSamples], _sy = new float[MaxSamples], _sz = new float[MaxSamples];
    private readonly float[] _syaw = new float[MaxSamples], _spit = new float[MaxSamples];
    private readonly float[] _sbank = new float[MaxSamples], _sext = new float[MaxSamples];
    private readonly int[] _skind = new int[MaxSamples];
    private int _n;
    private float _pos;
    private int _lastKind = -1;
    private int _curKind = KTransit;
    private int _secBx, _secBy;                    // центр текущего района (координаты кварталов, без модуля)
    private bool _farNext = true;

    // Состояние дрона.
    private float _dx, _dy, _dz, _yaw, _pitch, _bank, _ext;
    private float _view, _roll, _vel;
    private bool _ovr;
    private float _time;
    private uint _frame;
    private float _level, _bass, _kick;
    private float _signal = 1f;
    private bool _blocked;
    private float _bad;
    private float _mah, _flightSec, _landLeft, _volt, _current;
    private int _pullUps;

    // Аналоговый сигнал: срывы строк, перекат кадра, чёрный кадр.
    private int[] _tearDx = Array.Empty<int>(), _tearLeft = Array.Empty<int>();
    private float _rollLeft, _rollAmp;
    private int _rollPx;
    private float _blackLeft;

    // Кадр и геометрия луча (локальные направления пикселей считаются один раз в Init).
    private Rgba[] _px = Array.Empty<Rgba>();
    private float[] _lx = Array.Empty<float>(), _ly = Array.Empty<float>(), _lz = Array.Empty<float>();

    // Диагностика для тестов.
    internal int PullUpCount => _pullUps;
    internal string ManeuverName => KindNames[_curKind];
    internal float Roll => _roll;
    internal float Signal => _signal;
    internal bool Blocked => _blocked;
    internal float CameraX => _dx;
    internal float CameraY => _dy;
    internal float CameraZ => _dz;

    /// <summary>Расстояние по тору от пилота до дрона, м.</summary>
    internal float PilotDistance => MathF.Sqrt(WrapDelta(_dx - PilotX) * WrapDelta(_dx - PilotX)
                                              + WrapDelta(_dy - PilotY) * WrapDelta(_dy - PilotY));

    /// <summary>Расстояние до ближайшей стены дома, которая выше точки (до 8 м, иначе 99).</summary>
    internal float WallDistance(float x, float y, float z)
    {
        float best = 99f;
        int cx = (int)MathF.Floor(x), cy = (int)MathF.Floor(y);
        for (int dy = -8; dy <= 8; dy++)
        {
            for (int dx = -8; dx <= 8; dx++)
            {
                int i = CellIdx(cx + dx, cy + dy);
                if (_height[i] <= z) continue;
                float nx = Math.Clamp(x, cx + dx, cx + dx + 1f), ny = Math.Clamp(y, cy + dy, cy + dy + 1f);
                float d = MathF.Sqrt((x - nx) * (x - nx) + (y - ny) * (y - ny));
                if (d < best) best = d;
            }
        }
        return best;
    }

    internal bool LineBlockedFromPilot(float x, float y, float z) =>
        LineBlocked(WrapDelta(x - PilotX), WrapDelta(y - PilotY), z);

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = cols;
        _rows = rows;
        _w = cols;
        _h = rows * 2;
        _rng = rng;
        _speed = MathF.Max(0f, options.Speed);
        _cellAspect = options.CellAspect;
        _flySpeed = Math.Clamp(options.GetFloat(FlySpec.Name, FlySpec.Default), FlySpec.Min, FlySpec.Max);
        _breakup = Math.Clamp(options.GetFloat(BreakupSpec.Name, BreakupSpec.Default), 0f, 1f);
        _osd = options.GetBool(OsdSpec.Name, true);
        string time = options.GetString(TimeSpec.Name, TimeSpec.Options![(int)TimeSpec.Default]);
        _timeMode = time switch { "night" => 1, "day" => 2, _ => 0 };

        SetupLight();
        BuildCity(rng);

        _n = 0;
        _lastKind = -1;
        _farNext = false;             // первый район — у пилота, чтобы сразу был виден город
        BuildRoute(first: true);

        _time = 0f;
        _frame = 0;
        _level = 0f;
        _bass = 0f;
        _kick = 0f;
        _signal = 1f;
        _blocked = false;
        _bad = 0f;
        _ovr = false;
        _vel = 0f;
        _pullUps = 0;
        _mah = 0f;
        _flightSec = 0f;
        _landLeft = 0f;
        _volt = 16.4f;
        _current = 6f;
        _tearDx = new int[rows];
        _tearLeft = new int[rows];
        _rollLeft = 0f;
        _rollAmp = 0f;
        _rollPx = 0;
        _blackLeft = 0f;

        _px = new Rgba[_w * _h];
        AllocDirections();
        Interpolate();
        Render(full: true);
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;
        if (dt > 0.1f) dt = 0.1f;
        _time += dt;
        _frame++;

        // Звук: спад толчка газа на ударе.
        _kick *= MathF.Exp(-dt / 0.12f);
        float aggr = 0.6f + 0.8f * _level;

        if (!_ovr)
        {
            float v = V0 * _flySpeed * _speed * (1f + 0.5f * _kick) * (0.9f + 0.2f * _level);
            _pos += v * dt / Ds;
            if (_pos >= _n - 1) BuildRoute(first: false);
            Interpolate();
            _vel = v;
        }
        else
        {
            _vel = 0f;
        }

        // Вид камеры: дрон клонится вперёд на скорости (горизонт выше центра), камера смотрит вверх на 25°.
        float nd = Math.Clamp(_vel / (V0 * _flySpeed * 1.3f), 0f, 1f);
        // Наклон по траектории умеренный: при подъёме камера смотрит вперёд, а не в небо.
        _view = Math.Clamp(0.5f * _pitch + MountTilt - 0.7f * nd + 0.03f * _bass, -1.2f, 1.2f);
        _roll = Math.Clamp(_bank * aggr, -0.95f, 0.95f) + _ext;

        // Страховка: точка внутри стены (по сетке не должна случаться).
        int ci = CellIdx(_dx, _dy);
        if (_height[ci] > _dz - Half)
        {
            _dz = _height[ci] + 1f;
            _pullUps++;
        }

        // Батарея: 4S, садится за ~5 минут, потом «посадка» и перезапуск с полной.
        float throttle = Math.Clamp(0.35f + 0.5f * nd + 0.15f * _kick, 0f, 1f);
        _current = 6f + 18f * throttle;
        _mah += _current * dt / 3.6f;
        _flightSec += dt;
        _volt = 16.4f - 0.0022f * _mah - 0.02f * _current;
        if (_landLeft > 0f)
        {
            _landLeft -= dt;
            if (_landLeft <= 0f)
            {
                _landLeft = 0f;
                _mah = 0f;
                _flightSec = 0f;
            }
        }
        else if (_volt < 14f)
        {
            _landLeft = 6f;
        }

        // Сигнал: дистанция и прямая видимость до пилота.
        _blocked = LineBlocked(WrapDelta(_dx - PilotX), WrapDelta(_dy - PilotY), _dz);
        float target = SignalAt(_dx, _dy, _dz);
        float rate = target < _signal ? 8f : 3f;
        _signal += (target - _signal) * (1f - MathF.Exp(-rate * dt));
        _bad = _breakup * Math.Clamp((0.6f - _signal) / 0.5f, 0f, 1f);

        UpdateAnalog(dt);
        Render();
    }

    private bool _propsOn;

    public void Draw(CharGrid grid)
    {
        if (grid.Cols != _cols || grid.Rows != _rows) grid.Clear(Rgba.Black);
        int cols = Math.Min(grid.Cols, _cols), rows = Math.Min(grid.Rows, _rows);
        var cells = grid.Cells;
        int w = _w, hPx = _h;
        int fno = (int)_frame;
        bool black = _blackLeft > 0f;
        int noiseRow = (int)(Hash(0, 0, fno) * rows);
        float snowP = 0.1f * _bad;
        float desat = 0.7f * _bad;
        // Зерно есть и на хорошем сигнале: сила срывов задаёт его амплитуду (при breakup = 0 картинка чистая).
        float grain = 8f * _breakup + 6f * _bad;

        for (int r = 0; r < rows; r++)
        {
            int rowBase = r * grid.Cols;
            int dxRow = _tearDx[r];
            int tTop = 2 * r, tBot = 2 * r + 1;
            int sTop = Wrap(tTop + _rollPx, hPx), sBot = Wrap(tBot + _rollPx, hPx);
            for (int x = 0; x < cols; x++)
            {
                if (black)
                {
                    cells[rowBase + x] = r == noiseRow && Hash(x, r, fno) < 0.5f
                        ? SnowCell(x, r, fno)
                        : new Cell(' ', Rgba.White, Rgba.Black);
                    continue;
                }

                int sx = dxRow == 0 ? x : Wrap(x - dxRow, w);
                Rgba top = _px[sTop * w + sx], bot = _px[sBot * w + sx];
                if (_propsOn)
                {
                    top = PropBlend(x, tTop, top);
                    bot = PropBlend(x, tBot, bot);
                }
                // Один хеш на клетку: и для зерна, и для снега.
                float hv = Hash(x, tTop, fno);
                if (snowP > 0f && hv < snowP)
                {
                    cells[rowBase + x] = SnowCell(x, r, fno);
                    continue;
                }
                if (desat > 0f)
                {
                    top = Desat(top, desat);
                    bot = Desat(bot, desat);
                }
                int n = (int)((hv - 0.5f) * grain);
                if (n != 0)
                {
                    top = Add(top, n);
                    bot = Add(bot, n);
                }
                cells[rowBase + x] = new Cell('▀', top, bot);
            }
        }

        if (_osd && !black) DrawOsd(grid);
    }

    public void OnAudio(in AudioFrame audio)
    {
        if (!audio.Active)
        {
            _level = 0f;
            _bass = 0f;
            _kick = 0f;
            return;
        }
        _level = audio.Level;
        _bass = audio.Bass;
        if (audio.Beat) _kick = 1f;
    }

    /// <summary>Сигнал в точке (x, y, z) по координатам мира: дистанция до пилота и прямая видимость.</summary>
    internal float SignalAt(float x, float y, float z)
    {
        float dx = WrapDelta(x - PilotX), dy = WrapDelta(y - PilotY);
        float dz = z - Head;
        float d = MathF.Sqrt(dx * dx + dy * dy + dz * dz) / SignalRange;
        float q = Math.Clamp(1f - d * d, 0.05f, 1f);
        if (LineBlocked(dx, dy, z)) q *= 0.4f;
        return q;
    }

    /// <summary>Перекрыт ли луч от пилота к точке (PilotX + dx, PilotY + dy, z) домом.</summary>
    internal bool LineBlocked(float dx, float dy, float z)
    {
        float dz = z - Head;
        float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        int m = (int)MathF.Ceiling(len / 0.5f);
        for (int k = 1; k < m; k++)
        {
            float t = k / (float)m;
            if (_height[CellIdx(PilotX + dx * t, PilotY + dy * t)] > Head + dz * t) return true;
        }
        return false;
    }

    /// <summary>Точка (x, y, z) внутри дома: высота стены в её клетке выше z.</summary>
    internal bool InsideAt(float x, float y, float z) => _height[CellIdx(x, y)] > z;

    /// <summary>Тестовый хук: поставить дрон в точку и отключить маршрут (сигнал и кадр считаются как обычно).</summary>
    internal void DebugPlaceDrone(float x, float y, float z)
    {
        _ovr = true;
        _dx = x;
        _dy = y;
        _dz = z;
    }

    // ---------- Освещение и палитры ----------

    private void SetupLight()
    {
        Rgba[] palette;
        float lr, lg, lb;
        switch (_timeMode)
        {
            case 1: // ночь: темнее, огней больше
                _litP = 0.45f; _lampOn = true;
                _warm = Rgba.Hex(0xFFB060); _glass = Rgba.Hex(0x0A0E18);
                _zenith = Rgba.Hex(0x03050E); _horizon = Rgba.Hex(0x1A2540); _glow = Rgba.Hex(0x3A4A80); _skyTop = 0.25f;
                _fogCol = Rgba.Hex(0x0C1020); _asphalt = Rgba.Hex(0x08090C); _yard = Rgba.Hex(0x0E130D);
                _lamp = Rgba.Hex(0xFFD27A);
                palette = DuskPalette;
                lr = 0.6f * 1.2f; lg = 0.6f; lb = 0.6f * 0.9f;
                break;
            case 2: // день: голубое небо, серые дома, окна отражают небо
                _litP = 0f; _lampOn = false;
                _warm = Rgba.Hex(0xFFE6B0); _glass = Rgba.Hex(0x4C6A88);
                _zenith = Rgba.Hex(0x3A78D0); _horizon = Rgba.Hex(0xD6E8F6); _glow = Rgba.Hex(0xFFFFFF); _skyTop = 0.6f;
                _fogCol = Rgba.Hex(0xB8C8D8); _asphalt = Rgba.Hex(0x4A4D54); _yard = Rgba.Hex(0x4F7A46);
                _lamp = Rgba.Hex(0xFFE2A0);
                palette = DayPalette;
                lr = 1.1f; lg = 1.1f; lb = 1.0f;
                break;
            default: // сумерки: тёмный синий верх, оранжевая полоса у горизонта
                _litP = 0.35f; _lampOn = true;
                _warm = Rgba.Hex(0xFFC070); _glass = Rgba.Hex(0x141826);
                _zenith = Rgba.Hex(0x0E1A3A); _horizon = Rgba.Hex(0xFF8A4C); _glow = Rgba.Hex(0xFFB070); _skyTop = 0.2f;
                _fogCol = Rgba.Hex(0x2B2A38); _asphalt = Rgba.Hex(0x101216); _yard = Rgba.Hex(0x1A2218);
                _lamp = Rgba.Hex(0xFFD27A);
                palette = DuskPalette;
                lr = 1.7f; lg = 1.25f; lb = 0.9f;
                break;
        }
        _matBase = new Rgba[palette.Length];
        _matLit = new Rgba[palette.Length];
        for (int i = 0; i < palette.Length; i++)
        {
            Rgba b = _timeMode == 1 ? Shade(palette[i], 0.6f) : palette[i];
            _matBase[i] = b;
            _matLit[i] = Tint(b, lr, lg, lb);
        }
        BuildSkyTable();
    }

    /// <summary>Небо по углу возвышения: таблица на 256 шагов (0 — горизонт, 255 — зенит). Без ветвлений на пиксель.</summary>
    private void BuildSkyTable()
    {
        _skyTab = new Rgba[256];
        for (int i = 0; i < 256; i++)
        {
            float e = i / 255f;
            float x = Math.Clamp(e / _skyTop, 0f, 1f);
            float t = x * x * (3f - 2f * x);
            Rgba c = Mix(_horizon, _zenith, t);
            float gl = MathF.Max(0f, 1f - e / 0.3f);
            _skyTab[i] = Mix(c, _glow, 0.45f * gl * gl);
        }
    }

    // ---------- Город ----------

    private void BuildCity(Random rng)
    {
        _height = new byte[N * N];
        _mat = new byte[N * N];
        _macroMax = new byte[N * N];
        for (int b = 0; b < Blocks * Blocks; b++)
        {
            float r = rng.NextSingle();
            _block[b] = r < 0.12f ? BEmpty : r < 0.42f ? BPanel : r < 0.66f ? BGap : BHouses;
        }

        // Две высотки-доминанты и хотя бы один квартал с щелью между домами (для пролёта «щель»).
        // Высотки стоят в досягаемости районов полёта (5–12 кварталов от пилота), иначе облёт до них не добраться.
        do { _towerA = rng.Next(Blocks * Blocks); } while (!InReach(_towerA));
        do { _towerB = rng.Next(Blocks * Blocks); } while (_towerB == _towerA || !InReach(_towerB));
        _block[_towerA] = BTower;
        _block[_towerB] = BTower;
        bool hasGap = false;
        for (int b = 0; b < _block.Length; b++) if (_block[b] == BGap) hasGap = true;
        if (!hasGap)
        {
            int g;
            do { g = rng.Next(Blocks * Blocks); } while (_block[g] == BTower);
            _block[g] = BGap;
        }

        for (int b = 0; b < Blocks * Blocks; b++)
        {
            int ox = (b / Blocks) * Period, oy = (b % Blocks) * Period;
            switch (_block[b])
            {
                case BTower:
                    Fill(ox, oy, 7, 7, 17, 17, 70 + rng.Next(36), rng.Next(6));
                    break;
                case BPanel:
                    Fill(ox, oy, 5, 5, 19, 11, 24 + rng.Next(17), rng.Next(6));
                    Fill(ox, oy, 5, 13, 19, 19, 24 + rng.Next(17), rng.Next(6));
                    break;
                case BGap:
                    // Щель шириной 8 клеток: центр x = 12, стены в 4 м с обеих сторон.
                    Fill(ox, oy, 5, 5, 8, 19, 10 + rng.Next(21), rng.Next(6));
                    Fill(ox, oy, 16, 5, 19, 19, 10 + rng.Next(21), rng.Next(6));
                    break;
                case BHouses:
                    Fill(ox, oy, 5, 5, 11, 11, 5 + rng.Next(9), rng.Next(6));
                    Fill(ox, oy, 13, 5, 19, 11, 5 + rng.Next(9), rng.Next(6));
                    Fill(ox, oy, 5, 13, 11, 19, 5 + rng.Next(9), rng.Next(6));
                    Fill(ox, oy, 13, 13, 19, 19, 5 + rng.Next(9), rng.Next(6));
                    break;
            }
        }

        // Максимум высоты в плитке 6×6 для каждой клетки: по нему DDA пропускает пустые участки луча.
        for (int ty = 0; ty < N; ty += Macro)
        {
            for (int tx = 0; tx < N; tx += Macro)
            {
                int mx = 0;
                for (int y = 0; y < Macro; y++)
                    for (int x = 0; x < Macro; x++)
                        mx = Math.Max(mx, _height[(ty + y) * N + tx + x]);
                for (int y = 0; y < Macro; y++)
                    for (int x = 0; x < Macro; x++)
                        _macroMax[(ty + y) * N + tx + x] = (byte)mx;
            }
        }
    }

    private void Fill(int ox, int oy, int x0, int y0, int x1, int y1, int h, int mat)
    {
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = Mod(oy + y) * N + Mod(ox + x);
                _height[i] = (byte)h;
                _mat[i] = (byte)mat;
            }
        }
    }

    private static int Mod(int a) => ((a % N) + N) % N;

    private static int Wrap(int a, int n) => ((a % n) + n) % n;

    private static float WrapDelta(float d) => d - N * MathF.Round(d / N);

    private static float WrapF(float v) => v - N * MathF.Floor(v / N);

    private int CellIdx(float x, float y) => Mod((int)MathF.Floor(y)) * N + Mod((int)MathF.Floor(x));

    /// <summary>Квартал рядом с центром района нужного типа; возвращает смещение квартала без модуля.</summary>
    private bool PickNear(out int ux, out int uy, int mask)
    {
        for (int t = 0; t < 24; t++)
        {
            int cx = _secBx + _rng.Next(-1, 2), cy = _secBy + _rng.Next(-1, 2);
            if (((1 << _block[Wrap(cx, Blocks) * Blocks + Wrap(cy, Blocks)]) & mask) != 0)
            {
                ux = cx;
                uy = cy;
                return true;
            }
        }
        ux = 0;
        uy = 0;
        return false;
    }

    // ---------- Маршрут ----------

    private bool Append(float x, float y, float z, float ext, int kind)
    {
        if (_n >= MaxSamples) return false;
        _sx[_n] = x;
        _sy[_n] = y;
        _sz[_n] = z;
        _sext[_n] = ext;
        _skind[_n] = kind;
        _n++;
        return true;
    }

    /// <summary>Точка свободна: дом выше дрона не ближе ClearCells клеток по сетке, углы дрона над полом.</summary>
    private bool Clear(float x, float y, float z)
    {
        if (z < 1f) return false;
        for (int k = 0; k < CornerX.Length; k++)
        {
            if (_height[CellIdx(x + CornerX[k], y + CornerY[k])] >= z - 0.25f) return false;
        }
        // Зазор до стен: расстояние от точки до ближайшей стены дома выше дрона не меньше MinWall.
        int cx = (int)MathF.Floor(x), cy = (int)MathF.Floor(y);
        for (int dy = -ClearCells; dy <= ClearCells; dy++)
        {
            for (int dx = -ClearCells; dx <= ClearCells; dx++)
            {
                int ex = cx + dx, ey = cy + dy;
                if (_height[CellIdx(ex, ey)] <= z) continue;
                float nx = Math.Clamp(x, ex, ex + 1f), ny = Math.Clamp(y, ey, ey + 1f);
                if ((x - nx) * (x - nx) + (y - ny) * (y - ny) < MinWall * MinWall) return false;
            }
        }
        return true;
    }

    private bool ValidRange(int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            if (!Clear(_sx[i], _sy[i], _sz[i])) return false;
        }
        return true;
    }

    /// <summary>Кубическая кривая от последней точки к (ex, ey, ez): вертикальный подъём, над крышами, вертикальный спуск.
    /// Между районами (дальше 30 м) — подъём до LongLift.</summary>
    private bool Transit(float ex, float ey, float ez)
    {
        float px = _sx[_n - 1], py = _sy[_n - 1], pz = _sz[_n - 1];
        float hd = MathF.Sqrt((ex - px) * (ex - px) + (ey - py) * (ey - py));
        float dist = MathF.Sqrt(hd * hd + (ez - pz) * (ez - pz));
        if (dist < 0.5f) return true;
        // Высота прямой над самым высоким домом вдоль отрезка (с запасом): кривая пройдёт над крышами.
        float lineMax = 0f;
        int steps = Math.Max(1, (int)MathF.Ceiling(hd));
        for (int s = 0; s <= steps; s++)
        {
            float f = s / (float)steps;
            lineMax = MathF.Max(lineMax, _macroMax[CellIdx(px + (ex - px) * f, py + (ey - py) * f)]);
        }
        for (int attempt = 0; attempt < 4; attempt++)
        {
            float lift = MathF.Max(MathF.Max(pz, ez) + 6f, lineMax + 6f) + attempt * 8f;
            float len = MathF.Abs(lift - pz) + hd + MathF.Abs(lift - ez);
            int m = Math.Max(2, (int)MathF.Ceiling(len / Ds));
            int mark = _n;
            bool ok = true;
            for (int k = 1; k <= m && ok; k++)
            {
                float t = k / (float)m, u = 1f - t;
                float a = u * u * u, b = 3f * u * u * t, c = 3f * u * t * t, d = t * t * t;
                float x = (a + b) * px + (c + d) * ex;
                float y = (a + b) * py + (c + d) * ey;
                float z = a * pz + b * lift + c * lift + d * ez;
                if (!Append(x, y, z, 0f, KTransit) || !Clear(x, y, z)) ok = false;
            }
            if (ok) return true;
            _n = mark;
        }
        return false;
    }

    private void BuildRoute(bool first)
    {
        if (first)
        {
            _n = 0;
            Append(PilotX, PilotY, 1.6f, 0f, KTransit);
        }
        else
        {
            float x = _sx[_n - 1], y = _sy[_n - 1], z = _sz[_n - 1];
            _n = 0;
            Append(x, y, z, 0f, KTransit);
        }

        // Районы чередуются: дальний (100–400 м от пилота) и домашний, у пилота. Дрон уходит и возвращается.
        // Перелёт между районами длинный, поэтому маршрут не кончается, пока в районе не выполнено хотя бы три манёвра.
        int guard = 0, plansLeft = 0, added = 0;
        while (((_n - 1) * Ds < RouteLen || added < 3) && _n < MaxSamples - 2000 && guard++ < 300)
        {
            if (plansLeft == 0)
            {
                ChooseSector();
                plansLeft = 3;
            }
            int kind = _rng.Next(5);
            if (kind == _lastKind) kind = (kind + 1) % 5;
            // Облёт возможен, только если высотка в досягаемости: иначе берём другой манёвр.
            if (kind == KOrbit && !TowerOffset(_towerA, out _, out _) && !TowerOffset(_towerB, out _, out _)) kind = KDive;
            int mark = _n;
            if (Plan(kind))
            {
                _lastKind = kind;
                added++;
            }
            else
            {
                _n = mark;
            }
            plansLeft--;
        }
        if (_n < 2) Append(_sx[0] + Ds, _sy[0], _sz[0], 0f, KTransit);

        Attributes();
        _pos = 0f;
    }

    private void ChooseSector()
    {
        if (_farNext)
        {
            float a = _rng.NextSingle() * MathF.Tau;
            float dist = 4.5f + _rng.NextSingle() * 9f;    // кварталов: 108–324 м (плюс разброс района и манёвр, итог < 400 м)
            _secBx = (int)MathF.Round(dist * MathF.Cos(a));
            _secBy = (int)MathF.Round(dist * MathF.Sin(a));
            _farNext = false;
        }
        else
        {
            _secBx = 0;
            _secBy = 0;
            _farNext = true;
        }
    }

    private static bool InReach(int tower)
    {
        if (!TowerOffset(tower, out int bx, out int by)) return false;
        float d = MathF.Sqrt((float)(bx * bx + by * by));
        return d >= 5f && d <= 12f;
    }

    /// <summary>Смещение высотки от дома в кварталах (с учётом тора), если она в пределах 13.5 кварталов.</summary>
    private static bool TowerOffset(int tower, out int bx, out int by)
    {
        bx = tower / Blocks;
        by = tower % Blocks;
        if (bx > Blocks / 2) bx -= Blocks;
        if (by > Blocks / 2) by -= Blocks;
        return MathF.Sqrt((float)(bx * bx + by * by)) <= 13.5f;
    }

    private bool Plan(int kind) => kind switch
    {
        KOrbit => PlanOrbit(),
        KDive => PlanDive(),
        KCorridor => PlanCorridor(),
        KGap => PlanGap(),
        _ => PlanCandle(),
    };

    /// <summary>Облёт высотки по кругу с креном: радиус 11.5 м, полтора оборота.</summary>
    private bool PlanOrbit()
    {
        // Высотка — ближайшая копия к дому (тор). Облёт идёт в её районе, поэтому дальше 13.5 кварталов не берём.
        if (!TowerOffset(_towerA, out int bxA, out int byA) && !TowerOffset(_towerB, out bxA, out byA)) return false;
        int bx = bxA, by = byA;
        _secBx = bx;
        _secBy = by;
        float cx = bx * Period + 12f, cy = by * Period + 12f;
        float H = _height[CellIdx(cx, cy)];
        float th0 = _rng.NextSingle() * MathF.Tau;
        float dir = _rng.NextSingle() < 0.5f ? 1f : -1f;
        float zOrb = Math.Clamp(H * 0.5f, 14f, 44f);
        const float loops = 1.5f;

        if (!Transit(cx + Radius * MathF.Cos(th0), cy + Radius * MathF.Sin(th0), zOrb)) return false;
        int from = _n;
        int m = (int)MathF.Ceiling(MathF.Tau * Radius * loops / Ds);
        for (int k = 1; k <= m; k++)
        {
            float f = k / (float)m;
            float th = th0 + dir * MathF.Tau * loops * f;
            if (!Append(cx + Radius * MathF.Cos(th), cy + Radius * MathF.Sin(th), zOrb + 3f * f, 0f, KOrbit)) return false;
        }
        return ValidRange(from, _n);
    }

    /// <summary>Нырок с крыши вдоль фасада на 4.5 м от стены и выход над улицей.</summary>
    private bool PlanDive()
    {
        if (!PickNear(out int ux, out int uy, (1 << BPanel) | (1 << BTower))) return false;
        int type = _block[Wrap(ux, Blocks) * Blocks + Wrap(uy, Blocks)];
        float ox = ux * Period, oy = uy * Period;
        float facade = type == BTower ? 17f : 19f;
        float h = _height[CellIdx(ox + 8f, oy + 8f)];
        float fx = ox + facade + 4.5f, fy = oy + 12f;
        float top = h + 3f;
        if (!Transit(fx, fy, top)) return false;

        int from = _n;
        int m = (int)MathF.Ceiling((top - 3f) / Ds);
        for (int k = 1; k <= m; k++)
        {
            if (!Append(fx, fy, top - (top - 3f) * k / m, 0f, KDive)) return false;
        }
        float dirY = _rng.NextSingle() < 0.5f ? 1f : -1f;
        int m2 = (int)MathF.Ceiling(16f / Ds);
        for (int k = 1; k <= m2; k++)
        {
            float f = k / (float)m2;
            if (!Append(fx, fy + dirY * 16f * f, 3f + 1.5f * f, 0f, KDive)) return false;
        }
        return ValidRange(from, _n);
    }

    /// <summary>Низкий пролёт по улице (центр улицы в 4.5 м от стен), иногда с флипом на 360° за ~0.4 с.</summary>
    private bool PlanCorridor()
    {
        if (!PickNear(out int ux, out int uy, 0x1F)) return false;
        float x = ux * Period + 0.5f, y0 = uy * Period + 0.5f;
        float dir = _rng.NextSingle() < 0.5f ? 1f : -1f;
        if (!Transit(x, y0, 3.4f)) return false;

        int from = _n;
        const float len = 36f;
        int m = (int)MathF.Ceiling(len / Ds);
        bool flip = _rng.NextSingle() < 0.4f;
        int flipFrom = m / 2, flipCount = (int)(4f / Ds);
        for (int k = 1; k <= m; k++)
        {
            float f = k / (float)m;
            float z = 3.4f + 0.25f * MathF.Sin(k * 0.05f);
            float ext = 0f;
            if (flip && k > flipFrom && k <= flipFrom + flipCount)
            {
                float p = (k - flipFrom) / (float)flipCount;
                ext = MathF.Tau * p * p * (3f - 2f * p);
            }
            if (!Append(x, y0 + dir * len * f, z, ext, KCorridor)) return false;
        }
        return ValidRange(from, _n);
    }

    /// <summary>Пролёт через щель между двумя домами квартала (центр щели x = 12, стены в 4 м).</summary>
    private bool PlanGap()
    {
        if (!PickNear(out int ux, out int uy, 1 << BGap)) return false;
        float ox = ux * Period, oy = uy * Period;
        float x = ox + 12f;
        float dir = _rng.NextSingle() < 0.5f ? 1f : -1f;
        float y0 = dir > 0f ? oy + 1f : oy + 23f;
        if (!Transit(x, y0, 4.5f)) return false;

        int from = _n;
        const float len = 22f;
        int m = (int)MathF.Ceiling(len / Ds);
        for (int k = 1; k <= m; k++)
        {
            float f = k / (float)m;
            if (!Append(x, y0 + dir * len * f, 4.5f + 0.3f * MathF.Sin(k * 0.1f), 0f, KGap)) return false;
        }
        return ValidRange(from, _n);
    }

    /// <summary>Свеча: вертикально вверх над улицей, полуразворот на вершине, выход вперёд.</summary>
    private bool PlanCandle()
    {
        if (!PickNear(out int ux, out int uy, 0x1F)) return false;
        float x = ux * Period + 0.5f, y = uy * Period + 0.5f;
        if (!Transit(x, y, 3f)) return false;

        int from = _n;
        float maxH = 0f;
        for (int dy = -7; dy <= 7; dy++)
        {
            for (int dx = -7; dx <= 7; dx++)
            {
                maxH = MathF.Max(maxH, _height[CellIdx(x + dx, y + dy)]);
            }
        }
        float top = Math.Clamp(maxH + 14f, 36f, 110f);
        int m = (int)MathF.Ceiling((top - 3f) / Ds);
        for (int k = 1; k <= m; k++)
        {
            if (!Append(x, y, 3f + (top - 3f) * k / m, 0f, KCandle)) return false;
        }

        float s = _rng.NextSingle() < 0.5f ? 1f : -1f;
        const float r = 1.6f;
        float phi0 = -s * MathF.PI / 2f;
        int m2 = (int)MathF.Ceiling(MathF.PI * r / Ds);
        for (int k = 1; k <= m2; k++)
        {
            float phi = phi0 + MathF.PI * k / m2;
            if (!Append(x + r * MathF.Cos(phi), y + r * s + r * MathF.Sin(phi), top, 0f, KCandle)) return false;
        }
        int m3 = (int)MathF.Ceiling(10f / Ds);
        for (int k = 1; k <= m3; k++)
        {
            if (!Append(x, y + 2f * r * s + 10f * s * k / m3, top, 0f, KCandle)) return false;
        }
        return ValidRange(from, _n);
    }

    /// <summary>Атрибуты точек: курс (развёрнутый), тангаж и крен из кривизны курса на скорости V0.</summary>
    private void Attributes()
    {
        float prevYaw = 0f;
        bool first = true;
        for (int i = 0; i < _n; i++)
        {
            int a = Math.Max(0, i - 2), b = Math.Min(_n - 1, i + 2);
            float dx = _sx[b] - _sx[a], dy = _sy[b] - _sy[a], dz = _sz[b] - _sz[a];
            float hl = MathF.Sqrt(dx * dx + dy * dy);
            float yaw;
            if (hl > 1e-3f)
            {
                float raw = MathF.Atan2(dy, dx);
                yaw = first ? raw : prevYaw + WrapPi(raw - prevYaw);
            }
            else
            {
                yaw = first ? 0f : prevYaw;
            }
            _syaw[i] = yaw;
            _spit[i] = MathF.Atan2(dz, hl);
            prevYaw = yaw;
            first = false;
        }
        for (int i = 0; i < _n; i++)
        {
            int a = Math.Max(0, i - 2), b = Math.Min(_n - 1, i + 2);
            float span = (b - a) * Ds;
            float dyaw = span > 0f ? (_syaw[b] - _syaw[a]) / span : 0f;
            _sbank[i] = MathF.Atan(V0 * V0 * dyaw / Gravity);
        }
    }

    private static float WrapPi(float a) => a - MathF.Tau * MathF.Round(a / MathF.Tau);

    /// <summary>Положение дрона на маршруте (линейная интерполяция между точками).</summary>
    private void Interpolate()
    {
        int i = (int)_pos;
        if (i < 0) i = 0;
        if (i > _n - 2) i = _n - 2;
        float f = Math.Clamp(_pos - i, 0f, 1f);
        _dx = Lerp(_sx[i], _sx[i + 1], f);
        _dy = Lerp(_sy[i], _sy[i + 1], f);
        _dz = Lerp(_sz[i], _sz[i + 1], f);
        _yaw = Lerp(_syaw[i], _syaw[i + 1], f);
        _pitch = Lerp(_spit[i], _spit[i + 1], f);
        _bank = Lerp(_sbank[i], _sbank[i + 1], f);
        _ext = Lerp(_sext[i], _sext[i + 1], f);
        _curKind = _skind[i];
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // ---------- Аналоговый сигнал ----------

    private void UpdateAnalog(float dt)
    {
        float pTear = 0.03f * _bad;
        for (int r = 0; r < _rows; r++)
        {
            if (_tearLeft[r] > 0)
            {
                if (--_tearLeft[r] == 0) _tearDx[r] = 0;
            }
            else if (_bad > 0.01f && _rng.NextSingle() < pTear)
            {
                _tearLeft[r] = 2 + _rng.Next(4);
                int d = 3 + _rng.Next(10);
                _tearDx[r] = _rng.Next(2) == 0 ? d : -d;
            }
        }

        if (_rollLeft > 0f)
        {
            _rollLeft -= dt;
            _rollPx = (int)MathF.Round(_rollAmp * Math.Clamp(_rollLeft / 0.5f, 0f, 1f));
        }
        else
        {
            _rollPx = 0;
            if (_bad > 0.3f && _rng.NextSingle() < 0.0008f * _bad)
            {
                _rollLeft = 0.5f;
                _rollAmp = (_rng.Next(2) == 0 ? 1f : -1f) * (2 + _rng.Next(5));
            }
        }

        if (_blackLeft > 0f) _blackLeft -= dt;
        else if (_bad > 0.6f && _rng.NextSingle() < 0.0006f) _blackLeft = 1f;
    }

    // ---------- Рейкаст ----------

    private void AllocDirections()
    {
        int n = _w * _h;
        _lx = new float[n];
        _ly = new float[n];
        _lz = new float[n];
        float half = _w * 0.5f;
        for (int py = 0; py < _h; py++)
        {
            for (int px = 0; px < _w; px++)
            {
                float nx = (px + 0.5f - half) / half;
                // Пиксель выше ячейки вдвое: физическая высота строки пикселя = cellAspect / 2 ширины клетки.
                float ny = (_h * 0.5f - (py + 0.5f)) * (_cellAspect * 0.5f) / half;
                float r = MathF.Sqrt(nx * nx + ny * ny);
                float a = r * AngMax;
                int i = py * _w + px;
                _lz[i] = MathF.Cos(a);
                if (r < 1e-6f)
                {
                    _lx[i] = 0f;
                    _ly[i] = 0f;
                }
                else
                {
                    float sa = MathF.Sin(a) / r;
                    _lx[i] = sa * nx;
                    _ly[i] = sa * ny;
                }
            }
        }
    }

    /// <summary>Кадр: лучи по строкам одной чётности (поле). Полный кадр — только при Init.
    /// Аналоговое видео чересстрочное: за кадр обновляется одно поле, второе остаётся с прошлого кадра.</summary>
    private void Render(bool full = false)
    {
        float cx = WrapF(_dx), cy = WrapF(_dy), cz = _dz;
        float cv = MathF.Cos(_view);
        float fx = cv * MathF.Cos(_yaw), fy = cv * MathF.Sin(_yaw), fz = MathF.Sin(_view);
        // Правый вектор без крена: f × Z; верх = R × f.
        float rx0 = MathF.Sin(_yaw), ry0 = -MathF.Cos(_yaw), rz0 = 0f;
        float ux0 = ry0 * fz - rz0 * fy, uy0 = rz0 * fx - rx0 * fz, uz0 = rx0 * fy - ry0 * fx;
        float cr = MathF.Cos(_roll), sr = MathF.Sin(_roll);
        float rx = cr * rx0 + sr * ux0, ry = cr * ry0 + sr * uy0, rz = cr * rz0 + sr * uz0;
        float upx = -sr * rx0 + cr * ux0, upy = -sr * ry0 + cr * uy0, upz = -sr * rz0 + cr * uz0;

        int first = full ? 0 : (int)(_frame & 1), step = full ? 1 : 2;
        for (int py = first; py < _h; py += step)
        {
            for (int px = 0; px < _w; px++)
            {
                int i = py * _w + px;
                float lx = _lx[i], ly = _ly[i], lz = _lz[i];
                float dx = lx * rx + ly * upx + lz * fx;
                float dy = lx * ry + ly * upy + lz * fy;
                float dz = lx * rz + ly * upz + lz * fz;
                _px[i] = Trace(cx, cy, cz, dx, dy, dz);
            }
        }

        // Пропеллеры: появляются в углах кадра то и дело.
        _propsOn = MathF.Sin(_time * 0.21f) > 0.2f;
    }

    /// <summary>Луч: 2D DDA по клеткам с пропуском пустых кварталов; земля — плоскость z = 0 (аналитически).</summary>
    private Rgba Trace(float ox, float oy, float oz, float dx, float dy, float dz)
    {
        int ix = (int)MathF.Floor(ox), iy = (int)MathF.Floor(oy);     // ox, oy в [0, N); индексы без модуля
        const float Far = 1e9f;
        float tdx = dx != 0f ? MathF.Abs(1f / dx) : Far;
        float tdy = dy != 0f ? MathF.Abs(1f / dy) : Far;
        int sx = dx >= 0f ? 1 : -1, sy = dy >= 0f ? 1 : -1;
        float tmx = dx > 0f ? (ix + 1 - ox) * tdx : dx < 0f ? (ox - ix) * tdx : Far;
        float tmy = dy > 0f ? (iy + 1 - oy) * tdy : dy < 0f ? (oy - iy) * tdy : Far;
        float tGround = dz < 0f ? -oz / dz : Far;
        float t = 0f;
        int axis = 0;

        for (int step = 0; step < 4096; step++)
        {
            float tn = tmx < tmy ? tmx : tmy;
            int cxw = ix < 0 ? ix + N : ix >= N ? ix - N : ix;
            int cyw = iy < 0 ? iy + N : iy >= N ? iy - N : iy;
            int ci = cyw * N + cxw;
            float zc = oz + dz * t, zn = oz + dz * tn;
            float h = _height[ci];
            float zmin = MathF.Min(zc, zn);
            if (h > 0f && h > zmin)
            {
                bool roof = zc > h;
                float tHit = roof ? t + (zc - h) / (zc - zn) * (tn - t) : t;
                Rgba c = roof
                    ? Shade(_matBase[_mat[ci]], 0.5f)
                    : WallColor(ci, axis, axis == 0 ? sx : sy, ox + dx * tHit, oy + dy * tHit, oz + dz * tHit);
                return Fog(c, tHit);
            }
            if (tGround <= tn)
            {
                float gx = ox + dx * tGround, gy = oy + dy * tGround;
                return Fog(GroundColor(cxw, cyw, gx, gy), tGround);
            }
            // Дальше MaxRay: луч вниз уходит в дымку у горизонта, луч вверх — в небо.
            if (tn > MaxRay) return dz < 0f ? _fogCol : Sky(dz);

            // Пустой квартал: если на всём отрезке до его выхода луч выше всех домов квартала, прыгаем к выходу.
            if (_macroMax[ci] < zmin)
            {
                float bx0 = ix - PosModM(ix), by0 = iy - PosModM(iy);
                float tBx = dx != 0f ? ((sx > 0 ? bx0 + Macro : bx0) - ox) / dx : Far;
                float tBy = dy != 0f ? ((sy > 0 ? by0 + Macro : by0) - oy) / dy : Far;
                float tExit = MathF.Min(MathF.Min(tBx, tBy), MathF.Min(tGround, MaxRay));
                if (tExit > tn)
                {
                    float zExit = oz + dz * tExit;
                    if (_macroMax[ci] < MathF.Min(zc, zExit))
                    {
                        t = tExit;
                        float px = ox + dx * t, py = oy + dy * t;
                        ix = (int)MathF.Floor(px + sx * 1e-4f);
                        iy = (int)MathF.Floor(py + sy * 1e-4f);
                        tmx = t + (sx > 0 ? ix + 1 - px : px - ix) * tdx;
                        tmy = t + (sy > 0 ? iy + 1 - py : py - iy) * tdy;
                        continue;
                    }
                }
            }
            if (zc > MaxWallH && zn > MaxWallH && tGround >= Far) return Sky(dz);

            // Шаг по клетке.
            if (tmx < tmy)
            {
                t = tmx;
                tmx += tdx;
                ix += sx;
                axis = 0;
            }
            else
            {
                t = tmy;
                tmy += tdy;
                iy += sy;
                axis = 1;
            }
        }
        return dz < 0f ? _fogCol : Sky(dz);
    }

    private static int PosModM(int v) => ((v % Macro) + Macro) % Macro;

    private Rgba WallColor(int ci, int axis, int sgn, float hx, float hy, float hz)
    {
        int m = _mat[ci];
        // Солнце на западе: западные грани (ray идёт на восток, sgn > 0 по x) — освещены закатом.
        Rgba b;
        float shade;
        if (axis == 0)
        {
            b = sgn > 0 ? _matLit[m] : _matBase[m];
            shade = sgn > 0 ? 1f : 0.55f;
        }
        else
        {
            b = _matBase[m];
            shade = sgn > 0 ? 0.8f : 0.62f;
        }
        float u = axis == 0 ? hy : hx;
        float uu = u / 1.5f, vv = hz / 2f;
        float fu = uu - MathF.Floor(uu), fv = vv - MathF.Floor(vv);
        if (fu > 0.3f && fu < 0.7f && fv > 0.3f && fv < 0.75f)
        {
            if (_timeMode != 2 && Hash((int)MathF.Floor(uu), (int)MathF.Floor(vv), ci) < _litP)
                return Shade(_warm, shade);
            return Shade(_glass, shade);
        }
        return Shade(b, shade);
    }

    /// <summary>Земля: асфальт улиц (свободные колонки и строки), тёмные дворы внутри кварталов, фонари на осях улиц.</summary>
    private Rgba GroundColor(int cxw, int cyw, float hx, float hy)
    {
        bool street = StreetTab[cxw] || StreetTab[cyw];
        Rgba c = street ? _asphalt : _yard;
        if (_lampOn && LampX[cxw] && LampY[cyw])
        {
            float fx = hx - MathF.Floor(hx), fy = hy - MathF.Floor(hy);
            if ((fx < 0.2f || fx > 0.8f) && fy > 0.4f && fy < 0.6f) c = _lamp;
        }
        return c;
    }

    /// <summary>Таблицы по клетке (по модулю квартала): улица — колонка/строка свободна; фонарь — на оси улицы.</summary>
    private static readonly bool[] StreetTab = MakeTab(c => { int m = c % Period; return m >= 19 || m <= 4; });
    private static readonly bool[] LampX = MakeTab(c => c % Period == 0);
    private static readonly bool[] LampY = MakeTab(c => c % 6 == 0);

    private static bool[] MakeTab(Func<int, bool> f)
    {
        var t = new bool[N];
        for (int c = 0; c < N; c++) t[c] = f(c);
        return t;
    }

    private Rgba Sky(float dz)
    {
        int i = (int)(MathF.Min(1f, MathF.Max(0f, dz)) * 255f);
        return _skyTab[i];
    }

    private Rgba Fog(Rgba c, float t)
    {
        float f = (t - FogStart) / (MaxRay - FogStart);
        f = MathF.Min(1f, MathF.Max(0f, f)) * FogMax;
        return f <= 0f ? c : Mix(c, _fogCol, f);
    }

    /// <summary>Линейная смесь цветов; клэмп без ветвлений (кадр — миллионы таких операций).</summary>
    private static Rgba Mix(Rgba a, Rgba b, float t) => new(
        ClampByte(a.R + (b.R - a.R) * t), ClampByte(a.G + (b.G - a.G) * t), ClampByte(a.B + (b.B - a.B) * t), 255);

    private static Rgba Shade(Rgba c, float k) => new(ClampByte(c.R * k), ClampByte(c.G * k), ClampByte(c.B * k), 255);

    private static Rgba Tint(Rgba c, float r, float g, float b) => new(ClampByte(c.R * r), ClampByte(c.G * g), ClampByte(c.B * b), 255);

    private static byte ClampByte(float v) => (byte)MathF.Min(255f, MathF.Max(0f, v + 0.5f));

    /// <summary>Размытые кончики пропеллеров в верхних углах кадра: лопасти — тёмные дуги у самого края.
    /// Накладываются при выводе (Draw), чтобы не копить затемнение в буфере между полями.</summary>
    private Rgba PropBlend(int px, int py, Rgba c)
    {
        float w = _w;
        float k = _cellAspect * 0.5f;
        float yPhys = py * k;
        float physH = _h * k;
        float radius = 0.3f * w, band = 0.035f * w, reach = radius + band;
        float hubY = -0.25f * physH;
        float alpha = 0f;
        for (int hub = 0; hub < 2; hub++)
        {
            float hubX = hub == 0 ? -0.25f * w : 1.25f * w;
            float ddx = px - hubX, ddy = yPhys - hubY;
            if (MathF.Abs(ddx) > reach || MathF.Abs(ddy) > reach) continue;
            float r = MathF.Sqrt(ddx * ddx + ddy * ddy);
            float dr = MathF.Abs(r - radius);
            if (dr >= band) continue;
            float phase = _time * 30f + hub * 0.7f;
            float rel = MathF.Atan2(ddy, ddx) - phase;
            rel -= MathF.PI * MathF.Round(rel / MathF.PI);
            float da = MathF.Abs(rel);
            if (da >= 0.28f) continue;
            alpha = MathF.Max(alpha, 0.5f * (1f - dr / band) * (1f - da / 0.28f));
        }
        return alpha > 0f ? Mix(c, DarkBlade, alpha) : c;
    }

    // ---------- Эффекты и OSD (только чтение состояния) ----------

    private static float Hash(int a, int b, int c)
    {
        uint h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u ^ (uint)c * 0xC2B2AE3Du;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return (h & 0xFFFFFFu) / 16777216f;
    }

    private static Cell SnowCell(int x, int y, int fno)
    {
        float hc = Hash(x, y + 31, fno);
        byte v = (byte)(120 + 130 * hc);
        return new Cell(hc < 0.5f ? '░' : '▒', new Rgba(v, v, v), new Rgba((byte)(v / 3), (byte)(v / 3), (byte)(v / 3)));
    }

    private static Rgba Desat(Rgba c, float k)
    {
        float luma = c.R * 0.3f + c.G * 0.59f + c.B * 0.11f;
        return Mix(c, new Rgba(ClampByte(luma), ClampByte(luma), ClampByte(luma)), k * 0.8f);
    }

    private static Rgba Add(Rgba c, int d) => new(
        ClampByte(c.R + d), ClampByte(c.G + d), ClampByte(c.B + d), c.A);

    private int JitterX(int row)
    {
        if (row < 0 || row >= _rows) return 0;
        return Math.Clamp(_tearDx[row], -2, 2);
    }

    /// <summary>Крупный OSD: символы 5×7 из секстантов (PixelFont), 3×3 клетки на символ.</summary>
    private void DrawOsd(CharGrid g)
    {
        int cols = g.Cols, rows = g.Rows;
        Rgba white = Rgba.Hex(0xF2F2F2);
        int blink = ((int)_frame / 15) & 1;

        // Верх: RSSI, имя крафта, таймер полёта.
        int y1 = 1;
        int x = 2 + JitterX(y1);
        x = BigStr(g, x, y1, "RSSI ", white);
        BigNum(g, x, y1, (int)(_signal * 99f), 2, white);
        BigStr(g, cols / 2 - 16 + JitterX(y1), y1, "PHOSPHOR", white);
        int secs = (int)_flightSec;
        int tx = cols - 22 + JitterX(y1);
        BigNum(g, tx, y1, (secs / 60) % 100, 2, white);
        BigChar(g, tx + 4, y1, ':', white);
        BigNum(g, tx + 8, y1, secs % 60, 2, white);

        // Ряд 2: HOME со стрелкой; справа — спутники.
        int y2 = 5;
        float hx = WrapDelta(PilotX - _dx), hy = WrapDelta(PilotY - _dy);
        int homeDist = (int)MathF.Round(MathF.Sqrt(hx * hx + hy * hy));
        x = 2 + JitterX(y2);
        x = BigStr(g, x, y2, "HOME ", white);
        x = BigNum(g, x, y2, homeDist, DigitsOf(homeDist), white);
        x = BigStr(g, x + 4, y2, "M ", white);
        BigChar(g, x, y2, HomeArrow(hx, hy), white);
        int sats = 12 - (_signal < 0.3f ? 2 : 0);
        x = cols - 30 + JitterX(y2);
        x = BigStr(g, x, y2, "SAT ", white);
        BigNum(g, x, y2, sats, 2, white);

        // Ряд 3 справа: канал видео (R1 5658).
        int y3 = 9;
        BigStr(g, cols - 30 + JitterX(y3), y3, "R1 5658", white);

        // Предупреждения по центру, мигают.
        if (_signal < 0.3f && blink == 0) BigStr(g, cols / 2 - 16, 9, "LOW RSSI", white);
        bool lowBat = _volt < 14f || _landLeft > 0f;
        if (lowBat && blink == 0) BigStr(g, cols / 2 - 22, 13, "LOW BATTERY", white);

        // Низ слева: батарея, ток, mAh.
        int yb1 = rows - 9, yb2 = rows - 5;
        x = 2 + JitterX(yb1);
        x = BigStr(g, x, yb1, "4S ", white);
        x = BigDec(g, x, yb1, _volt, white);
        BigChar(g, x, yb1, 'V', white);
        x = 2 + JitterX(yb2);
        x = BigDec(g, x, yb2, _current, white);
        x = BigStr(g, x, yb2, "A MAH ", white);
        BigNum(g, x, yb2, (int)_mah, DigitsOf((int)_mah), white);

        // Низ справа: газ, скорость, высота.
        int thr = (int)(Math.Clamp(0.35f + 0.5f * Math.Clamp(_vel / (V0 * _flySpeed * 1.3f), 0f, 1f) + 0.15f * _kick, 0f, 1f) * 100f);
        int rx = cols - 30 + JitterX(yb1);
        x = BigStr(g, rx, yb1, "THR ", white);
        BigNum(g, x, yb1, thr, DigitsOf(thr), white);
        int kmh = (int)(_vel * 3.6f);
        x = cols - 30 + JitterX(yb2);
        x = BigNum(g, x, yb2, kmh, DigitsOf(kmh), white);
        BigStr(g, x + 4, yb2, "KM/H", white);
        int alt = (int)MathF.Max(0f, _dz);
        x = cols - 30 + JitterX(rows - 13);
        x = BigStr(g, x, rows - 13, "ALT ", white);
        x = BigNum(g, x, rows - 13, alt, DigitsOf(alt), white);
        BigChar(g, x, rows - 13, 'M', white);

        // Искусственный горизонт: короткая линия, наклонённая по крену, и «-+-» в центре.
        int cx = cols / 2, cy = rows / 2;
        float tilt = Math.Clamp(-MathF.Tan(Math.Clamp(_roll, -1.2f, 1.2f)), -3f, 3f);
        for (int i = -8; i <= 8; i++)
        {
            if (i >= -1 && i <= 1) continue;
            int yy = cy + (int)MathF.Round(i * tilt);
            g.Put(cx + i, yy, '-', white);
        }
        g.Put(cx - 1, cy, '-', white);
        g.Put(cx, cy, '+', white);
        g.Put(cx + 1, cy, '-', white);
    }

    /// <summary>Стрелка на пилота относительно курса: 0 — вперёд ↑, 1 — вправо ►, 2 — назад ↓, 3 — влево ◄.</summary>
    private char HomeArrow(float hx, float hy)
    {
        float yaw = _yaw;
        float fwd = hx * MathF.Cos(yaw) + hy * MathF.Sin(yaw);
        float right = hx * MathF.Sin(yaw) - hy * MathF.Cos(yaw);
        float angle = MathF.Atan2(right, fwd);
        int sector = Wrap((int)MathF.Round(angle / (MathF.PI / 2f)), 4);
        return sector switch { 0 => '^', 1 => '►', 2 => 'v', _ => '◄' };
    }

    private static int DigitsOf(int v)
    {
        int n = 1;
        v = Math.Abs(v);
        while (v >= 10)
        {
            v /= 10;
            n++;
        }
        return n;
    }

    // Стрелки вверх/вниз, которых нет в PixelFont: 5×7, по той же схеме, что и остальной шрифт.
    private static readonly int[] UpArrow = Pack(new[] { "..#..", ".###.", "#####", "..#..", "..#..", "..#..", "..#.." });
    private static readonly int[] DownArrow = Pack(new[] { "..#..", "..#..", "..#..", "#####", ".###.", "..#..", "..#.." });

    /// <summary>Упаковка 5×7 в 3×3 клетки: индекс cy*3+cx, бит dy*2+dx.</summary>
    private static int[] Pack(string[] rows)
    {
        var bits = new int[9];
        for (int r = 0; r < 7; r++)
        {
            for (int c = 0; c < 5; c++)
            {
                if (rows[r][c] != '#') continue;
                int cx = c / 2, dx = c % 2, cy = r / 3, dy = r % 3;
                bits[cy * 3 + cx] |= 1 << (dy * 2 + dx);
            }
        }
        return bits;
    }

    /// <summary>Один крупный символ 3×3 клетки (без фона: Bg соседних клеток не трогаем).</summary>
    private static void BigChar(CharGrid g, int x, int y, char ch, Rgba c)
    {
        int[] bits = ch switch
        {
            '^' => UpArrow,
            'v' => DownArrow,
            _ => PixelFont.Bits(ch),
        };
        for (int cy = 0; cy < PixelFont.CellsH; cy++)
        {
            for (int cx = 0; cx < PixelFont.CellsW; cx++)
            {
                int b = bits[cy * PixelFont.CellsW + cx];
                g.Put(x + cx, y + cy, b == 0 ? ' ' : GlyphSet.Sextant(b), c);
            }
        }
    }

    private static int BigStr(CharGrid g, int x, int y, string s, Rgba c)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != ' ') BigChar(g, x, y, s[i], c);
            x += PixelFont.Advance;
        }
        return x;
    }

    private static int BigNum(CharGrid g, int x, int y, int v, int digits, Rgba c)
    {
        int lim = 1;
        for (int i = 0; i < digits; i++) lim *= 10;
        v = Math.Clamp(v, 0, lim - 1);
        for (int d = digits - 1; d >= 0; d--)
        {
            BigChar(g, x + d * PixelFont.Advance, y, (char)('0' + v % 10), c);
            v /= 10;
        }
        return x + digits * PixelFont.Advance;
    }

    /// <summary>Значение с одним знаком после точки: «15.2».</summary>
    private static int BigDec(CharGrid g, int x, int y, float v, Rgba c)
    {
        int t = (int)MathF.Round(Math.Max(0f, v) * 10f);
        int ip = t / 10;
        x = BigNum(g, x, y, ip, DigitsOf(ip), c);
        BigChar(g, x, y, '.', c);
        x += PixelFont.Advance;
        BigChar(g, x, y, (char)('0' + t % 10), c);
        return x + PixelFont.Advance;
    }
}
