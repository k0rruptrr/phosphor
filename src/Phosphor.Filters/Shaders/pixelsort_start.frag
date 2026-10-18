#version 330 core
// Начало отрезка: для пикселя яркого (>= uLo) и стоящего первым после тёмного (или у края) в alpha пишется его индекс,
// иначе −1. Дальше pixelsort_scan превращает это в «начало текущего отрезка» для каждого пикселя.
out vec4 fragColor;
uniform sampler2D uPrev;
uniform int uHoriz;   // 1 — ось x, 0 — ось y
uniform float uLo;

float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }

void main() {
    ivec2 px = ivec2(gl_FragCoord.xy);
    vec4 self = texelFetch(uPrev, px, 0);
    int i = uHoriz == 1 ? px.x : px.y;
    float v = -1.0;
    if (luma(self.rgb) >= uLo) {
        if (i == 0) {
            v = 0.0;
        } else {
            ivec2 pp = uHoriz == 1 ? ivec2(px.x - 1, px.y) : ivec2(px.x, px.y - 1);
            if (luma(texelFetch(uPrev, pp, 0).rgb) < uLo) v = float(i);
        }
    }
    fragColor = vec4(self.rgb, v);
}
