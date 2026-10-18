#version 330 core

in vec2 vUv;
out vec4 fragColor;

// Расписание срезов, полоса 2 × H. Тексель (0, y) — параметры среза строки пикселей y:
//   R = e / 256 (колонка блока, с которой начинается хвост; 256 — среза нет),
//   G = kind / 8 (0 — сдвиг вектора, 1 — замёрзший кадр, 2 — тёмно-зелёный, 3 — DCT),
//   B = (dx + 128) / 256, A = (dy + 128) / 256 (сдвиг выборки, px).
// Тексель (1, y): R = tint / 8 (оттенок цветности), G = dc + 0.5 (сдвиг яркости), B = зерно среза (пер-блочные вариации).
// Срез — сплошной хвост строки от колонки e до правого края, захватывает 1–3 строки блоков подряд,
// живёт 0.3–1.5 с (своя эпоха на источник), затем «залечивается».

uniform float uBlock;   // размер макроблока, px
uniform float uCols;    // число блоков по горизонтали
uniform float uTime;    // секунды
uniform float uOrigin;  // вероятность среза на строку блоков на эпоху
uniform float uQ;       // качество сигнала (зелёный только при плохом сигнале)

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

// Источник s (строка блоков): есть ли срез в его текущей эпохе и каковы его параметры.
bool sourceAt(int s, out float e, out float kind, out float dxPx, out float dyBlk,
              out float tintI, out float dc, out float span, out float seed) {
    if (s < 0) return false;
    uint us = uint(s);
    float L = 0.3 + 1.2 * rnd3(20u, us, 0u);
    float phase = rnd3(21u, us, 0u) * L;
    uint ek = uint(max(floor((uTime + phase) / L), 0.0));
    if (rnd3(1u, us, ek) >= uOrigin) return false;
    span = 1.0 + floor(rnd3(2u, us, ek) * 3.0);
    e = floor(rnd3(3u, us, ek) * uCols);
    kind = floor(rnd3(4u, us, ek) * 4.0);
    if (kind == 2.0 && uQ < 0.45) kind = 3.0;
    float sg = rnd3(5u, us, ek) < 0.5 ? -1.0 : 1.0;
    dxPx = sg * (16.0 + 80.0 * rnd3(6u, us, ek));
    dyBlk = floor(rnd3(7u, us, ek) * 5.0) - 2.0;
    tintI = 1.0 + floor(rnd3(8u, us, ek) * 3.0);
    dc = rnd3(9u, us, ek) < 0.3 ? (rnd3(10u, us, ek) * 2.0 - 1.0) * 0.12 : 0.0;
    seed = rnd3(12u, us, ek);
    return true;
}

void main() {
    float B = uBlock;
    int by = int(floor(gl_FragCoord.y / B));

    float bestE = 256.0;   // среза нет
    float bKind = 0.0, bDx = 0.0, bDy = 0.0, bTint = 1.0, bDc = 0.0, bSeed = 0.0;
    for (int k = 0; k < 3; k++) {
        int s = by - k;   // строка-источник: эта строка или одна из двух выше (хвост тянется вниз до 3 строк)
        float e, kind, dxPx, dyBlk, tintI, dc, span, seed;
        if (!sourceAt(s, e, kind, dxPx, dyBlk, tintI, dc, span, seed)) continue;
        if (float(k) >= span) continue;
        float er = e;
        if (k > 0) er = clamp(e + floor(rnd3(11u, uint(by), uint(s)) * 5.0) - 2.0, 0.0, uCols - 1.0);
        if (er < bestE) {
            bestE = er;
            bKind = kind;
            bDx = dxPx;
            bDy = dyBlk * B;
            bTint = tintI;
            bDc = dc;
            bSeed = seed;
        }
    }

    if (floor(gl_FragCoord.x) < 0.5) {
        fragColor = vec4(bestE / 256.0, bKind / 8.0, (bDx + 128.0) / 256.0, (bDy + 128.0) / 256.0);
    } else {
        fragColor = vec4(bTint / 8.0, bDc + 0.5, bSeed, 1.0);
    }
}
