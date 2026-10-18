#version 330 core

// Состояние датамоша: прошлый испорченный кадр, сдвинутый блочными векторами, плюс урезанный остаток.
// Вне эпизода (uActive = 0) состояние = вход (ключевой кадр).
out vec4 fragColor;

uniform sampler2D uInput;    // вход текущего кадра
uniform sampler2D uPrevIn;   // вход прошлого кадра
uniform sampler2D uMosh;     // состояние прошлого кадра
uniform sampler2D uVec;      // векторы блоков (RG, px полного разрешения)
uniform vec2 uResolution;
uniform float uBlock;        // размер блока, px
uniform float uActive;       // 1 — эпизод идёт, 0 — кадр чистый
uniform float uLeak;         // доля нового входа там, где остаток большой

const vec3 LUMA = vec3(0.299, 0.587, 0.114);
const float RESIDUAL = 0.2;  // какая доля остатка проходит; остальное «потерял кодек»

void main() {
    ivec2 px = ivec2(gl_FragCoord.xy);
    vec3 cur = texelFetch(uInput, px, 0).rgb;
    if (uActive < 0.5) {
        fragColor = vec4(cur, 1.0);
        return;
    }
    ivec2 cell = ivec2(vec2(px) / uBlock);
    vec2 v = texelFetch(uVec, cell, 0).rg;
    vec2 q = (gl_FragCoord.xy + v) / uResolution;
    vec3 w = texture(uMosh, q).rgb;     // старая картинка, поехавшая по вектору
    vec3 s = texture(uPrevIn, q).rgb;   // прошлый вход в той же точке
    vec3 d = cur - s;                   // остаток
    vec3 m = w + RESIDUAL * d;
    // Где остаток большой, новый вход проступает долей leak.
    float big = smoothstep(0.2, 0.3, abs(dot(d, LUMA)));
    m = mix(m, cur, big * uLeak);
    fragColor = vec4(clamp(m, 0.0, 1.0), 1.0);
}
