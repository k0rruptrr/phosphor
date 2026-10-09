using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// Стая боидов Крейга Рейнольдса (red3d.com/cwr/boids): разделение, выравнивание, сцепление.
/// Хищник раскалывает стаю; рамка захвата отмечает крупнейшую группу.
/// Мир хранится в «квадратных» единицах: x — в ячейках, y — в ячейках * YScale.
/// </summary>
public sealed class BoidsScene : IScene, IAudioReactive, IHandoffReceiver
{
    private const float YScale = 1.8f;              // высота ячейки в квадратных единицах
    private const float ViewRadius = 8f;
    private const float ViewR2 = ViewRadius * ViewRadius;
    private const float SepRadius = 2f;
    private const float SepR2 = SepRadius * SepRadius;
    private const float CosFov = -0.70710678f;     // cos 135°: поле зрения 270°
    private const float WeightSep = 1.5f, WeightAli = 1.2f, WeightCoh = 1.0f;
    private const float BaseMaxSpeed = 14f, MinSpeed = 5f, MaxForce = 60f;
    private const float EdgeMargin = 4f, EdgeGain = 15f;
    private const float PredRadius = 10f, PredSpeed = 22f, FleeGain = 120f;
    private const float ClusterPeriod = 0.5f;
    private const float FrameSmooth = 0.2f, CaptureTime = 0.3f, CaptureMargin = 6f;
    private const float TailPeriod = 0.1f;
    private const float BurstTime = 0.2f, BurstMul = 1.6f;
    private const int MaxAgents = 300;

    private static readonly Rgba Orange = Rgba.Hex(0xFF6600);
    private static readonly Rgba PredRed = Rgba.Hex(0xFF3030);

    // Индекс = номер восьмушки направления (0 = →, 1 = ↘, 2 = ↓ ...). Ось y смотрит вниз.
    private static readonly char[] Heading = { '►', '\\', '▼', '/', '◄', '\\', '▲', '/' };

    public string Id => "boids";

    private int _cols, _rows, _n;
    private float _w, _h, _maxS;
    private Random _rng = new(0);
    private Rgba _base, _dim, _predColor;

    // Агенты
    private float[] _px = Array.Empty<float>(), _py = Array.Empty<float>();
    private float[] _vx = Array.Empty<float>(), _vy = Array.Empty<float>();
    private float[] _ax = Array.Empty<float>(), _ay = Array.Empty<float>();
    private float[] _tailX = Array.Empty<float>(), _tailY = Array.Empty<float>();

    // Пространственный хеш: ячейка = ViewRadius
    private int _gw, _gh;
    private int[] _head = Array.Empty<int>(), _next = Array.Empty<int>();
    private int[] _comp = Array.Empty<int>(), _stack = Array.Empty<int>();

    // Звук
    private bool _audioActive;
    private float _bass, _burstLeft;

    // Хищник
    private bool _predOn;
    private float _predX, _predY, _predVX, _predVY, _predTimer, _predLeft;

    // Таймеры
    private float _tailTimer, _clusterTimer, _captureLeft;

    // Группы и рамка захвата
    private int _clusterCount, _grpSize, _grpRep, _lastRep = -1;
    private float _goalX0, _goalY0, _goalX1, _goalY1;     // цель, клетки
    private float _frameX0, _frameY0, _frameX1, _frameY1; // сглаженная рамка, клетки

    /// <summary>Число связных групп (радиус обзора), пересчитывается раз в 0.5 с.</summary>
    public int ClusterCount => _clusterCount;

    /// <summary>true, пока хищник на экране.</summary>
    public bool PredatorActive => _predOn;

    /// <summary>Средняя нормированная скорость стаи: 0 — хаос, 1 — все летят одинаково.</summary>
    public float Alignment()
    {
        if (_n == 0) return 0f;
        float sx = 0f, sy = 0f;
        for (int i = 0; i < _n; i++)
        {
            float l = MathF.Sqrt(_vx[i] * _vx[i] + _vy[i] * _vy[i]);
            if (l > 1e-6f) { sx += _vx[i] / l; sy += _vy[i] / l; }
        }
        return MathF.Sqrt(sx * sx + sy * sy) / _n;
    }

