#version 330 core

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uInput;
uniform sampler2D uHist;    // выход прошлого кадра (история-RT): замёрзшие области и заморозка
uniform sampler2D uSlice;   // расписание срезов, полоса 2 × H (см. dvb_slice.frag)
uniform vec2 uResolution;

uniform float p_severity;
uniform float p_rate;
uniform float p_block;
uniform float p_freeze;

uniform float uQ;          // качество сигнала эпизода: severity × огибающая, 0 вне эпизода
uniform float uEpoch;      // полусекундный номер (полосы сильного срыва)
uniform float uFreezeOn;   // 1 — кадр замёрз: выход = история
uniform float uRefresh;    // 0..1: доля обновлённых блоков после заморозки
uniform float uBrk;        // сильный срыв 0..1
uniform float uHistOk;     // история валидна (после первого кадра)

const float PI = 3.14159265;

// Целочисленный хеш, как в vhs.frag.
uint ihash(uint x) {
    x ^= x >> 16u;
    x *= 0x7feb352du;
    x ^= x >> 15u;
    x *= 0x846ca68bu;
    x ^= x >> 16u;
    return x;
}

float rnd3(uint a, uint b, uint c) {
    uint h = ihash(a ^ ihash((b * 0x9E3779B9u) ^ ihash(c)));
    return float(h) * (1.0 / 4294967296.0);
}

// Четыре отсчёта во вторых четвертях квадрата [b0, b0 + s] (координаты пикселей, низ — 0).
void quarterTaps(vec2 b0, float s, out vec3 t00, out vec3 t10, out vec3 t01, out vec3 t11) {
    t00 = texture(uInput, (b0 + vec2(0.25, 0.25) * s) / uResolution).rgb;
    t10 = texture(uInput, (b0 + vec2(0.75, 0.25) * s) / uResolution).rgb;
    t01 = texture(uInput, (b0 + vec2(0.25, 0.75) * s) / uResolution).rgb;
    t11 = texture(uInput, (b0 + vec2(0.75, 0.75) * s) / uResolution).rgb;
}

// Средний цвет блока s × s.
vec3 blockMean(vec2 b0, float s) {
    vec3 t00, t10, t01, t11;
    quarterTaps(b0, s, t00, t10, t01, t11);
    return (t00 + t10 + t01 + t11) * 0.25;
}

// Низкий битрейт: блок s × s — ступенчатое среднее плюс наклон (первый DCT-коэффициент).
vec3 quantCell(vec2 px, float s, float step) {
    vec2 b0 = floor(px / s) * s;
    vec3 t00, t10, t01, t11;
    quarterTaps(b0, s, t00, t10, t01, t11);
    vec3 mean = (t00 + t10 + t01 + t11) * 0.25;
    vec3 gx = t10 + t11 - t00 - t01;
    vec3 gy = t01 + t11 - t00 - t10;
    mean = floor(mean / step + 0.5) * step;
    vec2 uv = (px - b0) / s - 0.5;
    return clamp(mean + gx * uv.x + gy * uv.y, 0.0, 1.0);
}

