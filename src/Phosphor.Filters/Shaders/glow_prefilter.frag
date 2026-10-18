#version 330 core
// Порог с мягким коленом: вес = clamp(b - t + knee)^2 / (4 knee), плюс линейная часть выше порога.
// Цвет не обесцвечиваем: умножаем на вес.
in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uInput;
uniform float uThreshold;

void main() {
    vec3 c = texture(uInput, vUv).rgb;
    float b = max(max(c.r, c.g), c.b);
    float knee = max(uThreshold * 0.5, 1e-4);
    float soft = clamp(b - uThreshold + knee, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee);
    float w = max(soft, b - uThreshold) / max(b, 1e-4);
    fragColor = vec4(c * max(w, 0.0), 1.0);
}