    /// <summary>
    /// Локальное выравнивание: среднее по агентам косинуса между своей скоростью и средней скоростью соседей
    /// в радиусе обзора (без поля зрения). Агенты без соседей не учитываются. 0 — если таких нет.
    /// </summary>
    public float LocalAlignment()
    {
        float sum = 0f;
        int count = 0;
        for (int i = 0; i < _n; i++)
        {
            float mx = 0f, my = 0f;
            int nb = 0;
            for (int j = 0; j < _n; j++)
            {
                if (j == i) continue;
                float dx = _px[j] - _px[i], dy = _py[j] - _py[i];
                if (dx * dx + dy * dy >= ViewR2) continue;
                mx += _vx[j]; my += _vy[j];
                nb++;
            }
            if (nb == 0) continue;
            float vl = MathF.Sqrt(_vx[i] * _vx[i] + _vy[i] * _vy[i]);
            float ml = MathF.Sqrt(mx * mx + my * my);
            if (vl < 1e-6f || ml < 1e-6f) { count++; continue; }
            sum += (_vx[i] * mx + _vy[i] * my) / (vl * ml);
            count++;
        }
        return count == 0 ? 0f : sum / count;
    }

    public void Init(int cols, int rows, Random rng, SceneOptions options)
    {
        _cols = cols;
        _rows = rows;
        _rng = rng;
        _w = cols;
        _h = rows * YScale;
        _maxS = MathF.Max(BaseMaxSpeed * MathF.Max(0f, options.Speed), MinSpeed);

        string pal = (options.Palette ?? "default").ToLowerInvariant();
        _base = Palettes.Base(options.Palette);
        _dim = _base.Scale(0.45f);
        bool knownPalette = pal is "amber" or "mono" or "green" or "rainbow";
        _predColor = knownPalette ? Palettes.Head(options.Palette, _base) : PredRed;

        int defN = Math.Clamp(cols * rows / 60, 40, MaxAgents);
        _n = Math.Clamp(options.GetInt("count", defN), 1, MaxAgents);

        _px = new float[_n]; _py = new float[_n];
        _vx = new float[_n]; _vy = new float[_n];
        _ax = new float[_n]; _ay = new float[_n];
        _tailX = new float[_n]; _tailY = new float[_n];

        _gw = (int)(_w / ViewRadius) + 1;
        _gh = (int)(_h / ViewRadius) + 1;
        _head = new int[_gw * _gh];
        _next = new int[_n];
        _comp = new int[_n];
        _stack = new int[_n];

        for (int i = 0; i < _n; i++)
        {
            _px[i] = rng.NextSingle() * _w;
            _py[i] = rng.NextSingle() * _h;
            float a = rng.NextSingle() * MathF.Tau;
            float sp = _maxS * (0.5f + 0.5f * rng.NextSingle());
            _vx[i] = MathF.Cos(a) * sp;
            _vy[i] = MathF.Sin(a) * sp;
            _tailX[i] = _px[i];
            _tailY[i] = _py[i];
        }

        _predOn = false;
        _predX = _predY = _predVX = _predVY = _predLeft = 0f;
        _predTimer = 30f + 30f * rng.NextSingle();

        _audioActive = false;
        _bass = 0f;
        _burstLeft = 0f;
        _captureLeft = 0f;
        _tailTimer = TailPeriod;
        _clusterTimer = ClusterPeriod;

        _lastRep = -1;
        Recompute();
        _frameX0 = _goalX0; _frameY0 = _goalY0; _frameX1 = _goalX1; _frameY1 = _goalY1;
    }

    public void Update(float dt)
    {
        if (!(dt > 0f)) return;
        dt = MathF.Min(dt, 0.1f);

        if (_burstLeft > 0f) _burstLeft -= dt;
        if (_captureLeft > 0f) _captureLeft = MathF.Max(0f, _captureLeft - dt);

        _tailTimer -= dt;
        bool snapTail = _tailTimer <= 0f;
        if (snapTail) _tailTimer += TailPeriod;

        UpdatePredator(dt);
        BuildHash();
        ComputeForces();

        if (snapTail)
        {
            Array.Copy(_px, _tailX, _n);
            Array.Copy(_py, _tailY, _n);
        }
        Integrate(dt);

        _clusterTimer -= dt;
        if (_clusterTimer <= 0f)
        {
            _clusterTimer += ClusterPeriod;
            Recompute();
        }

        float k = 1f - MathF.Exp(-dt / FrameSmooth);
        _frameX0 += (_goalX0 - _frameX0) * k;
        _frameY0 += (_goalY0 - _frameY0) * k;
        _frameX1 += (_goalX1 - _frameX1) * k;
        _frameY1 += (_goalY1 - _frameY1) * k;
    }