void main() {
    vec2 px = gl_FragCoord.xy;
    vec3 src = texture(uInput, vUv).rgb;

    // Вне эпизода (и без заморозки/срыва) кадр проходит как есть.
    if (uQ <= 0.0 && uFreezeOn < 0.5 && uRefresh >= 1.0 && uBrk <= 0.0) {
        fragColor = vec4(src, 1.0);
        return;
    }

    float B = floor(p_block + 0.5);
    vec3 col = src;

    if (uQ > 0.0) {
        // 1. Блочность низкого битрейта: сетка 8 × 8, вес растёт с качеством сигнала.
        // Ступени заметные: шаг среднего растёт с качеством, полный вес уже при качестве 0.5 и ниже.
        float qs = 0.03 + 0.1 * uQ;
        col = mix(src, quantCell(px, 8.0, qs), clamp(uQ * 2.0, 0.0, 1.0));

        // 2. Срез: сплошной хвост строки от колонки e до правого края. Параметры — из расписания по строке.
        vec4 t0 = texelFetch(uSlice, ivec2(0, int(floor(px.y))), 0);
        vec4 t1 = texelFetch(uSlice, ivec2(1, int(floor(px.y))), 0);
        float e = floor(t0.r * 256.0 + 0.5);
        float kind = floor(t0.g * 8.0 + 0.5);
        vec2 off = vec2(t0.b * 256.0 - 128.0, t0.a * 256.0 - 128.0);
        float tintI = floor(t1.r * 8.0 + 0.5);
        float dc = t1.g - 0.5;
        float bxf = floor(px.x / B);
        float byf = floor(px.y / B);
        // Мозаика хвоста: у каждого блока свой вектор (общий сдвиг среза ± 0/1 блок) и свой DC-сдвиг яркости.
        uint bidT = uint(bxf) + 4096u * uint(byf);
        uint seedT = uint(t1.b * 4096.0);
        float addB = floor(rnd3(30u, bidT, seedT) * 3.0) - 1.0;
        float dcSign = rnd3(31u, bidT, seedT) < 0.5 ? -1.0 : 1.0;
        float dcBlk = dcSign * (0.03 + 0.05 * rnd3(32u, bidT, seedT));

        if (bxf >= e) {
            vec3 err;
            vec2 offB = vec2(off.x + addB * B, off.y);
            vec2 sp = (px + offB) / uResolution;
            if (kind < 0.5) {
                // сдвиг вектора: кусок картинки «не того места», содержимое движется вместе со входом
                err = texture(uInput, sp).rgb;
            } else if (kind < 1.5) {
                // замёрзший кусок прошлого кадра: стоит на месте экрана, пока сцена движется
                err = uHistOk > 0.5 ? texture(uHist, px / uResolution).rgb : texture(uInput, sp).rgb;
            } else if (kind < 2.5) {
                // нулевые данные: тёмно-зелёный хвост с едва заметной сеткой 8 × 8; без цветности и DC
                float lx = mod(px.x, 8.0);
                float ly = mod(px.y, 8.0);
                float tex = 0.015 * cos(PI * (lx + 0.5) / 8.0) * cos(PI * (ly + 0.5) / 8.0);
                err = vec3(0.0, 0.38 + tex, 0.0);
                tintI = 0.0;
                dc = 0.0;
            } else {
                // DCT-узор: плавные косинусные полосы 8 × 8 (u, v = 1..3) поверх среднего цвета блока
                vec2 b0 = vec2(bxf, floor(px.y / B)) * B;
                vec3 mean = blockMean(b0, B);
                float kx = 1.0 + mod(abs(floor(off.x)), 3.0);
                float ky = 1.0 + mod(abs(floor(off.y)), 3.0);
                float lx = mod(px.x, 8.0);
                float ly = mod(px.y, 8.0);
                float bas = cos(PI * (lx + 0.5) * kx / 8.0) * cos(PI * (ly + 0.5) * ky / 8.0);
                err = clamp(mean + 0.2 * bas, 0.0, 1.0);
            }
            // цветность хвоста (сдвиг U/V): розовый, зелёный или фиолетовый оттенок
            if (tintI > 0.5) {
                vec3 tint = tintI < 1.5 ? vec3(0.15, -0.05, 0.12)
                          : (tintI < 2.5 ? vec3(-0.1, 0.12, -0.1) : vec3(0.08, -0.08, 0.16));
                err += tint;
            }
            // DC-сдвиг блока: у зелёного хвоста нулевые данные, без сдвигов
            float blkDc = (kind > 1.5 && kind < 2.5) ? 0.0 : dcBlk;
            col = err + vec3(dc + blkDc);
        }
    }

    // 3. Заморозка: весь кадр — история. После заморозки блоки догружаются случайными кусками.
    if (uFreezeOn > 0.5 && uHistOk > 0.5) {
        col = texture(uHist, vUv).rgb;
    } else if (uRefresh < 1.0 && uHistOk > 0.5) {
        uint rb = uint(floor(px.x / B)) + 4096u * uint(floor(px.y / B));
        if (rnd3(4u, rb, 0u) > uRefresh) col = texture(uHist, vUv).rgb;
    }

    // 4. Сильный срыв: почти чёрный кадр с горизонтальными полосами.
    if (uBrk > 0.0) {
        float band = step(0.8, rnd3(3u, uint(px.y / 4.0), uint(uEpoch)));
        vec3 dark = vec3(0.02, 0.02, 0.03) + band * vec3(0.35, 0.35, 0.4);
        col = mix(col, dark, clamp(uBrk, 0.0, 1.0) * 0.9);
    }

    col = max(col, 0.0);
    fragColor = vec4(col, 1.0);
}
