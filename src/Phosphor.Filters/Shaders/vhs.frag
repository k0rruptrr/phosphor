#version 330 core

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uInput;
uniform vec2 uResolution;
uniform float uTime;
uniform int uFrame;

uniform float p_jitter;
uniform float p_tracking;
uniform float p_bleed;
uniform float p_noise;
uniform float p_rgbShift;
uniform float p_bottomSkew;
uniform float p_wobble;
uniform float p_saturation;
uniform float p_dropouts;

const float TAU = 6.2831853;
const float SKEW_ZONE = 0.04;     // нижние 4% кадра
const float EVENT_SLOT = 15.0;    // слот редкого прохода полосы, с (событие в его начале, 0..5 с)
const float STREAK_CELL = 160.0;  // ширина ячейки горизонтальных штрихов, px
const int BLEED_TAPS = 10;

// Целочисленный хеш, без внешних текстур.
uint ihash(uint x) {
    x ^= x >> 16u;
    x *= 0x7feb352du;
    x ^= x >> 15u;
    x *= 0x846ca68bu;
    x ^= x >> 16u;
    return x;
}

// Равномерный шум от 0 до почти 1, от трёх целых.
float rnd3(uint a, uint b, uint c) {
    uint h = ihash(a ^ ihash((b * 0x9E3779B9u) ^ ihash(c)));
    return float(h) * (1.0 / 4294967296.0);
}

// Гладкий шум по одной оси (value noise), 0..1, с сидом. x >= 0.
float vnoise(float x, uint seed) {
    float i = floor(x);
    float f = x - i;
    float u = f * f * (3.0 - 2.0 * f);
    uint ii = uint(max(i, 0.0));
    return mix(rnd3(seed, ii, 0u), rnd3(seed, ii + 1u, 0u), u);
}

// Выборка с горизонтальным сдвигом RGB: красный вправо на s, синий влево на s (s в uv).
vec3 fetchRGB(float x, float y, float s) {
    return vec3(texture(uInput, vec2(x - s, y)).r,
                texture(uInput, vec2(x, y)).g,
                texture(uInput, vec2(x + s, y)).b);
}

// RGB -> YIQ (x = Y, y = I, z = Q).
vec3 toYIQ(vec3 c) {
    return vec3(dot(c, vec3(0.299, 0.587, 0.114)),
                dot(c, vec3(0.596, -0.274, -0.322)),
                dot(c, vec3(0.211, -0.523, 0.312)));
}

// Дельта цветности YIQ (Y = 0) -> RGB.
vec3 chromaToRGB(vec2 iq) {
    return vec3(0.956 * iq.x + 0.621 * iq.y,
               -0.272 * iq.x - 0.647 * iq.y,
               -1.106 * iq.x + 1.703 * iq.y);
}

