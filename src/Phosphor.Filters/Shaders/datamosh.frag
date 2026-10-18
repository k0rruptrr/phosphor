#version 330 core

// Выход датамоша: вход, смешанный с состоянием по огибающей эпизода (uAmt = 0 — чистый вход).
out vec4 fragColor;

uniform sampler2D uInput;
uniform sampler2D uMosh;
uniform float uAmt;

void main() {
    ivec2 px = ivec2(gl_FragCoord.xy);
    vec3 cur = texelFetch(uInput, px, 0).rgb;
    vec3 m = texelFetch(uMosh, px, 0).rgb;
    fragColor = vec4(mix(cur, m, uAmt), 1.0);
}
