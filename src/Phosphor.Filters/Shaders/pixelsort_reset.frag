#version 330 core
// Сброс состояния: пиксель берёт новый вход, если вход заметно изменился (|ΔL| > 0.3) или состояние свежее.
out vec4 fragColor;
uniform sampler2D uInput;   // новый вход
uniform sampler2D uPrev;    // прошлое состояние (отсортированное)
uniform sampler2D uPrevIn;  // прошлый вход, alpha — его яркость
uniform int uFresh;         // 1 — взять вход целиком

float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }

void main() {
    ivec2 px = ivec2(gl_FragCoord.xy);
    vec3 cur = texelFetch(uInput, px, 0).rgb;
    float lp = texelFetch(uPrevIn, px, 0).a;
    bool change = uFresh == 1 || abs(luma(cur) - lp) > 0.3;
    vec3 st = texelFetch(uPrev, px, 0).rgb;
    fragColor = vec4(change ? cur : st, 0.0);
}
