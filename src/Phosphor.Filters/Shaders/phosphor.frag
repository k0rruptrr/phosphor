#version 330 core

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uInput;
uniform sampler2D uHistory;
uniform float uDt;

uniform float p_color;
uniform float p_persistence;
uniform float p_tint;
uniform float p_brightness;

const vec3 P1 = vec3(0.25, 1.0, 0.35);   // green P1
const vec3 P3 = vec3(1.0, 0.69, 0.0);    // amber P3
const vec3 P4 = vec3(0.92, 0.95, 1.0);   // white P4
const vec3 TRAIL_TINT = vec3(0.9, 1.0, 0.95);
const vec3 LUM_WEIGHTS = vec3(0.2126, 0.7152, 0.0722);

void main() {
    vec3 src = texture(uInput, vUv).rgb;
    float lum = dot(src, LUM_WEIGHTS);

    int idx = int(p_color + 0.5);
    vec3 phosphorColor = P1;
    if (idx == 1) {
        phosphorColor = P3;
    } else if (idx == 2) {
        phosphorColor = P4;
    }

    vec3 cur = mix(src, lum * phosphorColor, p_tint) * p_brightness;

    // Послесвечение: доля яркости прошлого кадра, не зависящая от частоты кадров.
    float decay = p_persistence > 0.0 ? pow(p_persistence, max(uDt, 0.0) * 60.0) : 0.0;
    vec3 prevC = texture(uHistory, vUv).rgb * decay * TRAIL_TINT;

    fragColor = vec4(max(cur, prevC), 1.0);
}
