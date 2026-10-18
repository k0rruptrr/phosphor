#version 330 core
// Один шаг префиксного максимума по alpha (radix-4): x(i) = max(x(i), x(i−s), x(i−2s), x(i−3s)).
// После шагов с s = 1, 4, 16, ... в alpha — индекс последнего начала отрезка не правее пикселя.
// Для пикселя внутри яркого отрезка это начало его отрезка.
out vec4 fragColor;
uniform sampler2D uPrev;
uniform int uHoriz;
uniform int uStep;

void main() {
    ivec2 px = ivec2(gl_FragCoord.xy);
    vec4 self = texelFetch(uPrev, px, 0);
    float v = self.a;
    int i = uHoriz == 1 ? px.x : px.y;
    for (int t = 1; t <= 3; t++) {
        int j = i - t * uStep;
        if (j < 0) break;
        ivec2 pj = uHoriz == 1 ? ivec2(px.x - t * uStep, px.y) : ivec2(px.x, px.y - t * uStep);
        v = max(v, texelFetch(uPrev, pj, 0).a);
    }
    fragColor = vec4(self.rgb, v);
}
