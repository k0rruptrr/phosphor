#version 330 core

// Яркость входа в четверти разрешения (R). Каждый фрагмент — один texel четверти, 2×2 отсчёта из 16 пикселей.
out vec4 fragColor;

uniform sampler2D uInput;
uniform ivec2 uRes;   // полный размер входа, px

const vec3 LUMA = vec3(0.299, 0.587, 0.114);

void main() {
    ivec2 q = ivec2(gl_FragCoord.xy);
    ivec2 maxT = uRes - 1;
    float acc = 0.0;
    for (int j = 0; j < 2; j++)
    for (int i = 0; i < 2; i++) {
        ivec2 t = min(q * 4 + ivec2(1 + 2 * i, 1 + 2 * j), maxT);
        acc += dot(texelFetch(uInput, t, 0).rgb, LUMA);
    }
    fragColor = vec4(acc * 0.25, 0.0, 0.0, 1.0);
}
