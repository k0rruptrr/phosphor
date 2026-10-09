using System.Globalization;
using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// «Организм»: ресурсы ПК как живое существо. Центр — ядро PHOSPHOR (радиальный градиент, дрожащая мембрана,
/// органеллы, ореол от GPU, пульс в такт музыки или дыхание). По кольцу — клетки ядер CPU: размер и яркость ∝ нагрузке.
/// Отростки — изогнутые кривые, по ним бегут капли и волны. Справа — секции в рамках: сеть, мониторы, усталость,
/// CPU/GPU спарклайны, кардиограмма диска. Данные: "system", "phosphor", "clock". Нет "system" — организм «спит».
/// </summary>
public sealed class OrganismScene : IScene, IDataScene, IAudioReactive
{
    public string Id => "organism";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] Keys = { "system", "phosphor", "clock" };
    public IReadOnlyList<string> DataKeys => Keys;

    /// <summary>Сколько клеток ядер сейчас нарисовано (0 без датчиков). Для тестов.</summary>
    public int CoreCellCount => _hasSys ? _nCores : 0;

    /// <summary>Координаты центра клетки ядра k в сетке сцены (для тестов). (-1,-1) — такой клетки нет.</summary>
    public (int X, int Y) CoreCellGridPos(int k)
    {
        int n = CoreCellCount;
        if (k < 0 || k >= n) return (-1, -1);
        Geometry(out float cx, out float cy, out float rr, out _);
        CellAt(k, n, cx, cy, rr, out float px, out float py, out _, out _, out _);
        return (BodyLeft + (int)px, BodyTop + (int)(py / 2f));
    }

    private const int BodyLeft = 2;   // отступы от краёв сетки: 2 колонки по бокам, 1 строка сверху и снизу
    private const int BodyTop = 4;
    private const int MaxCores = 64;
    private const int LabelLimit = 16;   // подписи c0..cN — только при таком числе ядер и меньше
    private const int History = 60;      // спарклайны: 60 с, одна точка в секунду
    private const int DiskHistory = 256; // кардиограмма: 2 Гц, рисуется по ширине
    private const int ParticleCount = 48;
    private const int OrganelleCount = 5;
    private const int BubbleCount = 6;
    private const int SleepCells = 8;    // серые клетки-заглушки, пока датчиков нет
    private const int SecNet = 0, SecMon = 1, SecFat = 2, SecCpu = 3, SecGpu = 4, SecDisk = 5;
    private const float Mb = 1048576f;
    private const string SleepText = "ДАТЧИКИ НЕДОСТУПНЫ";

    private static readonly string[] CoreLabels = BuildLabels();

    private static string[] BuildLabels()
    {
        var a = new string[MaxCores];
        for (int i = 0; i < a.Length; i++) a[i] = "c" + i.ToString(Inv);
        return a;
    }

    // --- раскладка (Init) ---
    private Random _rng = new(0);
    private int _cols = 1, _rows = 1;
    private int _bodyCols, _bodyRows, _memTop, _panelX, _panelW;
    private readonly bool[] _secOn = new bool[6];
    private readonly int[] _secY = new int[6];
    private readonly int[] _secH = new int[6];
    private RetroCanvas _canvas = null!;
    private CharGrid _bodyGrid = null!;
    private Pad _memPad = null!, _cpuPad = null!, _gpuPad = null!, _diskPad = null!;

    // --- данные ПК (цели приходят 2 Гц, визуал — сглаженные значения) ---
    private bool _hasSys;
    private int _nCores;
    private readonly float[] _coreTgt = new float[MaxCores];
    private readonly float[] _coreVal = new float[MaxCores];
    private float _cpuTgt, _cpuVal, _gpuTgt, _gpuVal, _memTgt, _memVal;
    private float _netDownTgt, _netDownVal, _netUpTgt, _netUpVal;
    private bool _gpuKnown;
    private readonly float[] _cpuHist = new float[History];
    private readonly float[] _gpuHist = new float[History];
    private int _histHead;
    private float _histAcc;
    private readonly float[] _diskHist = new float[DiskHistory];
    private int _diskHead;
    private float _phaseDown, _phaseUp;
    private string _hostLine = "", _memText = "ПАМЯТЬ --", _cpuTxt = "CPU --", _gpuTxt = "GPU --";
    private string _netDownTxt = "↓ -- МБ/С", _netUpTxt = "↑ -- МБ/С", _clockTxt = "--:--:--";

    // --- самочувствие PHOSPHOR ---
    private bool _hasPhos;
    private float _bpm, _fatigueTgt, _fatigueVal, _breathTgt, _breathVal, _nightTgt, _nightVal;
    private string _bpmTxt = "♥ ПОКОЙ", _fatTxt = "--%", _fpsTxt = "ЯДРО";
    private int _monCount;
    private readonly string[] _mon = new string[3];

    // --- звук и время ---
    private float _time, _beatPulse;
    private bool _waveOn;
    private float _waveT;

    // --- частицы сети: тип 0 = ↓ входящие (к ядру), 1 = ↑ исходящие (к краю) ---
    private readonly bool[] _pAlive = new bool[ParticleCount];
    private readonly float[] _pS = new float[ParticleCount];
    private readonly int[] _pT = new int[ParticleCount];
    private readonly float[] _spawnAcc = new float[2];

    // --- пузырьки в сосуде памяти (в пикселях сосуда) ---
    private readonly float[] _bx = new float[BubbleCount];
    private readonly float[] _by = new float[BubbleCount];

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = Math.Max(1, cols);
        _rows = Math.Max(1, rows);
        _rng = rng;
        _memTop = _rows - 5;
        _bodyCols = Math.Max(1, _cols * 6 / 10 - BodyLeft);
        _bodyRows = Math.Max(1, _memTop - BodyTop - 1);
        _panelX = BodyLeft + _bodyCols + 2;
        _panelW = Math.Max(1, _cols - 2 - _panelX);
        LayoutPanel();
        _canvas = new RetroCanvas(_bodyCols, _bodyRows, CanvasMode.HalfBlock);
        _bodyGrid = new CharGrid(_bodyCols, _bodyRows);
        _memPad = new Pad(_bodyCols - 2, 3);
        _cpuPad = new Pad(_panelW - 2, _secH[SecCpu] - 2);
        _gpuPad = new Pad(_panelW - 2, _secH[SecGpu] - 2);
        _diskPad = new Pad(_panelW - 2, _secH[SecDisk] - 2);
        ResetState();
        for (int b = 0; b < BubbleCount; b++)
        {
            _bx[b] = rng.NextSingle() * _memPad.W;
            _by[b] = rng.NextSingle() * _memPad.H;
        }
    }

    /// <summary>Секции панели в рамках, сверху вниз. Мало места — отбрасываются по приоритету, лишнее — графикам.</summary>
    private void LayoutPanel()
    {
        int[] minH = { 6, 5, 3, 5, 5, 5 };                       // NET, MON, FAT, CPU, GPU, DISK
        int[] dropOrder = { SecMon, SecDisk, SecFat, SecGpu, SecCpu, SecNet };
        int avail = (_rows - 2) - BodyTop + 1;                    // строки BodyTop..R-2
        int sum = 0;
        for (int i = 0; i < 6; i++) { _secOn[i] = true; _secH[i] = minH[i]; sum += minH[i]; }
        int d = 0;
        while (sum > avail && d < dropOrder.Length)
        {
            int s = dropOrder[d++];
            _secOn[s] = false;
            sum -= minH[s];
        }
        int extra = avail - sum;
        int[] grow = { SecCpu, SecGpu, SecDisk };
        bool any = true;
        while (extra > 0 && any)
        {
            any = false;
            foreach (int g in grow)
            {
                if (!_secOn[g] || extra <= 0) continue;
                _secH[g]++;
                extra--;
                any = true;
            }
        }
        int y = BodyTop;
        int[] order = { SecNet, SecMon, SecFat, SecCpu, SecGpu, SecDisk };
        foreach (int s in order)
        {
            _secY[s] = y;
            if (_secOn[s]) y += _secH[s];
            else _secH[s] = 0;
        }
    }

    private void ResetState()
    {
        Array.Clear(_coreTgt);
        Array.Clear(_coreVal);
        _cpuTgt = _cpuVal = _gpuTgt = _gpuVal = _memTgt = _memVal = 0f;
        _netDownTgt = _netDownVal = _netUpTgt = _netUpVal = 0f;
        _gpuKnown = false;
        Array.Clear(_cpuHist);
        Array.Clear(_gpuHist);
        _histHead = 0;
        _histAcc = 0f;
        Array.Clear(_diskHist);
        _diskHead = 0;
        _phaseDown = _phaseUp = 0f;
        _hostLine = "";
        _memText = "ПАМЯТЬ --";
        _cpuTxt = "CPU --";
        _gpuTxt = "GPU --";
        _netDownTxt = "↓ -- МБ/С";
        _netUpTxt = "↑ -- МБ/С";
        _clockTxt = "--:--:--";
        _hasSys = false;
        _nCores = 0;
        _hasPhos = false;
        _bpm = 0f;
        _fatigueTgt = _fatigueVal = _breathTgt = _breathVal = _nightTgt = _nightVal = 0f;
        _bpmTxt = "♥ ПОКОЙ";
        _fatTxt = "--%";
        _fpsTxt = "ЯДРО";
        _monCount = 0;
        _time = 0f;
        _beatPulse = 0f;
        _waveOn = false;
        _waveT = 0f;
        Array.Clear(_pAlive);
        Array.Clear(_pS);
        Array.Clear(_pT);
        Array.Clear(_spawnAcc);
        Array.Clear(_bx);
        Array.Clear(_by);
    }

    public void OnData(string key, DataSnapshot snapshot)
    {
        if (key == "system" && snapshot is SystemSnapshot s) ApplySystem(s);
        else if (key == "phosphor" && snapshot is PhosphorSnapshot p) ApplyPhosphor(p);
        else if (key == "clock" && snapshot is ClockSnapshot c) _clockTxt = c.Local.ToString("HH:mm:ss", Inv);
    }

    // Строки формируются здесь (2 Гц и раз в секунду), а не в Update/Draw — там аллокаций нет.
    private void ApplySystem(SystemSnapshot s)
    {
        bool first = !_hasSys;
        _hasSys = true;
        int n = Math.Min(s.CorePct.Length, MaxCores);
        _nCores = n;
        for (int i = 0; i < n; i++) _coreTgt[i] = Unit(s.CorePct[i] / 100f);

        _cpuTgt = Unit(s.CpuPct / 100f);
        _cpuTxt = FormattableString.Invariant($"CPU {s.CpuPct:0}%");

        _gpuKnown = !float.IsNaN(s.GpuPct);
        _gpuTgt = _gpuKnown ? Unit(s.GpuPct / 100f) : 0f;
        _gpuTxt = _gpuKnown ? FormattableString.Invariant($"GPU {s.GpuPct:0}% {Clean(s.GpuName)}") : "GPU --";

        _memTgt = s.RamTotalGb > 0 ? Unit((float)(s.RamUsedGb / s.RamTotalGb)) : 0f;
        _memText = FormattableString.Invariant($"ПАМЯТЬ {s.RamUsedGb:0.0}/{s.RamTotalGb:0.#} ГБ");

        _netDownTgt = RateLevel(s.NetDownBps);
        _netUpTgt = RateLevel(s.NetUpBps);
        _netDownTxt = FormattableString.Invariant($"↓ {s.NetDownBps / Mb:0.0} МБ/С");
        _netUpTxt = FormattableString.Invariant($"↑ {s.NetUpBps / Mb:0.0} МБ/С");

        float diskLevel = RateLevel(s.DiskReadBps + s.DiskWriteBps);
        if (first)
        {
            // До первого снимка «было так же»: история и сглаженные значения сразу на текущих числах.
            Array.Fill(_diskHist, diskLevel);
            Array.Fill(_cpuHist, _cpuTgt);
            Array.Fill(_gpuHist, _gpuKnown ? _gpuTgt : 0f);
            _cpuVal = _cpuTgt;
            _gpuVal = _gpuTgt;
            _memVal = _memTgt;
            _netDownVal = _netDownTgt;
            _netUpVal = _netUpTgt;
            for (int i = 0; i < n; i++) _coreVal[i] = _coreTgt[i];
        }
        else
        {
            _diskHist[_diskHead] = diskLevel;
            _diskHead = (_diskHead + 1) % DiskHistory;
        }

        var up = s.Uptime;
        string upTxt = up.Days > 0
            ? FormattableString.Invariant($"{up.Days}Д {up.Hours:00}:{up.Minutes:00}")
            : FormattableString.Invariant($"{up.Hours:00}:{up.Minutes:00}");
        _hostLine = Clean(s.Host) + " · " + Clean(s.Os) + " · АПТАЙМ " + upTxt;
    }

    private void ApplyPhosphor(PhosphorSnapshot p)
    {
        _hasPhos = true;
        _bpm = p.Bpm > 0f ? p.Bpm : 0f;
        _bpmTxt = _bpm > 0f ? FormattableString.Invariant($"♥ {_bpm:0} BPM") : "♥ ПОКОЙ";
        _fpsTxt = FormattableString.Invariant($"ЯДРО · {p.Fps:0} FPS");
        _fatigueTgt = Unit(p.Fatigue);
        _fatTxt = FormattableString.Invariant($"{_fatigueTgt * 100f:0}%");
        _breathTgt = Unit(p.Breath);
        _nightTgt = p.Night ? 1f : 0f;
        _monCount = Math.Min(p.Scenes.Count, _mon.Length);
        for (int i = 0; i < _monCount; i++)
        {
            string preset = i < p.Presets.Count ? p.Presets[i] : "-";
            _mon[i] = p.Scenes[i] + " · " + preset;
        }
    }

    public void OnAudio(in AudioFrame audio)
    {
        _beatPulse = audio.Active ? audio.BeatPulse : 0f;
        if (audio.Active && audio.Beat)
        {
            _waveOn = true;   // бит → волна по отросткам от ядра к клеткам
            _waveT = 0f;
        }
    }

    public void Update(float dt)
    {
        if (dt <= 0f) return;
        _time += dt;
        float k = 1f - MathF.Exp(-dt * 6f);   // экспоненциальное сглаживание ступенек 2 Гц
        for (int i = 0; i < _nCores; i++) _coreVal[i] += (_coreTgt[i] - _coreVal[i]) * k;
        _cpuVal += (_cpuTgt - _cpuVal) * k;
        _gpuVal += (_gpuTgt - _gpuVal) * k;
        _memVal += (_memTgt - _memVal) * k;
        _netDownVal += (_netDownTgt - _netDownVal) * k;
        _netUpVal += (_netUpTgt - _netUpVal) * k;
        _fatigueVal += (_fatigueTgt - _fatigueVal) * k;
        _breathVal += (_breathTgt - _breathVal) * k;
        _nightVal += (_nightTgt - _nightVal) * k;

        _histAcc += dt;
        while (_histAcc >= 1f)
        {
            _histAcc -= 1f;
            _cpuHist[_histHead] = _cpuVal;
            _gpuHist[_histHead] = _gpuKnown ? _gpuVal : 0f;
            _histHead = (_histHead + 1) % History;
        }

        _phaseDown = (_phaseDown + dt * (2f + 20f * _netDownVal)) % 1000f;
        _phaseUp = (_phaseUp + dt * (2f + 20f * _netUpVal)) % 1000f;
        StepParticles(dt);

        for (int b = 0; b < BubbleCount; b++)
        {
            _by[b] -= dt * 4f;
            if (_by[b] < 0f)
            {
                _by[b] = _memPad.H - 1f;
                _bx[b] = _rng.NextSingle() * _memPad.W;
            }
        }

        if (_waveOn)
        {
            _waveT += dt * 0.8f;
            if (_waveT >= 1f) _waveOn = false;
        }
    }

    private void StepParticles(float dt)
    {
        for (int t = 0; t < 2; t++)
        {
            if (!_hasSys)
            {
                _spawnAcc[t] = 0f;
                continue;
            }
            float level = t == 0 ? _netDownVal : _netUpVal;
            _spawnAcc[t] += dt * (0.5f + 8f * level);
            while (_spawnAcc[t] >= 1f)
            {
                _spawnAcc[t] -= 1f;
                int j = FreeSlot();
                if (j < 0) break;
                _pAlive[j] = true;
                _pT[j] = t;
                _pS[j] = t == 0 ? 1f : 0f;
            }
        }
        for (int j = 0; j < ParticleCount; j++)
        {
            if (!_pAlive[j]) continue;
            int t = _pT[j];
            float level = t == 0 ? _netDownVal : _netUpVal;
            _pS[j] += (t == 0 ? -1f : 1f) * (0.2f + 0.6f * level) * dt;
            if (_pS[j] < 0f || _pS[j] > 1f) _pAlive[j] = false;
        }
    }

    private int FreeSlot()
    {
        for (int j = 0; j < ParticleCount; j++) if (!_pAlive[j]) return j;
        return -1;
    }

    public void Draw(CharGrid grid)
    {
        if (_canvas is null) return;
        grid.Clear(Rgba.Black);
        _canvas.Clear(Rgba.Black);
        DrawBody();
        _canvas.Present(_bodyGrid);
        DrawBodyLabels();
        CopyBody(grid);
        DrawHeader(grid);
        DrawPanel(grid);
        DrawMemory(grid);
    }

    // ---------- геометрия и тело (холст) ----------

    /// <summary>Центр ядра и кольца в пикселях холста (1 пиксель = 1 колонка × пол-строки).</summary>
    private void Geometry(out float cx, out float cy, out float rr, out float rCore)
    {
        float w = _bodyCols, h = _bodyRows * 2f;
        cx = w * 0.5f;
        cy = h * 0.5f;
        rr = MathF.Max(3f, MathF.Min(w * 0.5f - 9f, h * 0.5f - 9f));   // запас под клетки и подписи у края
        rCore = MathF.Max(2f, MathF.Min(w, h) * 0.18f);
    }

    private float BreathSig() => 2f * (_hasPhos ? _breathVal : 0.5f + 0.5f * MathF.Sin(_time * 0.7f)) - 1f;

    /// <summary>Масштаб радиуса ядра сейчас: ±8–12% (удар в такт, дыхание мягче). Общий для отрисовки и геометрии отростков.</summary>
    private float CoreScale(bool sleep) => 1f + 0.12f * (sleep ? 0f : BeatK()) + (sleep ? 0f : 0.08f * BreathSig());

    private void DrawBody()
    {
        Geometry(out float cx, out float cy, out float rr, out float rCore);
        bool sleep = !_hasSys;
        float beat = sleep ? 0f : BeatK();
        float breathSig = BreathSig();
        float bright = sleep
            ? 0.18f + 0.12f * (0.5f + 0.5f * MathF.Sin(_time * 1.2f))
            : Math.Clamp(0.55f + 0.15f * breathSig + 0.35f * beat, 0f, 1f);
        float cpuAvg = sleep ? 0f : _cpuVal;
        float rCoreP = rCore * CoreScale(sleep);
        float glow = (!sleep && _gpuKnown) ? (0.15f + 0.5f * _gpuVal) * (0.8f + 0.4f * bright) : 0f;

        Rgba grey = P(0x3A3A3A, 0x4A3E2C);
        Rgba dimTendril = P(0x1F5A42, 0x6B4A10);
        Rgba waveCol = P(0x5CF2A0, 0xFFB000);
        Rgba drop = P(0x9CFFB8, 0xFFD27A);
        float waveT = _waveOn ? _waveT : -1f;

        // клетки ядер CPU с отростками (отростки — под клетками)
        int n = sleep ? SleepCells : _nCores;
        for (int k = 0; k < n; k++)
        {
            CellAt(k, n, cx, cy, rr, out float px, out float py, out float dx, out float dy, out float spacing);
            float load = sleep ? 0f : _coreVal[k];
            float rc = CellRadius(load, spacing);
            CellTendrilEnds(k, cx, cy, rCoreP, px, py, dx, dy, rc,
                out float x0, out float y0, out float x1, out float y1, out float sway);
            float dropF = (!sleep && load > 0.02f) ? Frac(_time * 0.3f + k * 0.13f) : -1f;
            Tendril(x0, y0, x1, y1, sway, sleep ? grey : dimTendril, waveT, waveCol, dropF, drop);

            Rgba center, edge, rim;
            bool rimOn;
            if (sleep)
            {
                center = grey; edge = Rgba.Lerp(Rgba.Black, grey, 0.5f); rim = grey; rimOn = false;
            }
            else
            {
                center = CellCenter(load);
                edge = Rgba.Lerp(Rgba.Black, center, 0.35f);
                rim = load > 0.85f ? Rgba.Lerp(center, P(0xFF8C1A, 0xFFA000), 0.5f) : Rgba.Lerp(center, Rgba.White, 0.3f);
                rimOn = load > 0.05f;
            }
            float amp = sleep ? 0f : 0.02f + 0.12f * load;   // дрожь мембраны ∝ нагрузке
            Blob(px, py, rc, amp, _time + k * 0.9f, center, edge, rim, rimOn, 0f, Rgba.Black);
        }

        DrawNet(cx, cy, rCore, _bodyCols, waveCol);

        // ядро PHOSPHOR: градиент, дрожащая мембрана, яркий ободок, ореол от GPU
        Rgba coreC = Rgba.Lerp(P(0x1C4A33, 0x5A3E10), P(0x9CFFB8, 0xFFD27A), bright);
        Rgba coreE = Rgba.Lerp(Rgba.Black, coreC, 0.35f);
        Rgba rimCore = P(0xB8FFCB, 0xFFE6A0).Scale(0.85f + 0.15f * bright);
        float coreAmp = sleep ? 0.02f : 0.05f + 0.08f * cpuAvg;
        Blob(cx, cy, rCoreP, coreAmp, _time, coreC, coreE, rimCore, !sleep, glow, P(0x2BD98A, 0xFF9A00));

        if (!sleep)
        {
            for (int i = 0; i < OrganelleCount; i++)
            {
                float a = _time * (0.15f + 0.04f * i) + i * 1.3f;
                float rad = rCoreP * 0.45f * (0.7f + 0.3f * MathF.Sin(_time * 0.4f + i));
                float ox = cx + MathF.Cos(a) * rad, oy = cy + MathF.Sin(a) * rad;
                float orr = rCoreP * (0.10f + 0.05f * (i % 3));
                Rgba oc = (i % 2 == 0) ? Rgba.Lerp(coreC, Rgba.Black, 0.45f) : Rgba.Lerp(coreC, Rgba.White, 0.35f);
                Disc(ox, oy, orr, oc);
            }
        }
    }

    private Rgba CellCenter(float load) => Rgba.Lerp(P(0x1C4A33, 0x5A3E10), P(0x9CFFB8, 0xFFD27A), load);

    private void DrawNet(float cx, float cy, float rCore, float w, Rgba waveCol)
    {
        Rgba dim = P(0x1F5A42, 0x6B4A10);
        for (int t = 0; t < 2; t++)
        {
            NetEnds(t, cx, cy, rCore, w, out float x0, out float y0, out float x1, out float y1);
            float sway = 0.10f * MathF.Sin(_time * 0.6f + t * 2f);
            Rgba hot = t == 0 ? P(0x40E0D0, 0xFFC040) : P(0xFF8C1A, 0xFFA000);
            float dropF = Frac(_time * 0.4f + t * 0.5f);
            Tendril(x0, y0, x1, y1, sway, dim, _waveOn ? _waveT : -1f, waveCol, dropF, hot);
        }
        for (int j = 0; j < ParticleCount; j++)
        {
            if (!_pAlive[j]) continue;
            int t = _pT[j];
            NetEnds(t, cx, cy, rCore, w, out float x0, out float y0, out float x1, out float y1);
            float sway = 0.10f * MathF.Sin(_time * 0.6f + t * 2f);
            Control(x0, y0, x1, y1, sway, out float qx, out float qy);
            Quad(_pS[j], x0, y0, qx, qy, x1, y1, out float x, out float y);
            Rgba c = t == 0 ? P(0x40E0D0, 0xFFC040) : P(0xFF8C1A, 0xFFA000);
            int ix = (int)MathF.Round(x), iy = (int)MathF.Round(y);
            _canvas.Set(ix, iy, c);
            _canvas.Set(ix, iy + 1, c);
        }
    }

    private static void NetEnds(int t, float cx, float cy, float rCore, float w,
        out float x0, out float y0, out float x1, out float y1)
    {
        float ang = t == 0 ? -0.3f : 0.3f;
        x0 = cx + MathF.Cos(ang) * rCore;
        y0 = cy + MathF.Sin(ang) * rCore;
        x1 = w - 1f;
        y1 = cy + MathF.Tan(ang) * (x1 - cx);
    }

    /// <summary>k-е ядро: положение на кольце (два кольца при &gt;16 ядрах) и размах дуги.</summary>
    private static void CellAt(int k, int n, float cx, float cy, float rr,
        out float px, out float py, out float dx, out float dy, out float spacing)
    {
        int outer = n > LabelLimit ? (n + 1) / 2 : n;
        bool inner = k >= outer;
        int j = inner ? k - outer : k;
        int count = inner ? n - outer : outer;
        float radius = inner ? rr * 0.62f : rr;
        float ang = -MathF.PI / 2f + 2f * MathF.PI * j / count + (inner ? MathF.PI / count : 0f);
        dx = MathF.Cos(ang);
        dy = MathF.Sin(ang);
        px = cx + dx * radius;
        py = cy + dy * radius;
        spacing = 2f * MathF.PI * radius / count;
    }

    /// <summary>Тело клетки/ядра: радиальный градиент, дрожащий край (радиус = R + шум(угол, время)), ободок, ореол.</summary>
    private void Blob(float cx, float cy, float r, float amp, float tt, Rgba center, Rgba edge, Rgba rim,
        bool rimOn, float glowAmt, Rgba glowCol)
    {
        float gr = glowAmt > 0f ? r * 1.6f : r * 1.25f;
        int R = (int)MathF.Ceiling(gr + amp * r + 1f);
        int x0 = (int)MathF.Round(cx), y0 = (int)MathF.Round(cy);
        for (int y = y0 - R; y <= y0 + R; y++)
            for (int x = x0 - R; x <= x0 + R; x++)
            {
                float dx = x - cx, dy = y - cy;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float rb = r * (1f + amp * Wobble(MathF.Atan2(dy, dx), tt));
                if (d <= rb - 0.8f)
                {
                    float t = d / MathF.Max(rb, 1e-3f);
                    _canvas.Set(x, y, Rgba.Lerp(center, edge, t * t));
                }
                else if (d <= rb + 0.8f)
                {
                    if (rimOn) _canvas.Set(x, y, rim);
                }
                else if (glowAmt > 0f && d < gr)
                {
                    float a = glowAmt * (1f - (d - rb) / MathF.Max(gr - rb, 1e-3f));
                    // ореол прибавляется к уже нарисованному (отростки внутри него остаются видны)
                    if (a > 0f) AddPx(x, y, Rgba.Lerp(Rgba.Black, glowCol, a * a));
                }
            }
    }

    /// <summary>Аддитивная запись пикселя: прибавляет цвет к уже нарисованному (без затирания чёрным).</summary>
    private void AddPx(int x, int y, Rgba c)
    {
        var o = _canvas.Get(x, y);
        _canvas.Set(x, y, new Rgba((byte)Math.Min(255, o.R + c.R), (byte)Math.Min(255, o.G + c.G), (byte)Math.Min(255, o.B + c.B)));
    }

    private static float Wobble(float th, float tt) =>
        0.5f * MathF.Sin(3f * th + 1.1f * tt) + 0.3f * MathF.Sin(5f * th - 1.7f * tt) + 0.2f * MathF.Sin(9f * th + 3.1f * tt);

    private void Disc(float cx, float cy, float r, Rgba c)
    {
        int R = (int)MathF.Ceiling(r);
        float r2 = r * r;
        int x0 = (int)MathF.Round(cx), y0 = (int)MathF.Round(cy);
        for (int y = y0 - R; y <= y0 + R; y++)
            for (int x = x0 - R; x <= x0 + R; x++)
            {
                float dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy <= r2) _canvas.Set(x, y, c);
            }
    }

    /// <summary>Изогнутый отросток: квадратичная Безье, контрольная точка качается; толще у ядра, тоньше к краю.
    /// dropF &lt; 0 — капли нет; hotAt &lt; 0 — волны нет.</summary>
    private void Tendril(float x0, float y0, float x1, float y1, float sway, Rgba dim,
        float hotAt, Rgba hot, float dropF, Rgba drop)
    {
        Control(x0, y0, x1, y1, sway, out float qx, out float qy);
        float len = MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) + 1f;
        int steps = CurveSteps(len);
        for (int i = 0; i <= steps; i++)
        {
            float f = i / (float)steps;
            Quad(f, x0, y0, qx, qy, x1, y1, out float x, out float y);
            int ix = (int)MathF.Round(x), iy = (int)MathF.Round(y);
            Rgba c = hotAt >= 0f && MathF.Abs(f - hotAt) < 0.08f ? hot : dim;
            _canvas.Set(ix, iy, c);
            if (f < 0.4f) _canvas.Set(ix, iy + 1, c);   // толщина 2 у ядра, 1 к краю
        }
        if (dropF >= 0f)
        {
            Quad(dropF, x0, y0, qx, qy, x1, y1, out float x, out float y);
            int ix = (int)MathF.Round(x), iy = (int)MathF.Round(y);
            _canvas.Set(ix, iy, drop);
            _canvas.Set(ix + 1, iy, drop);
            _canvas.Set(ix, iy + 1, drop);
        }
    }

    private static void Control(float x0, float y0, float x1, float y1, float sway, out float qx, out float qy)
    {
        float mx = (x0 + x1) * 0.5f, my = (y0 + y1) * 0.5f;
        float dx = x1 - x0, dy = y1 - y0;
        float len = MathF.Sqrt(dx * dx + dy * dy) + 1f;
        qx = mx - dy / len * sway * len;
        qy = my + dx / len * sway * len;
    }

    /// <summary>Радиус клетки ядра по нагрузке (≤ дольки кольца).</summary>
    private static float CellRadius(float load, float spacing)
    {
        float rMax = MathF.Max(1.5f, MathF.Min(spacing * 0.5f, 6f));
        return 1.5f + (rMax - 1.5f) * load;
    }

    /// <summary>Концы отростка: начало внутри мембраны ядра (ядро рисуется поверх), конец — внутри клетки.</summary>
    private void CellTendrilEnds(int k, float cx, float cy, float rCoreP, float px, float py, float dx, float dy, float rc,
        out float x0, out float y0, out float x1, out float y1, out float sway)
    {
        x0 = cx + dx * rCoreP * 0.7f;
        y0 = cy + dy * rCoreP * 0.7f;
        x1 = px - dx * rc * 0.5f;
        y1 = py - dy * rc * 0.5f;
        sway = 0.12f * MathF.Sin(_time * 0.5f + k * 1.7f);
    }

    /// <summary>Число шагов выборки кривой: плотно, чтобы соседние пиксели растра шли впритык.</summary>
    private static int CurveSteps(float len) => Math.Max(8, (int)(4f * len));

    /// <summary>Отросток клетки k в пикселях холста — для тестов непрерывности и концов.</summary>
    public TendrilProbe? CellTendril(int k)
    {
        int n = CoreCellCount;
        if (k < 0 || k >= n) return null;
        Geometry(out float cx, out float cy, out float rr, out float rCore);
        float rCoreP = rCore * CoreScale(false);
        CellAt(k, n, cx, cy, rr, out float px, out float py, out float dx, out float dy, out float spacing);
        float rc = CellRadius(_coreVal[k], spacing);
        CellTendrilEnds(k, cx, cy, rCoreP, px, py, dx, dy, rc, out float x0, out float y0, out float x1, out float y1, out float sway);
        Control(x0, y0, x1, y1, sway, out float qx, out float qy);
        float len = MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) + 1f;
        int steps = CurveSteps(len);
        var pts = new List<(int X, int Y)>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            Quad(i / (float)steps, x0, y0, qx, qy, x1, y1, out float x, out float y);
            pts.Add(((int)MathF.Round(x), (int)MathF.Round(y)));
        }
        return new TendrilProbe(pts, cx, cy, rCoreP, px, py, rc, BodyLeft, BodyTop);
    }

    /// <summary>Пиксели отростка (холст), центр и радиус ядра (базовый, без пульса), центр и радиус клетки,
    /// и origin тела в сетке сцены: пиксель холста (x, y) в сетке — (BodyX + x, BodyY + y / 2), верхняя половина — Fg, нижняя — Bg.</summary>
    public sealed record TendrilProbe(IReadOnlyList<(int X, int Y)> Points,
        float CoreX, float CoreY, float CoreR, float CellX, float CellY, float CellR, int BodyX, int BodyY);

    private static void Quad(float f, float x0, float y0, float qx, float qy, float x1, float y1, out float x, out float y)
    {
        float a = 1f - f;
        x = a * a * x0 + 2f * a * f * qx + f * f * x1;
        y = a * a * y0 + 2f * a * f * qy + f * f * y1;
    }

    private void DrawBodyLabels()
    {
        if (!_hasSys)
        {
            _bodyGrid.Text(Math.Max(0, (_bodyCols - SleepText.Length) / 2), _bodyRows - 1, SleepText, P(0x4A8A66, 0x8A6420));
            return;
        }
        Rgba dim = P(0x4A8A66, 0x8A6420);
        if (_nCores <= LabelLimit)
            for (int k = 0; k < _nCores; k++)
            {
                // подпись снаружи клетки: по радиусу от ядра, за клеткой + её радиус + 1 колонка
                Geometry(out float cx, out float cy, out float rr, out _);
                CellAt(k, _nCores, cx, cy, rr, out float px, out float py, out float dx, out float dy, out float spacing);
                float rc = CellRadius(_coreVal[k], spacing);
                // точка на расстоянии rc+2 пикселя от центра клетки в её направлении от ядра
                int gx = (int)(px + dx * (rc + 2f));
                int gy = Math.Clamp((int)((py + dy * (rc + 2f)) / 2f), 0, _bodyRows - 1);
                string lab = CoreLabels[k];
                int lx = MathF.Abs(dx) < 0.3f ? gx - lab.Length / 2 : dx > 0f ? gx : gx - lab.Length;
                _bodyGrid.Text(lx, gy, lab, dim);
            }
        // подпись под ядром: ЯДРО · FPS
        Geometry(out float ccx, out float ccy, out _, out float rCore);
        int ty = (int)(ccy / 2f) + (int)MathF.Ceiling(rCore / 2f) + 1;
        _bodyGrid.Text((int)ccx - _fpsTxt.Length / 2, ty, _fpsTxt, P(0xB8FFCB, 0xFFD27A));
    }

    private void CopyBody(CharGrid grid)
    {
        for (int y = 0; y < _bodyGrid.Rows; y++)
            for (int x = 0; x < _bodyGrid.Cols; x++)
            {
                var cell = _bodyGrid.Cells[y * _bodyGrid.Cols + x];
                grid.Put(BodyLeft + x, BodyTop + y, cell.Ch, cell.Fg, cell.Bg);
            }
    }

    // ---------- шапка, панель, низ ----------

    private void DrawHeader(CharGrid g)
    {
        Rgba text = P(0xB8FFCB, 0xFFD27A), dim = P(0x4A8A66, 0x8A6420);
        Txt(g, BodyLeft, 1, "PHOSPHOR · ОРГАНИЗМ", text, _cols);
        Txt(g, _cols - 2 - _clockTxt.Length, 1, _clockTxt, text, _cols);
        int bx = _cols - 2 - _bpmTxt.Length;
        float pulse = _hasSys ? BeatK() : 0f;
        Txt(g, bx, 2, _bpmTxt, Rgba.Lerp(dim, P(0xFF8C1A, 0xFFA000), pulse), _cols);
        Txt(g, BodyLeft, 2, _hostLine, dim, bx - 2);
    }

    private void DrawPanel(CharGrid g)
    {
        Rgba frame = P(0x1F5A42, 0x6B4A10), dim = P(0x4A8A66, 0x8A6420), text = P(0xB8FFCB, 0xFFD27A);
        Rgba accent = P(0xFF8C1A, 0xFFA000), turq = P(0x40E0D0, 0xFFC040), bar = P(0x9CFFB8, 0xFFD27A);
        int x = _panelX, w = _panelW, mx = _panelX + _panelW - 1;
        int iw = w - 2;

        if (_secOn[SecNet])
        {
            int y = _secY[SecNet];
            Box(g, x, y, w, _secH[SecNet], "СЕТЬ", frame, dim);
            Txt(g, x + 1, y + 1, _netDownTxt, turq, mx);
            Strip(g, x + 1, y + 2, iw, _phaseDown, turq, mx, _hasSys);
            Txt(g, x + 1, y + 3, _netUpTxt, accent, mx);
            Strip(g, x + 1, y + 4, iw, _phaseUp, accent, mx, _hasSys);
        }
        if (_secOn[SecMon])
        {
            int y = _secY[SecMon], inner = _secH[SecMon] - 2;
            Box(g, x, y, w, _secH[SecMon], "МОНИТОРЫ", frame, dim);
            if (_monCount == 0) Txt(g, x + 1, y + 1, "-", dim, mx);
            for (int i = 0; i < _monCount && i < inner; i++) Txt(g, x + 1, y + 1 + i, _mon[i], text, mx);
        }
        if (_secOn[SecFat])
        {
            int y = _secY[SecFat];
            Box(g, x, y, w, _secH[SecFat], "УСТАЛОСТЬ", frame, dim);
            int filled = (int)MathF.Round(_fatigueVal * 10f);
            for (int i = 0; i < 10; i++) g.Put(x + 1 + i, y + 1, i < filled ? '▓' : '░', i < filled ? accent : dim);
            Txt(g, x + 12, y + 1, _fatTxt, text, mx);
        }
        if (_secOn[SecCpu])
        {
            int y = _secY[SecCpu];
            Box(g, x, y, w, _secH[SecCpu], _cpuTxt, frame, dim);
            _cpuPad.Clear();
            FillBars(_cpuPad, _cpuHist, _histHead);
            _cpuPad.EmitEdge(g, x + 1, y + 1, P(0x1F5A42, 0x6B4A10), bar, Rgba.Black, mx);
        }
        if (_secOn[SecGpu])
        {
            int y = _secY[SecGpu];
            Box(g, x, y, w, _secH[SecGpu], _gpuTxt, frame, dim);
            _gpuPad.Clear();
            FillBars(_gpuPad, _gpuHist, _histHead);
            _gpuPad.EmitEdge(g, x + 1, y + 1, P(0x7A3E0E, 0x6B4A10), accent, Rgba.Black, mx);
        }
        if (_secOn[SecDisk])
        {
            int y = _secY[SecDisk];
            Box(g, x, y, w, _secH[SecDisk], "ДИСК", frame, dim);
            _diskPad.Clear();
            FillTrace(_diskPad, _diskHist, _diskHead);
            _diskPad.Emit(g, x + 1, y + 1, turq, Rgba.Black, mx);
        }
    }

    private void DrawMemory(CharGrid g)
    {
        Rgba text = P(0xB8FFCB, 0xFFD27A), dim = P(0x4A8A66, 0x8A6420);
        int right = BodyLeft + _bodyCols - 1;
        Txt(g, BodyLeft, _memTop, _memText, text, right + 1);
        FillVessel(_memPad);
        for (int y = 1; y <= 3; y++)
        {
            g.Put(BodyLeft, _memTop + y, '│', dim);
            g.Put(right, _memTop + y, '│', dim);
        }
        _memPad.Emit(g, BodyLeft + 1, _memTop + 1, P(0x40E0D0, 0xFFC040), Rgba.Black, right);
    }

    /// <summary>Рамка секции с заголовком в верхней линии. Без аллокаций: заголовок — готовая строка.</summary>
    private static void Box(CharGrid g, int x, int y, int w, int h, string title, Rgba frame, Rgba titleCol)
    {
        if (w < 3 || h < 2) return;
        int mx = x + w - 1;
        g.Put(x, y, '┌', frame);
        for (int i = 1; i < w - 1; i++) g.Put(x + i, y, '─', frame);
        g.Put(mx, y, '┐', frame);
        if (title.Length > 0 && w > title.Length + 6)
        {
            g.Put(x + 2, y, ' ', frame);
            Txt(g, x + 3, y, title, titleCol, mx);
            g.Put(x + 3 + title.Length, y, ' ', frame);
        }
        for (int r = 1; r < h - 1; r++)
        {
            g.Put(x, y + r, '│', frame);
            g.Put(mx, y + r, '│', frame);
        }
        g.Put(x, y + h - 1, '└', frame);
        for (int i = 1; i < w - 1; i++) g.Put(x + i, y + h - 1, '─', frame);
        g.Put(mx, y + h - 1, '┘', frame);
    }

    /// <summary>Бегущая полоска «··•··» — скорость и плотность ∝ трафику (фаза растёт быстрее при большей нагрузке).</summary>
    private static void Strip(CharGrid g, int x, int y, int w, float phase, Rgba c, int maxX, bool live)
    {
        int p = (int)phase;
        for (int i = 0; i < w; i++)
        {
            if (x + i >= maxX) break;
            int k = (i + p) % 5;
            g.Put(x + i, y, live && k == 2 ? '•' : '·', live ? c : Rgba.Black);
        }
    }

    /// <summary>Сосуд памяти: уровень жидкости с лёгкой рябью и пузырьками (дырками в заливке).</summary>
    private void FillVessel(Pad p)
    {
        p.Clear();
        float level = _hasSys ? _memVal : 0f;
        int fill = (int)MathF.Round(level * p.H);
        if (fill <= 0) return;
        for (int x = 0; x < p.W; x++)
        {
            int surf = Surface(p, fill, x);
            for (int y = Math.Max(0, surf); y < p.H; y++) p.Set(x, y, true);
        }
        for (int b = 0; b < BubbleCount; b++)
        {
            int bx = (int)_bx[b], by = (int)_by[b];
            if (by > Surface(p, fill, bx)) p.Set(bx, by, false);
        }
    }

    private int Surface(Pad p, int fill, int x) =>
        p.H - fill + (int)MathF.Round(0.6f * MathF.Sin(x * 0.5f + _time * 2.5f));

    /// <summary>Спарклайн-столбики: пиксель-колонки — история за 60 с, справа — свежее.</summary>
    private static void FillBars(Pad p, float[] hist, int head)
    {
        int n = hist.Length;
        for (int x = 0; x < p.W; x++)
        {
            int age = p.W - 1 - x;
            int idx = (int)((long)age * n / p.W);
            float v = hist[((head - 1 - idx) % n + n) % n];
            int hgt = (int)MathF.Round(Math.Clamp(v, 0f, 1f) * p.H);
            for (int y = p.H - 1; y >= p.H - hgt; y--) p.Set(x, y, true);
            if (hgt > 0) p.SetEdge(x, p.H - hgt);
            else p.SetEdge(x, p.H - 1);   // ровная нулевая линия — тоже кромка
        }
    }

    /// <summary>Кардиограмма: линия по кольцевому буферу (справа — свежее), соединённая по вертикали.</summary>
    private static void FillTrace(Pad p, float[] hist, int head)
    {
        int n = hist.Length, prev = -1;
        for (int x = 0; x < p.W; x++)
        {
            int age = p.W - 1 - x;
            int idx = (int)((long)age * n / p.W);
            float v = hist[((head - 1 - idx) % n + n) % n];
            int y = (int)MathF.Round((1f - Math.Clamp(v, 0f, 1f)) * (p.H - 1));
            int from = prev < 0 ? y : prev;
            for (int yy = Math.Min(from, y); yy <= Math.Max(from, y); yy++) p.Set(x, yy, true);
            prev = y;
        }
    }

    // ---------- утилиты ----------

    /// <summary>Пульс ядра: звуковой BeatPulse или, при известном BPM, синтетический удар в такт.</summary>
    private float BeatK()
    {
        float k = _beatPulse;
        if (_bpm > 0f)
        {
            float period = 60f / _bpm;
            float ph = _time - MathF.Floor(_time / period) * period;
            k = MathF.Max(k, MathF.Exp(-ph / 0.15f));
        }
        return k;
    }

    private Rgba P(uint day, uint night) => Rgba.Lerp(Rgba.Hex(day), Rgba.Hex(night), _nightVal);

    private static float Frac(float v) => v - MathF.Floor(v);

    private static void Txt(CharGrid g, int x, int y, string s, Rgba fg, int maxX)
    {
        for (int i = 0; i < s.Length; i++)
        {
            int xx = x + i;
            if (xx >= maxX) break;
            g.Put(xx, y, s[i], fg);
        }
    }

    private static float Unit(float v) => float.IsNaN(v) ? 0f : MathF.Min(1f, MathF.Max(0f, v));

    private static float RateLevel(double bps) =>
        (float)Math.Clamp(Math.Log10(1.0 + Math.Max(0.0, bps) / 1024.0) / 4.0, 0.0, 1.0);

    private static string Clean(string? s) => GlyphSet.Sanitize(s ?? "").ToUpperInvariant();

    /// <summary>Мини-холст символов: клетка = 2×3 пикселя (секстель). Заполняется в Draw без аллокаций.</summary>
    private sealed class Pad
    {
        public readonly int W, H;
        private readonly int _cellsX, _cellsY;
        private readonly bool[] _on;
        private readonly bool[] _edge;

        public Pad(int cellsX, int cellsY)
        {
            _cellsX = Math.Max(1, cellsX);
            _cellsY = Math.Max(1, cellsY);
            W = _cellsX * 2;
            H = _cellsY * 3;
            _on = new bool[W * H];
            _edge = new bool[W * H];
        }

        public void Clear()
        {
            Array.Clear(_on);
            Array.Clear(_edge);
        }

        public void Set(int x, int y, bool v = true)
        {
            if ((uint)x < (uint)W && (uint)y < (uint)H) _on[y * W + x] = v;
        }

        /// <summary>Пометить пиксель как верхнюю кромку спарклайна (рисуется ярче заливки).</summary>
        public void SetEdge(int x, int y)
        {
            if ((uint)x < (uint)W && (uint)y < (uint)H) _edge[y * W + x] = true;
        }

        /// <summary>Как Emit, но клетки с кромкой — цветом edge, остальные заливки — цветом fill.</summary>
        public void EmitEdge(CharGrid g, int gx, int gy, Rgba fill, Rgba edge, Rgba bg, int maxX)
        {
            for (int cy = 0; cy < _cellsY; cy++)
                for (int cx = 0; cx < _cellsX; cx++)
                {
                    if (gx + cx > maxX) break;
                    int bits = 0;
                    bool hasEdge = false;
                    for (int j = 0; j < 3; j++)
                        for (int i = 0; i < 2; i++)
                        {
                            int p = (cy * 3 + j) * W + cx * 2 + i;
                            if (_on[p]) bits |= 1 << (j * 2 + i);
                            if (_edge[p]) hasEdge = true;
                        }
                    g.Put(gx + cx, gy + cy, bits == 0 ? ' ' : GlyphSet.Sextant(bits), hasEdge ? edge : fill, bg);
                }
        }

        public void Emit(CharGrid g, int gx, int gy, Rgba fg, Rgba bg, int maxX)
        {
            for (int cy = 0; cy < _cellsY; cy++)
                for (int cx = 0; cx < _cellsX; cx++)
                {
                    if (gx + cx > maxX) break;
                    int bits = 0;
                    for (int j = 0; j < 3; j++)
                        for (int i = 0; i < 2; i++)
                            if (_on[(cy * 3 + j) * W + cx * 2 + i]) bits |= 1 << (j * 2 + i);
                    g.Put(gx + cx, gy + cy, bits == 0 ? ' ' : GlyphSet.Sextant(bits), fg, bg);
                }
        }
    }
}
