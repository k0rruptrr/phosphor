#version 330 core

// Проход 2: декодирование сигнала (tv_encode.frag) как в телевизоре, смешивание с исходником по amount.
// NTSC и PAL: гребенчатый фильтр (яркость = сумма строк с противоположной фазой, цветность = разность).
// SECAM: модель по исходнику (uSrc), композит не используется: яркость с лёгким ФНЧ, цвет с асимметричным
// ФНЧ по горизонтали и огрублением через строку, факелы и снег приёма по хешу.

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uInput;     // композит (PAL/NTSC): R = s
uniform sampler2D uSrc;       // исходная картинка (SECAM читает её напрямую)
uniform vec2 uResolution;
uniform float uTime;
uniform int uFrame;

uniform float p_mode;         // 0 = PAL, 1 = SECAM, 2 = NTSC
uniform float p_amount;
uniform float p_hue;          // градусы, только NTSC (Tint)
uniform float p_sharpness;
uniform float p_saturation;   // SECAM: насыщенность цвета
uniform float p_fires;        // SECAM: количество факелов
uniform float p_signal;       // SECAM: качество приёма (снег яркости, факелы от шума)
uniform float p_edge;         // SECAM: сигналы опознавания (полоса) сверху кадра

const float PI = 3.1415927;
const float TAU = 6.2831853;
const float SC_REF = 1920.0;
const float NTSC_CYC = 188.0;
const float PAL_CYC = 230.0;
const float SIG_NARROW = 4.5;         // сигма узкого ядра, px при 1920
const float SIG_WIDE = 9.0;           // сигма широкого ядра, px при 1920
const float NTSC_DRIFT = 4.0 * PI / 180.0;   // внутренняя ошибка фазы NTSC, множитель amount
const float PAL_ERR = 5.0 * PI / 180.0;      // внутренняя ошибка фазы PAL (линия задержки гасит её в насыщенность)
const float SECAM_SEG = 16.0;         // сегмент строки для факелов, px при 1920
const int SECAM_SEG_N = 4;            // сегментов назад от пикселя (покрывает 64 px влево при 16 px сегментах)
// ФНЧ цвета, px на 1280. Хвост ВЛЕВО от пикселя (d < 0, длинный): цвет из левых точек приезжает в пиксель,
// то есть цвет течёт ВПРАВО от своего источника. Короткая сторона ВПРАВО от пикселя (d > 0).
const float SECAM_CHROMA_TAIL = 12.0;
const float SECAM_CHROMA_NEAR = 4.0;

uint ihash(uint x) {
    x ^= x >> 16u;
    x *= 0x7feb352du;
    x ^= x >> 15u;
    x *= 0x846ca68bu;
    x ^= x >> 16u;
    return x;
}

float rnd(uint h) { return float(h) * (1.0 / 4294967296.0); }

// Гаусс-тап: k в [-8..8], шаг сигма/4, вес exp(-k^2/32). Таблица по |k|.
const float GWT[9] = float[9](1.0, 0.969233, 0.882497, 0.754840, 0.606531,
                              0.457833, 0.324652, 0.216265, 0.135335);
float gw(int k) { return GWT[abs(k)]; }

float scale() { return uResolution.x / SC_REF; }

// Яркость Rec.601.
float lum(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }

// Композит в строке ry (целая), позиция x в px.
float sigAt(float x, int ry) {
    return texture(uInput, vec2(x / uResolution.x, (float(ry) + 0.5) / uResolution.y)).r;
}

// Сигнал цветности для демодуляции (PAL/NTSC): m = 2 (NTSC): разность с предыдущей строкой, m = 0 (PAL): с строкой n-2.
float csig(float xp, int ry, int m) {
    if (m == 2) return 0.5 * (sigAt(xp, ry) - sigAt(xp, max(ry - 1, 0)));
    return 0.5 * (sigAt(xp, ry) - sigAt(xp, max(ry - 2, 0)));
}

