#version 330 core

// Проход 2: декодирование сигнала (tv_encode.frag) как в телевизоре, смешивание с исходником по amount.
// NTSC и PAL: гребенчатый фильтр (яркость = сумма строк с противоположной фазой, цветность = разность).
// SECAM: гребёнки нет, узкий ФНЧ яркости.

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uInput;     // композит: R = s, G = D (SECAM)
uniform sampler2D uSrc;       // исходная картинка
uniform vec2 uResolution;
uniform float uTime;
uniform int uFrame;

uniform float p_mode;         // 0 = PAL, 1 = SECAM, 2 = NTSC
uniform float p_amount;
uniform float p_hue;          // градусы, только NTSC (Tint)
uniform float p_sharpness;

const float PI = 3.1415927;
const float TAU = 6.2831853;
const float SC_REF = 1920.0;
const float NTSC_CYC = 188.0;
const float PAL_CYC = 230.0;
const float SECAM_CYC_DB = 221.0;
const float SECAM_CYC_DR = 229.0;
const float SIG_NARROW = 4.5;
const float SIG_WIDE = 9.0;
const float SECAM_LUMA_NOTCH = 0.135;        // сигма ФНЧ яркости SECAM в долях периода поднесущей (ядро ~0.6 периода)
const float NTSC_DRIFT = 4.0 * PI / 180.0;   // внутренняя ошибка фазы NTSC, множитель amount
const float PAL_ERR = 5.0 * PI / 180.0;      // внутренняя ошибка фазы PAL (линия задержки гасит её в насыщенность)
const float SECAM_DEV = 1.2;
const float SECAM_AMIN = 0.03;             // амплитуда на сером (как в tv_encode)
const float SECAM_KEY_LO = 1.2 * SECAM_AMIN; // color killer: ниже этой огибающей цветность гаснет
const float SECAM_KEY_HI = 2.0 * SECAM_AMIN;

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

// Композит в строке ry (целая), позиция x в px.
float sigAt(float x, int ry) {
    return texture(uInput, vec2(x / uResolution.x, (float(ry) + 0.5) / uResolution.y)).r;
}
float dAt(float x, int ry) {
    return texture(uInput, vec2(x / uResolution.x, (float(ry) + 0.5) / uResolution.y)).g;
}

// Сигнал цветности для демодуляции: m = 2 (NTSC): разность с предыдущей строкой, m = 0 (PAL): с строкой n-2,
// m = 1 (SECAM): сам композит. Разность гасит яркость и даёт s_n - s_{n-1} = 2C (фаза противоположна).
float csig(float xp, int ry, int m) {
    if (m == 2) return 0.5 * (sigAt(xp, ry) - sigAt(xp, max(ry - 1, 0)));
    if (m == 0) return 0.5 * (sigAt(xp, ry) - sigAt(xp, max(ry - 2, 0)));
    return sigAt(xp, ry);
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

// SECAM: D строки r = девиация фазы относительно несущей этой строки (Db или Dr).
// Color killer: огибающая sqrt(C^2+S^2) = A(D); ниже порога цветность плавно гаснет.
float secamD(float x, int r) {
    bool ev = (r & 1) == 0;
    float cyc = ev ? SECAM_CYC_DB : SECAM_CYC_DR;
    float sg = SIG_WIDE * scale();
    float c = demod(x, r, cyc, 0.0, sg, false, 1);
    float s = demod(x, r, cyc, 0.0, sg, true, 1);
    // c = A cos θ, s = -A sin θ
    float env = length(vec2(c, s));
    float key = smoothstep(SECAM_KEY_LO, SECAM_KEY_HI, env);
    return key * atan(-s, c) / SECAM_DEV;
}

// SECAM: (Db, Dr). Чётная строка даёт Db, нечётная Dr; вторая компонента берётся с соседней строки.
vec2 secamChroma(float x, int row) {
    int rp = max(row - 1, 0);
    float own = secamD(x, row);
    float prev = secamD(x, rp);
    return ((row & 1) == 0) ? vec2(own, prev) : vec2(prev, own);
}

// SECAM-факелы: резкий край D слева от точки даёт цветной отрезок вправо, 10..60 px, с затуханием.
vec2 fire(float x, int row) {
    for (int d = 1; d <= 60; d++) {
        float xe = x - float(d);
        if (xe < 2.0) break;
        float e = abs(dAt(xe + 1.0, row) - dAt(xe - 1.0, row));
        if (e > 0.05) {
            uint h = ihash(uint(row) * 0x9E3779B9u ^ uint(uFrame) * 0x85EBCA6Bu ^ uint(xe));
            float r1 = rnd(h);
            float r2 = rnd(ihash(h ^ 0x68E31DA4u));
            float r3 = rnd(ihash(h ^ 0xB5297A4Du));
            float len = 10.0 + 50.0 * r2;
            if (r1 < 0.6 && float(d) < len) {
                float w = exp(-float(d) / (len * 0.5)) * (0.4 + 0.6 * r3);
                return w * secamChroma(xe - 4.0, row);
            }
            break;
        }
    }
    return vec2(0.0);
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
        // Ошибка фазы одинакова для всех строк: V-перетечка уходит в знаки и взаимно гасится.
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
        // SECAM: узкий ФНЧ яркости (ядро ~0.6 периода, без гребёнки), цвет из двух соседних строк, плюс факелы.
        bool even = (row & 1) == 0;
        float cyc = even ? SECAM_CYC_DB : SECAM_CYC_DR;
        float P = W / cyc;
        float y1 = lumaLP(x, row, P * SECAM_LUMA_NOTCH);
        float y2 = lumaLP(x, row, P / 1.25);
        float y = y1 + p_sharpness * (y1 - y2);
        vec2 dc = secamChroma(x, row) + fire(x, row);
        float b = y + dc.x / 1.505;
        float r = y - dc.y / 1.902;
        float g = (y - 0.299 * r - 0.114 * b) / 0.587;
        // Выход вне гаммы: цветность уменьшается к яркости, яркость сохраняется (клип по каналам её поднимал бы).
        vec3 cs = vec3(r, g, b) - vec3(y);
        float k = 1.0;
        for (int i = 0; i < 3; i++) {
            if (y + cs[i] < 0.0) k = min(k, max(y, 0.0) / max(-cs[i], 1e-6));
            if (y + cs[i] > 1.0) k = min(k, max(1.0 - y, 0.0) / max(cs[i], 1e-6));
        }
        col = vec3(y) + k * cs;
    }

    col = max(col, vec3(0.0));
    vec3 src = texture(uSrc, vUv).rgb;
    vec3 outc = mix(src, col, p_amount);
    fragColor = vec4(max(outc, vec3(0.0)), 1.0);
}
