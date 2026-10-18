#version 330 core

// Поиск векторов, шаг 1: SAD яркости блока против смещённого прошлого кадра. Один фрагмент — одна пара
// (блок, кандидат). Раскладка: texel (cx, cy·49 + c), c — номер кандидата (7×7 смещений по 4 px, ±12 px).
out vec4 fragColor;

uniform sampler2D uQCur;    // яркость четверти, текущий кадр
uniform sampler2D uQPrev;   // яркость четверти, прошлый кадр
uniform ivec2 uQSize;       // размер четверти, texel
uniform ivec2 uGrid;        // число блоков по x и y для текущего размера блока
uniform int uBlock;         // размер блока, px (кратен 4)

const int SIDE = 7;         // смещений по одной оси
const int SEARCH_Q = 3;     // ±3 texel четверти = ±12 px
const int NCAND = 49;

void main() {
    ivec2 f = ivec2(gl_FragCoord.xy);
    int cy = f.y / NCAND;
    int c = f.y - cy * NCAND;
    if (f.x >= uGrid.x || cy >= uGrid.y) {
        fragColor = vec4(0.0);
        return;
    }
    int K = uBlock / 4;            // блок в texel четверти
    ivec2 base = ivec2(f.x, cy) * K;
    ivec2 qmax = uQSize - 1;
    int k = c % SIDE - SEARCH_Q;
    int l = c / SIDE - SEARCH_Q;
    float sad = 0.0;
    for (int j = 0; j < K; j++)
    for (int i = 0; i < K; i++) {
        ivec2 a = min(base + ivec2(i, j), qmax);
        ivec2 b = clamp(base + ivec2(i + k, j + l), ivec2(0), qmax);
        sad += abs(texelFetch(uQCur, a, 0).r - texelFetch(uQPrev, b, 0).r);
    }
    fragColor = vec4(sad, 0.0, 0.0, 1.0);
}
