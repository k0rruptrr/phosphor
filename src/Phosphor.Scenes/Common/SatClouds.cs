using System.Globalization;
using Phosphor.Core;

namespace Phosphor.Scenes;

/// <summary>
/// Трекинг облаков для кадров Equirect. Области ищутся на сетке 64×32 по адаптивному порогу (верхние 12% яркости).
/// Для каждой области (до фильтров) блочным сопоставлением по пикселям кадра считается сдвиг: окно 33×33 из последнего
/// кадра ищется в базовом кадре в пределах ±6 пикселей, по среднему |разности| яркости (MAD), сдвиг уточняется параболой.
/// Фильтры: крупные (&gt; 15% сетки), вытянутые, у края (&lt; 20 пикселей) — не берутся. Из оставшихся — 2–3 крупнейших.
/// Сдвиг → км (1° = 111 км, долгота × cos широты) → км/ч. Если сопоставление плохое или скорость &gt; 200 км/ч — «ТРЕК».
/// </summary>
public sealed class SatClouds
{
    public const int GW = 64, GH = 32;
    public const int MaxTracked = 3;
    private const int MaxComps = 64;
    private const int MinArea = 3;
    private const double TopFrac = 0.12;      // яркие — верхние 12% клеток сетки
    private const double MaxAreaFrac = 0.15;  // больше 15% сетки — фон, не облако
    private const double MinCompact = 0.25;   // площадь / площадь bbox
    private const int Search = 6;             // поиск сдвига ±6 пикселей кадра
    private const int Half = 16;              // полуокно сопоставления: 33×33 пикселя
    private const int EdgeMargin = 20;        // окно не помещается ближе 20 пикселей к краю кадра
    private const double MaxBadMad = 0.15;    // средняя |разность| яркости выше — трек ненадёжен
    private const double MaxMadRatio = 0.7;   // лучший MAD / медиана по окну поиска: по данным meteosat 0.58–0.66 у облака, 0.86–0.87 у шума
    private const double MaxSpeedKmh = 200;
    private const double KmPerDeg = 111.0;
    private const string Arrows = "↑→↓←";

    /// <summary>Облако для показа: рамка в долях кадра, скорость км/ч (NaN — не выдаём), стрелка, подпись.</summary>
    public struct Cloud
    {
        public float U0, V0, U1, V1;
        public double SpeedKmh;
        public char Arrow;
        public string Label;
    }

    /// <summary>
    /// Кандидат до фильтров (для диагностики). Bbox — в клетках сетки; Mx/My — сдвиг в пикселях кадра (последний → базовый
    /// совпадает при смещении −движение; здесь уже движение: +x — на восток); Mad — лучший; Mad0 — при нулевом сдвиге;
    /// MadMedian — медиана MAD по всем сдвигам; NaN, если не посчитано.
    /// </summary>
    public readonly record struct Candidate(int GX0, int GY0, int GX1, int GY1, int Area, bool Compact, bool Big, bool Edge,
        double Mx, double My, double Mad, double Mad0, double MadMedian, double SpeedKmh, double Hours);

    public Cloud[] Items { get; } = new Cloud[MaxTracked];
    public int Count { get; private set; }

    /// <summary>Все кандидаты последнего Compute (до фильтров), для диагностики.</summary>
    public IReadOnlyList<Candidate> Candidates => _candidates;
    private readonly List<Candidate> _candidates = new();

    private struct Comp
    {
        public int Area, X0, Y0, X1, Y1;
        public double Cx, Cy; // взвешенный центроид в координатах сетки (клетка i — центр i+0.5)
    }

    private readonly float[] _lum = new float[GW * GH];
    private readonly float[] _sorted = new float[GW * GH];
    private readonly int[] _lab = new int[GW * GH];
    private readonly int[] _queue = new int[GW * GH];
    private readonly Comp[] _cur = new Comp[MaxComps];
    private readonly int[] _pick = new int[MaxTracked];
    private readonly float[] _madBuf = new float[(2 * Search + 1) * (2 * Search + 1)];
    private float[] _lumLast = Array.Empty<float>();
    private float[] _lumBase = Array.Empty<float>();
    private int _w, _h;

    public void Clear()
    {
        Count = 0;
        _candidates.Clear();
    }