    public void Draw(CharGrid grid)
    {
        grid.Clear(Rgba.Black);

        // Хвосты: позиция из снимка ~0.1 с назад, тусклой точкой.
        for (int i = 0; i < _n; i++)
        {
            int tc = ColOf(_tailX[i]), tr = RowOf(_tailY[i]);
            int hc = ColOf(_px[i]), hr = RowOf(_py[i]);
            if (tc != hc || tr != hr) grid.Put(tc, tr, '·', _dim);
        }

        // Агенты: символ по направлению, яркость по скорости.
        for (int i = 0; i < _n; i++)
        {
            float vx = _vx[i], vy = _vy[i];
            float sp = MathF.Sqrt(vx * vx + vy * vy);
            float t = Math.Clamp((sp - MinSpeed) / MathF.Max(_maxS - MinSpeed, 1e-3f), 0f, 1f);
            float screenVy = vy / YScale;
            int k = (int)MathF.Round(MathF.Atan2(screenVy, vx) / (MathF.PI / 4f));
            k = ((k % 8) + 8) % 8;
            grid.Put(ColOf(_px[i]), RowOf(_py[i]), Heading[k], _base.Scale(0.75f + 0.25f * t));
        }

        DrawPredator(grid);
        DrawFrame(grid);
        DrawHud(grid);
    }

    public void OnAudio(in AudioFrame audio)
    {
        _audioActive = audio.Active;
        _bass = audio.Active ? audio.Bass : 0f;
        if (!audio.Active || !audio.Beat || _n == 0) return;

        // Вспышка: скорость каждого агента растёт в сторону от центра масс.
        float cx = 0f, cy = 0f;
        for (int i = 0; i < _n; i++) { cx += _px[i]; cy += _py[i]; }
        cx /= _n; cy /= _n;

        _burstLeft = BurstTime;
        for (int i = 0; i < _n; i++)
        {
            float dx = _px[i] - cx, dy = _py[i] - cy;
            var (ux, uy) = Dir(dx, dy);
            float sp = MathF.Sqrt(_vx[i] * _vx[i] + _vy[i] * _vy[i]);
            _vx[i] += ux * sp * (BurstMul - 1f);
            _vy[i] += uy * sp * (BurstMul - 1f);
        }
    }

    /// <summary>
    /// Эстафета: агенты стартуют в самых ярких ячейках previous (по одному на ячейку, со сдвигом внутри ячейки),
    /// скорость — случайное направление × MinSpeed. Если ярких ячеек меньше N, остальные агенты — как после Init.
    /// </summary>
    public void Seed(CharGrid previous)
    {
        int total = previous.Cells.Length;
        var keys = new float[total];
        var vals = new int[total];
        int cnt = 0;
        for (int i = 0; i < total; i++)
        {
            float l = HandoffLuma(previous.Cells[i]);
            if (l <= 0f) continue;
            keys[cnt] = -l;
            vals[cnt] = i;
            cnt++;
        }
        Array.Sort(keys, vals, 0, cnt); // по убыванию яркости

        int pc = previous.Cols, pr = previous.Rows;
        int k = Math.Min(_n, cnt);
        for (int a = 0; a < k; a++)
        {
            int ci = vals[a];
            int cx = ci % pc, cy = ci / pc;
            float x = (cx + 0.5f + (_rng.NextSingle() - 0.5f) * 0.6f) * _w / pc;
            float y = (cy + 0.5f + (_rng.NextSingle() - 0.5f) * 0.6f) * _h / pr;
            _px[a] = Math.Clamp(x, 0f, _w - 1e-3f);
            _py[a] = Math.Clamp(y, 0f, _h - 1e-3f);
            float ang = _rng.NextSingle() * MathF.Tau;
            _vx[a] = MathF.Cos(ang) * MinSpeed;
            _vy[a] = MathF.Sin(ang) * MinSpeed;
            _tailX[a] = _px[a];
            _tailY[a] = _py[a];
        }

        // Как в Init: пересчёт групп и рамка сразу в цели.
        _lastRep = -1;
        Recompute();
        _frameX0 = _goalX0; _frameY0 = _goalY0; _frameX1 = _goalX1; _frameY1 = _goalY1;
    }