// Гребенчатая яркость: сумма строк с противоположной фазой поднесущей, цветность сокращается.
float combY(float x, int row, int m) {
    int rp = (m == 2) ? max(row - 1, 0) : max(row - 2, 0);
    return 0.5 * (sigAt(x, row) + sigAt(x, rp));
}

// Низкочастотный фильтр яркости (гаусс сигма).
float lumaLP(float x, int ry, float sigma) {
    float acc = 0.0;
    float ws = 0.0;
    for (int k = -8; k <= 8; k++) {
        float w = gw(k);
        acc += w * sigAt(x + float(k) * sigma * 0.25, ry);
        ws += w;
    }
    return acc / ws;
}

// Демодуляция: 2 * LP(c * cos или sin опорной фазы). Опорная фаза в тапах k идёт с постоянным шагом,
// поэтому cos/sin считаются вращением фазора (два sin/cos на пиксель вместо 17).
float demod(float x, int ry, float cyc, float shift, float sigma, bool useSin, int m) {
    float W = uResolution.x;
    float dl = TAU * cyc * sigma * 0.25 / W;
    float a0 = TAU * cyc * x / W + shift - 8.0 * dl;
    vec2 z = vec2(cos(a0), sin(a0));
    vec2 st = vec2(cos(dl), sin(dl));
    float acc = 0.0;
    float ws = 0.0;
    for (int k = -8; k <= 8; k++) {
        float w = gw(k);
        float xp = x + float(k) * sigma * 0.25;
        float r = useSin ? z.y : z.x;
        acc += w * csig(xp, ry, m) * r;
        ws += w;
        z = vec2(z.x * st.x - z.y * st.y, z.x * st.y + z.y * st.x);
    }
    return 2.0 * acc / ws;
}

// Шум времени: гладкая интерполяция целочисленных значений в [-1, 1].
float hash1(int i) { return float(ihash(uint(i))) * (2.0 / 4294967296.0) - 1.0; }
float smoothNoise(float t) {
    float i = floor(t);
    float f = t - i;
    f = f * f * (3.0 - 2.0 * f);
    return mix(hash1(int(i)), hash1(int(i) + 1), f);
}

// ---------- SECAM (по исходнику) ----------

// Исходный цвет в пикселе (px, ry); координаты зажаты в кадр.
vec3 srcRgb(float px, int ry) {
    float xc = clamp(px, 0.0, uResolution.x - 1.0);
    float yc = clamp(float(ry), 0.0, uResolution.y - 1.0);
    return texture(uSrc, vec2((xc + 0.5) / uResolution.x, (yc + 0.5) / uResolution.y)).rgb;
}

// Яркость SECAM: лёгкий ФНЧ 0.25 / 0.5 / 0.25 по горизонтали.
float secamLuma(float px, int row) {
    return 0.25 * lum(srcRgb(px - 1.0, row)) + 0.5 * lum(srcRgb(px, row))
         + 0.25 * lum(srcRgb(px + 1.0, row));
}

// Цветность (Cb, Cr) строки ry, ФНЧ по горизонтали: треугольное ядро, хвост влево 12 px, вправо 4 px (на 1280).
// Левый (длинный) хвост даёт растекание цвета ВПРАВО от источника.
vec2 chromaRow(float px, int ry) {
    float s = uResolution.x / 1280.0;
    int tail = int(SECAM_CHROMA_TAIL * s + 0.5);
    int near = int(SECAM_CHROMA_NEAR * s + 0.5);
    vec2 acc = vec2(0.0);
    float ws = 0.0;
    for (int d = -tail; d <= near; d++) {
        float w = (d < 0) ? 1.0 - float(-d) / float(tail + 1) : 1.0 - float(d) / float(near + 1);
        vec3 c = srcRgb(px + float(d), ry);
        acc += w * vec2(-0.1687 * c.r - 0.3313 * c.g + 0.5 * c.b, 0.5 * c.r - 0.4187 * c.g - 0.0813 * c.b);
        ws += w;
    }
    return acc / ws;
}

