#version 330 core

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uInput;
uniform sampler2D uChroma;   // размытая цветность (I в R, Q в G), из отдельных проходов; нужна при chromaRes < 400
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
uniform float p_badTracking;
uniform float p_bandSpeed;
uniform float p_iqShift;
uniform float p_chromaClip;
uniform float p_lumaRes;
uniform float p_chromaRes;

const float TAU = 6.2831853;
const float SKEW_ZONE = 0.025;    // шов головок: нижние 2.5% кадра (CRT прячет низ по углам)
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

// Мягкая обрезка одной компоненты цветности: до 0.8·lim линейно, выше — гладко к lim.
float clipComp(float v, float lim) {
    float a = abs(v);
    float knee = 0.8 * lim;
    float x = max(a - knee, 0.0) / (lim - knee);
    float soft = knee + (lim - knee) * x / (1.0 + x);
    return sign(v) * mix(a, soft, smoothstep(knee, lim, a));
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

    // Автотрекинг: эпизоды, только при tracking >= 0.25. Пауза между эпизодами от 25 с (0.25) до 6 с (1),
    // эпизод начинается в слоте со случайным сдвигом 0..0.5 с, длится 1.5–3 с. Всё от номера слота (хеш).
    float t01 = clamp((p_tracking - 0.25) / 0.75, 0.0, 1.0);
    float gap = mix(25.0, 6.0, t01);
    float period = gap + 3.5;                      // слот: пауза + максимальная длина эпизода и сдвига
    uint epi = uint(floor(tm / period));
    float ph = tm - float(epi) * period;
    float epOff = 0.5 * rnd3(epi, 21u, 0u);
    float epLen = 1.5 + 1.5 * rnd3(epi, 22u, 0u);
    float te = ph - epOff;                         // время от начала эпизода
    float f = te / epLen;                          // 0..1 внутри эпизода
    bool inEp = p_tracking >= 0.25 && te >= 0.0 && f <= 1.0;
    // Первые ~30% — полоса во всю силу; дальше «автотрекинг тянет»: полоса сужается и гаснет.
    float env = inEp ? (smoothstep(0.0, 0.03, f) * (1.0 - smoothstep(0.3, 1.0, f))) : 0.0;
    // Высота: случайная в слоте, ползёт и дрожит; иногда один короткий перескок во второй половине.
    float yBase = 0.1 + 0.8 * rnd3(epi, 25u, 0u);
    float jump = (rnd3(epi, 24u, 0u) < 0.5 && f > 0.5) ? 0.15 * (2.0 * rnd3(epi, 26u, 0u) - 1.0) : 0.0;
    float yc = yBase + 0.02 * f + 0.004 * sin(tm * 23.0) + jump;
    // Сила: tracking внутри эпизода, с «дыханием» по шуму.
    float S = p_tracking * env * (0.4 + 0.6 * vnoise(tm * 2.0, 101u));

    // Колокол e(y): полуширина зависит от tracking и сужается вместе с затуханием, край рваный по x.
    float sg = (0.03 + 0.05 * p_tracking) * (0.5 + 0.5 * env);
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

    // Шов переключения головок: нижние 2.5% кадра. Верхний край рваный по x (частый шум), дрожит каждый кадр.
    float zt = SKEW_ZONE * (0.85 + 0.3 * vnoise(vUv.x * 60.0, 33u)) + 0.0008 * (rnd3(fr, 5u, 0u) - 0.5);
    float tb = clamp(1.0 - vUv.y / zt, 0.0, 1.0);
    float inZone = step(vUv.y, zt);
    // Растянутая копия: строки зоны берут пиксели с одной-двух строк у верхней границы шва
    // (источник меняется раз в несколько кадров). Вертикальный смаз, сдвиг по x — выше.
    // Источник — самая яркая из пяти строк чуть выше верхней границы шва (средняя по 8 точкам x), один на весь кадр,
    // чтобы копия несла содержимое, а не тёмный низ кадра.
    float ySrc = 0.0;
    if (inZone > 0.5) {
        float bestL = -1.0;
        for (int k = 0; k < 5; k++) {
            float yk = 0.030 + 0.0012 * float(k);
            float lsum = 0.0;
            for (int j = 0; j < 8; j++) {
                vec3 s = texture(uInput, vec2((float(j) + 0.5) / 8.0, yk)).rgb;
                lsum += dot(s, vec3(0.299, 0.587, 0.114));
            }
            if (lsum > bestL) { bestL = lsum; ySrc = yk; }
        }
    }
    float vy = mix(vUv.y, ySrc, inZone);
    // Куски строк: ячейки 96 px по x, пара строк — один блок. В каждой ячейке свой множитель (0.25..1),
    // на границах ячеек строка рвётся: глифы надламываются резкими краями.
    uint cx = uint(floor(px.x / 96.0));
    float cm = rnd3(row >> 1u, cx, fr + 13u);
    float skew = p_bottomSkew * 45.0 * tb * (0.25 + 0.75 * cm);

    // Плохой трекинг: катящаяся полоса, сверху вниз, 8 с на проход при bandSpeed = 1.
    // yb — центр полосы (uv), a — расстояние строки до центра со знаком, в долях высоты (-0.5..0.5).
    float bt = p_badTracking;
    float yb = 1.0 - fract(tm * p_bandSpeed / 8.0);
    float a = fract(vUv.y - yb + 0.5) - 0.5;
    float da = abs(a);
    float zf = clamp(1.0 - da / 0.12, 0.0, 1.0);          // 1 в центре полосы, 0 на краю зоны (0.12)
    float bandAmp = mix(0.6, 1.0, rnd3(fr, 61u, 0u));    // дёрганье: амплитуда меняется каждый кадр
    float bandRow = 0.7 + 0.3 * rnd3(row, fr, 62u);      // и по строкам
    // Перекос: выше центра — сильный сдвиг вправо (до 60 px на 1920), ниже — слабый обратный хвост (до 10 px).
    float bandRaw = a >= 0.0 ? 60.0 * pow(zf, 4.0) : -10.0 * pow(zf, 2.0);
    float bandX = bt * (res.x / 1920.0) * bandAmp * bandRow * bandRaw;

    float D = wob + jit + bandShift + skew + bandX;
    float xg = vUv.x - D / res.x;

    // 2. Выборка: Y резкая (1 тап с rgbShift), I/Q размыты влево от точки, цвет тянется вправо.
    float rs = p_rgbShift / res.x;
    vec3 c0 = fetchRGB(xg, vy, rs);
    vec3 yiq0 = toYIQ(c0);
    float L = 10.0 * p_bleed / res.x;
    float sI = 0.0;
    float sQ = 0.0;
    float sW = 0.0;
    // Разъезд I/Q: I берётся из x + iqs, Q из x − iqs (iqShift px на 1080 по высоте). Y — из x.
    float iqs = p_iqShift * (res.y / 1080.0) / res.x;
    if (p_chromaRes < 399.5) {
        // Полоса цветности (chromaRes): даунсэмпл и гаусс сделаны отдельными проходами, здесь — только выборка.
        // Сдвиг вправо на bleed·10 px складывает bleed с размытой цветностью.
        vec2 iqb = texture(uChroma, vec2(xg - 10.0 * p_bleed / res.x, vy)).rg;
        sI = iqb.x;
        sQ = iqb.y;
        sW = 1.0;
    } else {
        for (int i = 0; i < BLEED_TAPS; i++) {
            float t = float(i) / float(BLEED_TAPS - 1);
            float w = 1.0 - 0.6 * t;
            float xt = xg - L * t;
            sI += toYIQ(fetchRGB(xt + iqs, vy, rs)).y * w;
            sQ += toYIQ(fetchRGB(xt - iqs, vy, rs)).z * w;
            sW += w;
        }
    }
    // Разрешение яркости (lumaRes): Y размыт по x, σ = 0.5·W/lumaRes. При 1920 — выключено (дельта 0).
    float dY = 0.0;
    if (p_lumaRes < 1919.5) {
        float sL = 0.5 * res.x / p_lumaRes;
        float yb = 0.0;
        float yw = 0.0;
        // 9 проб, шаг σ/2, охват ±2σ: веса exp(-i²/8)
        for (int i = -4; i <= 4; i++) {
            float w = exp(-float(i * i) / 8.0);
            yb += dot(fetchRGB(xg + float(i) * sL * 0.5 / res.x, vy, rs), vec3(0.299, 0.587, 0.114)) * w;
            yw += w;
        }
        dY = yb / yw - yiq0.x;
    }
    // Меняем цветность дельтой: при bleed = 0, iqShift = 0 и chromaRes = 400 она равна 0, цвет исходный.
    vec3 col = c0 + vec3(dY) + chromaToRGB(vec2(sI / sW - yiq0.y, sQ / sW - yiq0.z));

    // Обрезка цветности: |I|,|Q| ограничиваются порогом lim (0 — без обрезки). Меняем дельту, как выше.
    if (p_chromaClip > 0.0) {
        float lim = mix(0.6, 0.15, p_chromaClip);
        vec3 yc = toYIQ(col);
        col += chromaToRGB(vec2(clipComp(yc.y, lim), clipComp(yc.z, lim)) - yc.yz);
    }
    // Плохой трекинг: цветность в зоне полосы смешивается с шумом (шум строки + немного пиксельного).
    float kc = bt * zf * zf;
    if (kc > 0.0) {
        vec2 nIQ = 0.5 * vec2(rnd3(row, fr, 91u) - 0.5 + 0.3 * (rnd3(xi, row, fr + 92u) - 0.5),
                              rnd3(row, fr, 93u) - 0.5 + 0.3 * (rnd3(xi, row, fr + 94u) - 0.5));
        vec3 yc = toYIQ(col);
        col += chromaToRGB(kc * (nIQ - yc.yz));
    }

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

    // Плохой трекинг: шум яркости в зоне полосы и белые чёрточки в самой полосе (|a| < 0.01), ячейки 200 px.
    col += bt * zf * 0.1 * rnd3(xi, row, fr + 95u);
    if (bt > 0.0 && da < 0.01) {
        int ci2 = int(floor(px.x / 200.0));
        for (int k = 0; k < 2; k++) {
            int cc = ci2 - k;
            if (cc < 0) continue;
            uint cu = uint(cc);
            if (rnd3(row, cu, fr + 81u) < bt * 0.25) {
                float len2 = 10.0 + 50.0 * rnd3(row, cu, fr + 82u);
                float x2 = float(cu) * 200.0 + rnd3(row, cu, fr + 83u) * 200.0;
                float onSpeck = smoothstep(x2 - 1.0, x2 + 0.5, px.x) * (1.0 - smoothstep(x2 + len2 - 1.0, x2 + len2 + 1.0, px.x));
                col = mix(col, vec3(0.95), onSpeck);
            }
        }
    }

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

    // Шов головок: песок внутри зоны и тонкая светлая линия-стык над ней.
    col += inZone * (rnd3(xi, row, fr + 2000u) - 0.5) * 0.06 * p_bottomSkew;
    float seamLine = exp(-pow((vUv.y - zt - 0.0018) / 0.0010, 2.0));
    col += seamLine * 0.18 * p_bottomSkew;

    col = max(col, 0.0);                                          // снизу клампим: без отрицательных цветов
    fragColor = vec4(col, 1.0);
}