    /// <summary>Яркость ячейки для эстафеты: max(Fg) у непробельного символа, иначе max(Bg), 0..1.</summary>
    private static float HandoffLuma(Cell c)
    {
        var k = c.Ch != ' ' ? c.Fg : c.Bg;
        return MathF.Max(k.R, MathF.Max(k.G, k.B)) / 255f;
    }

    // ---------- Правила стаи ----------

    private void ComputeForces()
    {
        float cohW = WeightCoh * (_audioActive ? 1f + _bass : 1f);

        for (int i = 0; i < _n; i++)
        {
            float px = _px[i], py = _py[i], vx = _vx[i], vy = _vy[i];
            float vlen = MathF.Sqrt(vx * vx + vy * vy);

            float sepX = 0f, sepY = 0f, avX = 0f, avY = 0f, cX = 0f, cY = 0f;
            int nSep = 0, nView = 0;

            int gx = HashX(px), gy = HashY(py);
            int y0 = Math.Max(0, gy - 1), y1 = Math.Min(_gh - 1, gy + 1);
            int x0 = Math.Max(0, gx - 1), x1 = Math.Min(_gw - 1, gx + 1);
            for (int ny = y0; ny <= y1; ny++)
            {
                for (int nx = x0; nx <= x1; nx++)
                {
                    for (int j = _head[ny * _gw + nx]; j >= 0; j = _next[j])
                    {
                        if (j == i) continue;
                        float dx = _px[j] - px, dy = _py[j] - py;
                        float d2 = dx * dx + dy * dy;
                        if (d2 >= ViewR2) continue;

                        if (d2 < SepR2)
                        {
                            sepX -= dx / (d2 + 0.01f);
                            sepY -= dy / (d2 + 0.01f);
                            nSep++;
                        }

                        float d = MathF.Sqrt(d2);
                        if (vlen > 0f && vx * dx + vy * dy < CosFov * vlen * d) continue; // вне поля зрения

                        avX += _vx[j]; avY += _vy[j];
                        cX += dx; cY += dy;
                        nView++;
                    }
                }
            }

            float ax = 0f, ay = 0f;
            if (nSep > 0)
            {
                var (dx, dy) = Dir(sepX, sepY);
                ax += WeightSep * (dx * _maxS - vx);
                ay += WeightSep * (dy * _maxS - vy);
            }
            if (nView > 0)
            {
                // Выравнивание: (средняя скорость соседей − своя скорость) * вес.
                float mx = avX / nView, my = avY / nView;
                ax += WeightAli * (mx - vx);
                ay += WeightAli * (my - vy);

                // Сцепление: целевая скорость пропорциональна смещению до центра соседей.
                float ccx = cX / nView, ccy = cY / nView;
                ax += cohW * (ccx - vx);
                ay += cohW * (ccy - vy);
            }

            float al = MathF.Sqrt(ax * ax + ay * ay);
            if (al > MaxForce) { ax *= MaxForce / al; ay *= MaxForce / al; }

            // Мягкое отталкивание от краёв.
            if (px < EdgeMargin) ax += (EdgeMargin - px) * EdgeGain;
            else if (px > _w - EdgeMargin) ax -= (px - (_w - EdgeMargin)) * EdgeGain;
            if (py < EdgeMargin) ay += (EdgeMargin - py) * EdgeGain;
            else if (py > _h - EdgeMargin) ay -= (py - (_h - EdgeMargin)) * EdgeGain;

            // Бегство от хищника.
            if (_predOn)
            {
                float fx = px - _predX, fy = py - _predY;
                float fd = MathF.Sqrt(fx * fx + fy * fy);
                if (fd < PredRadius)
                {
                    float k = FleeGain * (1f - 0.5f * fd / PredRadius) / MathF.Max(fd, 1e-3f);
                    ax += fx * k;
                    ay += fy * k;
                }
            }

            _ax[i] = ax;
            _ay[i] = ay;
        }
    }