// Огрубление через строку: строка L = floor(row / 2) (на 1080 это 2 px). На Db-строке (L чётна) Cb берётся с этой строки,
// Cr с предыдущей; на Dr-строке наоборот.
vec2 secamChroma(float px, int row) {
    int L = row / 2;
    int Lp = max(L - 1, 0);
    vec2 now = chromaRow(px, 2 * L);
    vec2 prev = chromaRow(px, 2 * Lp);
    return ((L & 1) == 0) ? vec2(now.x, prev.y) : vec2(prev.x, now.y);
}
// Факелы по сегментам: строка L делится на сегменты по 16 px (масштаб по ширине). Для пикселя рассматриваются
// его сегмент и 3 предыдущих. В сегменте 4 пробы края e_i = |Y(x) - Y(x-2)| (x = xs + 4i), x0 = максимум, e = максимум.
// p = 0.05*fires*smoothstep(0.08, 0.3, e) + (Y0 < 0.25 ? 0.003*fires : 0) + (1-signal)*0.9; на Db-строках p втрое меньше.
// Выпадение по хешу (сегмент, линия, кадр). Факел от x0 вправо: длина 10..60 px, амплитуда 0.25..0.45,
// затухание exp(-3*(x-x0)/длина). Dr-строка добавляет Cr, Db-строка Cb.
vec2 flares(float px, int L) {
    vec2 acc = vec2(0.0);
    bool dbLine = (L & 1) == 0;
    float mul = dbLine ? (1.0 / 3.0) : 1.0;
    int rowE = 2 * L;
    float segW = floor(SECAM_SEG * uResolution.x / SC_REF + 0.5);
    float step = max(1.0, floor(segW / 4.0));
    float sIdx = floor(px / segW);
    for (int k = 0; k < SECAM_SEG_N; k++) {
        float s = sIdx - float(k);
        if (s < 0.0) break;
        float xs = s * segW;
        float e = 0.0;
        float x0 = xs;
        for (int i = 0; i < 4; i++) {
            float xi = xs + float(i) * step;
            float ei = abs(lum(srcRgb(xi, rowE)) - lum(srcRgb(xi - 2.0, rowE)));
            if (ei > e) { e = ei; x0 = xi; }
        }
        float y0 = lum(srcRgb(x0, rowE));
        float p = 0.05 * p_fires * smoothstep(0.08, 0.3, e)
                + (y0 < 0.25 ? 0.003 * p_fires : 0.0)
                + (1.0 - p_signal) * 0.9;
        p *= mul;
        uint h = ihash(uint(s) * 0x9E3779B9u ^ uint(L) * 0x85EBCA6Bu ^ uint(uFrame) * 0xC2B2AE35u);
        if (rnd(h) >= p) continue;
        float len = 10.0 + 50.0 * rnd(ihash(h ^ 0x68E31DA4u));
        float amp = 0.25 + 0.2 * rnd(ihash(h ^ 0xB5297A4Du));
        float d = px - x0;
        if (d < 0.0 || d >= len) continue;
        float a = amp * exp(-3.0 * d / len);
        if (dbLine) acc.x += a; else acc.y += a;
    }
    return clamp(acc, 0.0, 0.8);
}

