#version 330 core

// Поиск векторов, шаг 2: минимум по 49 кандидатам из texel SAD (datamosh_sad.frag). Один фрагмент — один блок.
// Результат: RG = (dx, dy) в пикселях полного разрешения; на ничьей — меньший вектор.
out vec4 fragColor;

uniform sampler2D uSad;     // SAD: texel (cx, cy·49 + c)
uniform ivec2 uGrid;        // число блоков по x и y для текущего размера блока

const int SIDE = 7;
const int SEARCH_Q = 3;
const int NCAND = 49;
const float PENALTY = 0.0005;   // штраф за длину вектора (тай-брейк на плоских местах)

void main() {
    ivec2 cell = ivec2(gl_FragCoord.xy);
    if (cell.x >= uGrid.x || cell.y >= uGrid.y) {
        fragColor = vec4(0.0);
        return;
    }
    float best = 1e9;
    ivec2 bestV = ivec2(0);
    for (int c = 0; c < NCAND; c++) {
        int k = c % SIDE - SEARCH_Q;
        int l = c / SIDE - SEARCH_Q;
        float cost = texelFetch(uSad, ivec2(cell.x, cell.y * NCAND + c), 0).r + PENALTY * float(abs(k) + abs(l));
        if (cost < best) {
            best = cost;
            bestV = ivec2(k, l);
        }
    }
    fragColor = vec4(vec2(bestV * 4), 0.0, 1.0);
}