    private void Integrate(float dt)
    {
        float maxNow = _burstLeft > 0f ? _maxS * BurstMul : _maxS;
        for (int i = 0; i < _n; i++)
        {
            float vx = _vx[i] + _ax[i] * dt, vy = _vy[i] + _ay[i] * dt;
            float sp = MathF.Sqrt(vx * vx + vy * vy);
            if (sp > maxNow)
            {
                vx *= maxNow / sp; vy *= maxNow / sp;
            }
            else if (sp < MinSpeed)
            {
                if (sp > 1e-4f) { vx *= MinSpeed / sp; vy *= MinSpeed / sp; }
                else { vx = MinSpeed; vy = 0f; }
            }
            _vx[i] = vx;
            _vy[i] = vy;
            _px[i] = Math.Clamp(_px[i] + vx * dt, 0f, _w - 1e-3f);
            _py[i] = Math.Clamp(_py[i] + vy * dt, 0f, _h - 1e-3f);
        }
    }

    private void UpdatePredator(float dt)
    {
        if (_predOn)
        {
            _predX += _predVX * dt;
            _predY += _predVY * dt;
            _predLeft -= dt;
            if (_predLeft <= 0f || _predX < -3f || _predX > _w + 3f || _predY < -3f || _predY > _h + 3f)
            {
                _predOn = false;
                _predTimer = 30f + 30f * _rng.NextSingle();
            }
        }
        else
        {
            _predTimer -= dt;
            if (_predTimer <= 0f) SpawnPredator();
        }
    }

    private void SpawnPredator()
    {
        bool fromLeft = _rng.Next(2) == 0;
        _predOn = true;
        _predX = fromLeft ? -2f : _w + 2f;
        _predY = _h * (0.25f + 0.5f * _rng.NextSingle());
        _predVX = fromLeft ? PredSpeed : -PredSpeed;
        _predVY = (_rng.NextSingle() - 0.5f) * 4f;
        _predLeft = (_w + 8f) / PredSpeed + 1f;
    }

    // ---------- Группы и хеш ----------

    private void BuildHash()
    {
        Array.Fill(_head, -1);
        for (int i = 0; i < _n; i++)
        {
            int c = HashY(_py[i]) * _gw + HashX(_px[i]);
            _next[i] = _head[c];
            _head[c] = i;
        }
    }

    /// <summary>Связные группы по радиусу обзора; крупнейшая получает цель и рамку.</summary>
    private void Recompute()
    {
        BuildHash();
        Array.Fill(_comp, -1);

        int clusters = 0, bestSize = 0, bestRep = 0;
        float bx0 = 0f, by0 = 0f, bx1 = 0f, by1 = 0f;

        for (int s = 0; s < _n; s++)
        {
            if (_comp[s] >= 0) continue;
            int id = clusters++;
            int sp = 0, size = 0, rep = s;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;

            _comp[s] = id;
            _stack[sp++] = s;
            while (sp > 0)
            {
                int i = _stack[--sp];
                size++;
                if (i < rep) rep = i;

                float cx = _px[i], cy = _py[i] / YScale;
                if (cx < x0) x0 = cx;
                if (cx > x1) x1 = cx;
                if (cy < y0) y0 = cy;
                if (cy > y1) y1 = cy;

                int gx = HashX(_px[i]), gy = HashY(_py[i]);
                int ya = Math.Max(0, gy - 1), yb = Math.Min(_gh - 1, gy + 1);
                int xa = Math.Max(0, gx - 1), xb = Math.Min(_gw - 1, gx + 1);
                for (int ny = ya; ny <= yb; ny++)
                {
                    for (int nx = xa; nx <= xb; nx++)
                    {
                        for (int j = _head[ny * _gw + nx]; j >= 0; j = _next[j])
                        {
                            if (_comp[j] >= 0) continue;
                            float dx = _px[j] - _px[i], dy = _py[j] - _py[i];
                            if (dx * dx + dy * dy >= ViewR2) continue;
                            _comp[j] = id;
                            _stack[sp++] = j;
                        }
                    }
                }
            }

            if (size > bestSize)
            {
                bestSize = size;
                bestRep = rep;
                bx0 = x0; by0 = y0; bx1 = x1; by1 = y1;
            }
        }

        _clusterCount = clusters;
        _grpSize = bestSize;
        _goalX0 = bx0 - 1.5f;
        _goalY0 = by0 - 1.5f;
        _goalX1 = bx1 + 1.5f;
        _goalY1 = by1 + 1.5f;

        if (bestRep != _lastRep)
        {
            if (_lastRep >= 0) _captureLeft = CaptureTime; // захват новой цели
            _lastRep = bestRep;
        }
        _grpRep = bestRep;
    }