void main() {
    float W = uResolution.x;
    float x = vUv.x * W;
    int row = int(floor(vUv.y * uResolution.y));
    int m = int(p_mode + 0.5);
    float sg = scale();

    vec3 col;
    if (m == 2) {
        // NTSC: гребёнка для яркости, демодуляция разности строк. Опорная фаза = фаза строки + hue + дрейф.
        float cyc = NTSC_CYC;
        float P = W / cyc;
        float y1 = combY(x, row, 2);
        float y2 = lumaLP(x, row, P / 1.25);
        float y = y1 + p_sharpness * (y1 - y2);
        float drift = NTSC_DRIFT * p_amount * smoothNoise(uTime * 0.2);
        float shift = PI * float((row + uFrame) & 1) + p_hue * PI / 180.0 + drift;
        float i = demod(x, row, cyc, shift, SIG_NARROW * sg, false, 2);
        float q = demod(x, row, cyc, shift, SIG_WIDE * sg, true, 2);
        col = vec3(y + 0.956 * i + 0.621 * q,
                   y - 0.272 * i - 0.647 * q,
                   y - 1.106 * i + 1.703 * q);
    } else if (m == 0) {
        // PAL: гребёнка по строкам n и n-2 (фаза сдвинута на π). Цветность: линия задержки, веса 1/4, 1/2, 1/4.
        float cyc = PAL_CYC;
        float P = W / cyc;
        float y1 = combY(x, row, 0);
        float y2 = lumaLP(x, row, P / 1.25);
        float y = y1 + p_sharpness * (y1 - y2);
        float uu = 0.0;
        float vv = 0.0;
        for (int k = 0; k < 3; k++) {
            int rk = max(row - k, 0);
            float sv = ((rk & 1) == 0) ? 1.0 : -1.0;
            float shift = float(rk) * PI * 0.5 + PAL_ERR;
            float a = demod(x, rk, cyc, shift, SIG_NARROW * sg, true, 0);
            float b = demod(x, rk, cyc, shift, SIG_WIDE * sg, false, 0);
            float wk = (k == 1) ? 0.5 : 0.25;
            uu += wk * a;
            vv += wk * sv * b;
        }
        col = vec3(y + 1.140 * vv,
                   y - 0.395 * uu - 0.581 * vv,
                   y + 2.032 * uu);
    } else {
        // SECAM: модель по исходнику. Яркость: ФНЧ и снег приёма; цвет: огрубление через строку, насыщенность,
        // бледнение светлых мест (как на таблице), факелы поверх.
        float px = floor(x);
        int L = row / 2;
        float y = secamLuma(px, row);
        float snow = rnd(ihash(uint(px) * 0x27D4EB2Du ^ uint(row) * 0x165667B1u ^ uint(uFrame) * 0x9E3779B9u)) - 0.5;
        y += (1.0 - p_signal) * 0.15 * snow;
        vec2 cc = secamChroma(px, row) * p_saturation;
        cc *= 1.0 - 0.35 * smoothstep(0.6, 1.0, y);
        if (p_fires > 0.0 || p_signal < 1.0) cc += flares(px, L);
        // Обратное преобразование Y, Cb, Cr в RGB (Cb = 0.564(B-Y), Cr = 0.713(R-Y)).
        col = vec3(y + 1.402 * cc.y,
                   y - 0.344 * cc.x - 0.714 * cc.y,
                   y + 1.772 * cc.x);
        // Сигналы опознавания (бутылки) сверху кадра: полоса высотой 1.4% кадра, нижний край размыт на 3.5 px (на 1080),
        // полоса дышит с периодом ~7 с и дрожит по высоте на 1–2 px. Строки чередуются: Db чуть голубее, Dr чуть желтее.
        float yTop = (1.0 - vUv.y) * uResolution.y;
        float sy = uResolution.y / 1080.0;
        float bandH = 0.014 * uResolution.y + 1.5 * sy * smoothNoise(uTime * 0.5);
        float bandMask = 1.0 - smoothstep(bandH - 3.5 * sy, bandH, yTop);
        float breath = 1.0 + 0.2 * sin(TAU * uTime / 7.0);
        float bandAmt = p_edge * 0.5 * breath * bandMask;
        bool bandDb = (row & 1) == 0;
        col += bandAmt * (bandDb ? vec3(0.0, 1.0, 0.35) : vec3(0.35, 1.0, 0.0));
    }

    col = max(col, vec3(0.0));
    vec3 src = texture(uSrc, vUv).rgb;
    vec3 outc = mix(src, col, p_amount);
    fragColor = vec4(max(outc, vec3(0.0)), 1.0);
}
