#version 330 core
// Один проход odd-even transposition sort. Пара (i, i+1) сравнивается в фазе uPhase.
// Пара участвует, если обе яркости не ниже uLo, строка (столбец) лежит в зоне от uZoneLo до uZoneHi и оба пикселя в одном куске:
// кусок — (i − начало отрезка) / uSpan, начало отрезка лежит в alpha (граница маски). Обмен только внутри пары.
out vec4 fragColor;
uniform sampler2D uPrev;
uniform int uHoriz;    // 1 — сортируем по x (строки), 0 — по y (столбцы)
uniform int uDesc;     // 0 — яркость растёт с индексом, 1 — падает
uniform int uPhase;    // чётность пары: 0 или 1
uniform int uLen;      // длина оси сортировки в пикселях
uniform int uSpan;     // длина куска в пикселях
uniform float uLo;     // порог яркости
uniform int uZoneLo;   // зона сортировки по перпендикулярной оси, от uZoneLo до uZoneHi
uniform int uZoneHi;

float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }

void main() {
    ivec2 px = ivec2(gl_FragCoord.xy);
    vec4 self = texelFetch(uPrev, px, 0);
    fragColor = self;
    int i = uHoriz == 1 ? px.x : px.y;
    int line = uHoriz == 1 ? px.y : px.x;
    int j = ((i & 1) == uPhase) ? i + 1 : i - 1;
    if (j < 0 || j >= uLen) return;
    if (line < uZoneLo || line >= uZoneHi) return;
    ivec2 pj = uHoriz == 1 ? ivec2(j, line) : ivec2(line, j);
    vec4 other = texelFetch(uPrev, pj, 0);
    bool selfLeft = i < j;
    vec3 L = selfLeft ? self.rgb : other.rgb;
    vec3 R = selfLeft ? other.rgb : self.rgb;
    float lL = luma(L), lR = luma(R);
    if (lL < uLo || lR < uLo) return;
    // Оба пикселя ярче порога и соседи, значит в одном отрезке; начало отрезка — у них общее.
    int s = int(self.a + 0.5);
    if ((i - s) / uSpan != (j - s) / uSpan) return;
    bool swap = uDesc == 0 ? (lL > lR) : (lL < lR);
    if (swap) fragColor = vec4(other.rgb, self.a);
}