    /// <summary>Пересчитать по последнему кадру и базовому (более раннему) кадру. baseline = null — облаков нет.</summary>
    public void Compute(SatelliteFrame last, SatelliteFrame? baseline, SatelliteArea area)
    {
        Clear();
        if (baseline is null || area.East <= area.West || area.North <= area.South) return;
        if (baseline.Width != last.Width || baseline.Height != last.Height) return;

        int n = Detect(last, _cur);
        _w = last.Width;
        _h = last.Height;
        FillLuma(last, ref _lumLast);
        FillLuma(baseline, ref _lumBase);
        double hours = (last.TakenAt - baseline.TakenAt).TotalHours;

        // Метрики всех кандидатов (до фильтров).
        for (int i = 0; i < n; i++)
        {
            Comp c = _cur[i];
            var m = Measure(c, area, hours);
            bool big = c.Area > GW * GH * MaxAreaFrac;
            bool compact = c.Area >= MinCompact * ((c.X1 - c.X0 + 1) * (double)(c.Y1 - c.Y0 + 1));
            _candidates.Add(new Candidate(c.X0, c.Y0, c.X1, c.Y1, c.Area, compact, big, NearEdge(c, _w, _h),
                m.Mx, m.My, m.Mad, m.Mad0, m.MadMedian, m.Speed, hours));
        }

        // Отбор: не большие, компактные, не у края; крупнейшие — первыми; «ТРЕК», если сопоставление плохое.
        int cnt = 0;
        for (int k = 0; k < MaxTracked; k++)
        {
            int best = -1;
            for (int i = 0; i < _candidates.Count; i++)
            {
                var cd = _candidates[i];
                if (cd.Big || !cd.Compact || cd.Edge || IsPicked(i, k)) continue;
                if (best < 0 || cd.Area > _candidates[best].Area) best = i;
            }
            if (best < 0) break;
            _pick[k] = best;

            Candidate cd2 = _candidates[best];
            bool bad = !(hours > 0) || !double.IsFinite(cd2.Mad) || cd2.Mad > MaxBadMad || cd2.SpeedKmh > MaxSpeedKmh
                       || !(cd2.Mad < MaxMadRatio * cd2.MadMedian); // минимум должен быть заметно глубже медианы
            string name = "CLD-0" + (k + 1).ToString(CultureInfo.InvariantCulture);
            char arrow = '\0';
            double speed = bad ? double.NaN : cd2.SpeedKmh;
            string label;
            if (bad) label = name + " · ТРЕК";
            else
            {
                var m = Measure(_cur[best], area, hours);
                double bearing = Math.Atan2(m.DxKm, m.DyKm) * 180 / Math.PI;
                if (bearing < 0) bearing += 360;
                arrow = Arrows[((int)Math.Round(bearing / 90.0)) & 3];
                label = name + " " + arrow + " " + ((int)Math.Round(speed)).ToString(CultureInfo.InvariantCulture) + " КМ/Ч";
            }

            Comp c = _cur[best];
            Items[cnt++] = new Cloud
            {
                U0 = (float)c.X0 / GW,
                V0 = (float)c.Y0 / GH,
                U1 = (float)(c.X1 + 1) / GW,
                V1 = (float)(c.Y1 + 1) / GH,
                SpeedKmh = speed,
                Arrow = arrow,
                Label = label,
            };
        }
        Count = cnt;
    }

    private readonly record struct Measurement(double Mx, double My, double Mad, double Mad0, double MadMedian,
        double Speed, double DxKm, double DyKm);

    /// <summary>Сопоставление окна вокруг области: лучший сдвиг, MAD, MAD при нуле, медиана, скорость.</summary>
    private Measurement Measure(Comp c, SatelliteArea area, double hours)
    {
        int ix = (int)Math.Round(c.Cx / GW * _w), iy = (int)Math.Round(c.Cy / GH * _h);

        int cnt = 0;
        double bestMad = double.PositiveInfinity;
        int sx = 0, sy = 0;
        for (int dy = -Search; dy <= Search; dy++)
            for (int dx = -Search; dx <= Search; dx++)
            {
                double m = Mad(ix, iy, dx, dy);
                if (double.IsFinite(m)) _madBuf[cnt++] = (float)m;
                if (m < bestMad) { bestMad = m; sx = dx; sy = dy; }
            }
        double median = double.NaN;
        if (cnt > 0)
        {
            Array.Sort(_madBuf, 0, cnt);
            median = _madBuf[cnt / 2];
        }

        // Окно из последнего кадра совпадает с базовым при смещении s = −движение (объект уехал на +движение).
        double mx = -sx, my = -sy;
        if (double.IsFinite(bestMad))
        {
            if (sx > -Search && sx < Search) mx -= Parab(Mad(ix, iy, sx - 1, sy), bestMad, Mad(ix, iy, sx + 1, sy));
            if (sy > -Search && sy < Search) my -= Parab(Mad(ix, iy, sx, sy - 1), bestMad, Mad(ix, iy, sx, sy + 1));
        }

        double lat = area.North - (c.Cy / GH * _h) / _h * (area.North - area.South);
        double cosLat = Math.Max(0.1, Math.Cos(lat * Math.PI / 180));
        double dxKm = mx * (area.East - area.West) / _w * KmPerDeg * cosLat; // восток — вправо
        double dyKm = -my * (area.North - area.South) / _h * KmPerDeg;       // север — вверх (y растёт вниз)
        double speed = hours > 0 ? Math.Sqrt(dxKm * dxKm + dyKm * dyKm) / hours : double.NaN;
        return new Measurement(mx, my, bestMad, Mad(ix, iy, 0, 0), median, speed, dxKm, dyKm);
    }

    /// <summary>bbox области ближе EdgeMargin пикселей кадра к краю: окно сопоставления не помещается.</summary>
    private static bool NearEdge(Comp c, int w, int h)
    {
        // Центр окна (центроид) должен быть не ближе EdgeMargin к краю; облако, которое уходит за край, — допустимо.
        double cx = c.Cx / GW * w, cy = c.Cy / GH * h;
        return cx < EdgeMargin || cy < EdgeMargin || cx > w - EdgeMargin || cy > h - EdgeMargin;
    }