    // ---------- Отрисовка ----------

    private void DrawPredator(CharGrid grid)
    {
        if (!_predOn) return;
        int pc = ColOf(_predX), pr = RowOf(_predY);
        grid.Put(pc, pr, '■', _predColor);
        if (pc + 9 < _cols)
        {
            grid.Put(pc + 2, pr, '┼', PredRed);
            grid.Text(pc + 4, pr, "THREAT", PredRed);
        }
        else
        {
            grid.Put(pc - 2, pr, '┼', PredRed);
            grid.Text(pc - 9, pr, "THREAT", PredRed);
        }
    }

    private void DrawFrame(CharGrid grid)
    {
        if (_cols < 4 || _rows < 3) return;

        float m = _captureLeft > 0f ? CaptureMargin * (_captureLeft / CaptureTime) : 0f;
        int x0 = Math.Clamp((int)MathF.Round(_frameX0 - m), 0, _cols - 1);
        int x1 = Math.Clamp((int)MathF.Round(_frameX1 + m), 0, _cols - 1);
        int y0 = Math.Clamp((int)MathF.Round(_frameY0 - m), 0, _rows - 1);
        int y1 = Math.Clamp((int)MathF.Round(_frameY1 + m), 0, _rows - 1);
        if (x1 < x0 + 3) { x1 = Math.Min(_cols - 1, x0 + 3); x0 = Math.Max(0, x1 - 3); }
        if (y1 < y0 + 2) { y1 = Math.Min(_rows - 1, y0 + 2); y0 = Math.Max(0, y1 - 2); }

        grid.Put(x0, y0, '┌', Orange); grid.Put(x0 + 1, y0, '─', Orange);
        grid.Put(x1 - 1, y0, '─', Orange); grid.Put(x1, y0, '┐', Orange);
        grid.Put(x0, y1, '└', Orange); grid.Put(x0 + 1, y1, '─', Orange);
        grid.Put(x1 - 1, y1, '─', Orange); grid.Put(x1, y1, '┘', Orange);

        // Подпись под правым нижним углом (если нет места снизу — внутри рамки).
        int labelLen = 9 + DigitCount(_grpSize);
        int lx = x1 - labelLen + 1;
        int ly = y1 + 1 < _rows ? y1 + 1 : y1 - 1;
        grid.Text(lx, ly, "TGT-01 · ", Orange);
        PutNumber(grid, lx + 9, ly, _grpSize, Orange);
    }

    private void DrawHud(CharGrid grid)
    {
        int x = 2, y = 2;
        grid.Text(x, y, "FLOCK · N=", _dim); x += 10;
        x = PutPadded(grid, x, y, _n, 4, _dim);
        grid.Text(x, y, " · CLUSTERS ", _dim); x += 12;
        PutPadded(grid, x, y, Math.Min(_clusterCount, 99), 2, _dim);
    }

    // ---------- Утилиты (без аллокаций) ----------

    private int ColOf(float x) => Math.Clamp((int)x, 0, _cols - 1);
    private int RowOf(float y) => Math.Clamp((int)(y / YScale), 0, _rows - 1);
    private int HashX(float x) => Math.Clamp((int)(x / ViewRadius), 0, _gw - 1);
    private int HashY(float y) => Math.Clamp((int)(y / ViewRadius), 0, _gh - 1);

    private static (float, float) Dir(float x, float y)
    {
        float l = MathF.Sqrt(x * x + y * y);
        return l > 1e-6f ? (x / l, y / l) : (0f, 0f);
    }

    private static int DigitCount(int v)
    {
        int n = 1;
        while (v >= 10) { v /= 10; n++; }
        return n;
    }

    private static void PutNumber(CharGrid g, int x, int y, int v, Rgba c)
    {
        int len = DigitCount(v);
        for (int k = len - 1; k >= 0; k--)
        {
            g.Put(x + k, y, (char)('0' + v % 10), c);
            v /= 10;
        }
    }

    private static int PutPadded(CharGrid g, int x, int y, int v, int digits, Rgba c)
    {
        for (int k = digits - 1; k >= 0; k--)
        {
            g.Put(x + k, y, (char)('0' + v % 10), c);
            v /= 10;
        }
        return x + digits;
    }
}
