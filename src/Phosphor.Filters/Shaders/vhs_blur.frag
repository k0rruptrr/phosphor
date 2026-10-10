#version 330 core

// Сепарабельный гаусс для полосы цветности VHS: 13 проб, шаг uStep = σ/3, охват ±2σ.
// Веса exp(-i²/18) для i = −6..6. Вызывается дважды: по x и по y (uStep задаёт направление).

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uInput;
uniform vec2 uStep;   // шаг одной пробы, в uv

void main() {
    vec4 acc = vec4(0.0);
    float wsum = 0.0;
    for (int i = -6; i <= 6; i++) {
        float w = exp(-float(i * i) / 18.0);
        acc += texture(uInput, vUv + float(i) * uStep) * w;
        wsum += w;
    }
    fragColor = acc / wsum;
}