    private bool IsPicked(int i, int k)
    {
        for (int j = 0; j < k; j++) if (_pick[j] == i) return true;
        return false;
    }

    /// <summary>Средняя |разность| яркости окна 33×33 из последнего кадра (центр ix,iy) и базового (центр +dx,+dy).</summary>
    private double Mad(int ix, int iy, int dx, int dy)
    {
        int w = _w, h = _h;
        int bx = ix + dx, by = iy + dy;
        if (ix - Half < 0 || iy - Half < 0 || ix + Half >= w || iy + Half >= h) return double.PositiveInfinity;
        if (bx - Half < 0 || by - Half < 0 || bx + Half >= w || by + Half >= h) return double.PositiveInfinity;
        double acc = 0;
        int cnt = 0;
        for (int v = -Half; v <= Half; v++)
            for (int u = -Half; u <= Half; u++)
            {
                acc += Math.Abs(_lumLast[(iy + v) * w + ix + u] - _lumBase[(by + v) * w + bx + u]);
                cnt++;
            }
        return acc / cnt;
    }

    /// <summary>Смещение минимума параболы по трём значениям (в пределах ±0.5 пикселя).</summary>
    private static double Parab(double l, double c, double r)
    {
        if (!double.IsFinite(l) || !double.IsFinite(r)) return 0;
        double denom = l - 2 * c + r;
        if (!(denom > 1e-12)) return 0;
        return Math.Max(-0.5, Math.Min(0.5, 0.5 * (l - r) / denom));
    }

    private static void FillLuma(SatelliteFrame f, ref float[] buf)
    {
        int n = f.Width * f.Height;
        if (buf.Length < n) buf = new float[n];
        byte[] d = f.Rgb;
        for (int p = 0; p < n; p++)
        {
            int i = p * 3;
            buf[p] = (d[i] * 299 + d[i + 1] * 587 + d[i + 2] * 114) / (1000f * 255f);
        }
    }

    /// <summary>Связные области сетки над адаптивным порогом (верхние 12% клеток), все, без фильтров. Возвращает число.</summary>
    private int Detect(SatelliteFrame f, Comp[] comps)
    {
        int w = f.Width, h = f.Height;
        byte[] d = f.Rgb;
        for (int gy = 0; gy < GH; gy++)
        {
            int y0 = gy * h / GH, y1 = Math.Min(h, Math.Max(y0 + 1, (gy + 1) * h / GH));
            for (int gx = 0; gx < GW; gx++)
            {
                int x0 = gx * w / GW, x1 = Math.Min(w, Math.Max(x0 + 1, (gx + 1) * w / GW));
                double acc = 0;
                int cnt = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++, cnt++)
                    {
                        int i = (y * w + x) * 3;
                        acc += (d[i] * 299 + d[i + 1] * 587 + d[i + 2] * 114) / 1000.0 / 255.0;
                    }
                _lum[gy * GW + gx] = (float)(acc / cnt);
            }
        }

        int total = GW * GH;
        Array.Copy(_lum, _sorted, total);
        Array.Sort(_sorted);
        if (_sorted[total - 1] - _sorted[0] < 0.02f) return 0; // почти однотонный кадр
        float thr = _sorted[(int)(total * (1 - TopFrac))];

        Array.Clear(_lab);
        int n = 0, id = 0;
        for (int s = 0; s < total; s++)
        {
            if (_lab[s] != 0 || _lum[s] <= thr) continue;
            id++;
            int head = 0, tail = 0;
            _queue[tail++] = s;
            _lab[s] = id;
            var c = new Comp { X0 = GW, Y0 = GH, X1 = -1, Y1 = -1 };
            double sw = 0, sx = 0, sy = 0;
            while (head < tail)
            {
                int p = _queue[head++];
                int px = p % GW, py = p / GW;
                double wgt = _lum[p] - thr + 1e-4;
                c.Area++;
                sw += wgt;
                sx += wgt * (px + 0.5);
                sy += wgt * (py + 0.5);
                c.X0 = Math.Min(c.X0, px); c.X1 = Math.Max(c.X1, px);
                c.Y0 = Math.Min(c.Y0, py); c.Y1 = Math.Max(c.Y1, py);
                Push(px - 1, py, thr, id, ref tail);
                Push(px + 1, py, thr, id, ref tail);
                Push(px, py - 1, thr, id, ref tail);
                Push(px, py + 1, thr, id, ref tail);
            }

            if (c.Area >= MinArea && n < MaxComps && sw > 0)
            {
                c.Cx = sx / sw;
                c.Cy = sy / sw;
                comps[n++] = c;
            }
        }
        return n;
    }

    private void Push(int x, int y, float thr, int id, ref int tail)
    {
        if ((uint)x >= GW || (uint)y >= GH) return;
        int q = y * GW + x;
        if (_lab[q] != 0 || _lum[q] <= thr) return;
        _lab[q] = id;
        _queue[tail++] = q;
    }
}