void main() {
    vec2 res = uResolution;
    vec2 px = vUv * res;                       // пиксели, (0,0) — левый низ
    uint row = uint(floor(px.y));
    uint grp = uint(floor(px.y * 0.5));        // группы по 2 строки для дрожания
    uint xi = uint(floor(px.x));
    uint fr = uint(max(uFrame, 0));
    float tm = max(uTime, 0.0);

    // 1. Геометрия. D — смещение в пикселях, + = вправо (содержимое уезжает вправо).
    float wob = p_wobble * 3.0 * (0.6 * sin(uTime * 0.9 + vUv.y * TAU * 1.3)
                                + 0.4 * sin(uTime * 0.37 + vUv.y * TAU * 0.4 + 1.7));
    float jit = p_jitter * floor(rnd3(grp, fr, 11u) * 4.0);     // 0..3 px × jitter

    // Полоса трекинга.
    // Сила дышит во времени: от 0.4 до 1 от tracking.
    float S = p_tracking * (0.4 + 0.6 * vnoise(tm * 0.2, 101u));
    // Покой: нижние ~15–25% кадра, медленный дрейф по гладкому шуму.
    float yRest = 0.16 + 0.04 * (2.0 * vnoise(tm * 0.05, 102u) - 1.0);
    // Редкий проход: слот 15 с, событие в его начале (0..5 с), длится 1–2 с. Интервал 10–20 с.
    uint slot = uint(floor(tm / EVENT_SLOT));
    float tSlot = tm - float(slot) * EVENT_SLOT;
    float evStart = 5.0 * rnd3(slot, 1u, 0u);
    float evLen = 1.0 + rnd3(slot, 2u, 0u);
    float p = (tSlot - evStart) / evLen;
    float evE = smoothstep(0.0, 0.05, p) * (1.0 - smoothstep(0.9, 1.0, p));
    float sweepY = mix(yRest, 1.1, smoothstep(0.0, 1.0, clamp(p, 0.0, 1.0)));
    float yc = mix(yRest, sweepY, evE);

    // Колокол e(y): полуширина зависит от tracking, край рваный по x (масштаб ~ 1/8 ширины).
    float sg = 0.03 + 0.05 * p_tracking;
    float ragged = vnoise(vUv.x * 8.0 + tm * 0.15, 31u) - 0.5;
    float sgx = sg * (0.75 + 0.5 * vnoise(vUv.x * 6.0 + tm * 0.1, 32u));
    float dy = vUv.y - yc - sg * 0.8 * ragged;
    float e = exp(-(dy / sgx) * (dy / sgx));
    // Колокол без рваного края: плотность штрихов (не зависит от x, чтобы строка была целой).
    float dRow = (vUv.y - yc) / sg;
    float eRow = exp(-dRow * dRow);

    // Сдвиг строк: когерентные группы по 4 строки, максимум в центре полосы.
    uint bg = uint(floor(px.y * 0.25));
    float ng = 2.0 * vnoise(tm * 2.5, bg + 1000u) - 1.0;
    float bandShift = e * 30.0 * S * ng;

    float tb = clamp(1.0 - vUv.y / SKEW_ZONE, 0.0, 1.0);
    float skew = p_bottomSkew * 40.0 * tb * (0.6 + 0.4 * rnd3(row, fr, 13u));

    float D = wob + jit + bandShift + skew;
    float xg = vUv.x - D / res.x;

    // 2. Выборка: Y резкая (1 тап с rgbShift), I/Q размыты влево от точки, цвет тянется вправо.
    float rs = p_rgbShift / res.x;
    vec3 c0 = fetchRGB(xg, vUv.y, rs);
    vec3 yiq0 = toYIQ(c0);
    float L = 10.0 * p_bleed / res.x;
    float sI = 0.0;
    float sQ = 0.0;
    float sW = 0.0;
    for (int i = 0; i < BLEED_TAPS; i++) {
        float t = float(i) / float(BLEED_TAPS - 1);
        float w = 1.0 - 0.6 * t;
        vec3 q = toYIQ(fetchRGB(xg - L * t, vUv.y, rs));
        sI += q.y * w;
        sQ += q.z * w;
        sW += w;
    }
    // Меняем только цветность: при bleed = 0 дельта равна 0, цвет остаётся исходным.
    vec3 col = c0 + chromaToRGB(vec2(sI / sW - yiq0.y, sQ / sW - yiq0.z));

    // 3. Цвет: насыщенность (1 = без изменений).
    float lum = dot(col, vec3(0.299, 0.587, 0.114));
    col = mix(vec3(lum), col, p_saturation);

    // Внутри полосы картинка теряет цвет, слегка сереет и светлеет. bk = интенсивность полосы в пикселе.
    float bk = e * S;
    float lum2 = dot(col, vec3(0.299, 0.587, 0.114));
    col = mix(col, vec3(lum2), 0.5 * bk);
    col += bk * 0.15;

    // Горизонтальный шум-хвост: выборка по x с шагом 45 px с интерполяцией, по y построчно.
    float tu = px.x / 45.0;
    float ti = floor(tu);
    float tf = tu - ti;
    uint tii = uint(ti);
    float tv = mix(rnd3(row, tii, fr + 100u), rnd3(row, tii + 1u, fr + 100u), tf * tf * (3.0 - 2.0 * tf));
    col += bk * (tv - 0.5) * 0.25;

    // Светлые горизонтальные штрихи: ячейки по x, живут 1–2 кадра, плотность ∝ e.
    // Отрезок до 300 px может начаться в одной из трёх предыдущих ячеек (160 px каждая).
    uint ci = uint(floor(px.x / STREAK_CELL));
    uint ep = fr / 2u;
    float streak = 0.0;
    for (int k = 0; k < 3; k++) {
        int cI = int(ci) - k;
        if (cI < 0) continue;
        uint c = uint(cI);
        if (rnd3(row, c, ep * 4u) < 0.4 * eRow) {
            float len = 20.0 + 280.0 * pow(rnd3(row, c, ep * 4u + 1u), 2.0);
            float x0 = float(c) * STREAK_CELL + rnd3(row, c, ep * 4u + 2u) * STREAK_CELL;
            float b = 0.30 + 0.50 * rnd3(row, c, ep * 4u + 3u);
            streak += b * smoothstep(x0 - 2.0, x0 + 1.0, px.x) * (1.0 - smoothstep(x0 + len - 1.0, x0 + len + 2.0, px.x));
        }
    }
    col += streak * bk;

    // 4. Шум, выпадения.
    float g = rnd3(xi, row, fr + 1000u);                          // зерно, каждый кадр, в основном осветляет
    col += (g - 0.35) * p_noise * 0.3;

    float drop = 0.0;
    if (p_dropouts > 0.0) {
        uint g3 = uint(floor(px.y / 3.0));                        // выпадение = группа из 3 строк
        uint ep2 = fr / 2u;                                       // событие живёт 1–2 кадра
        float ev = rnd3(g3, 7919u, ep2);
        if (ev < p_dropouts * 0.0045) {                           // 3 строки × 0.0015: та же плотность на строку
            float x0 = rnd3(g3, 1u, ep2) * res.x;
            float len = 20.0 + rnd3(g3, 2u, ep2) * 180.0;        // 20–200 px
            float t = (px.x - x0) / len;                          // 0..1 по длине
            drop = step(0.0, t) * step(t, 1.0) * sqrt(sin(3.14159265 * clamp(t, 0.0, 1.0)));  // мягкие концы
        }
    }
    col = mix(col, vec3(0.92), 0.9 * drop);

    col = max(col, 0.0);                                          // снизу клампим: без отрицательных цветов
    fragColor = vec4(col, 1.0);
}
