#version 330 core
// Композит свечения: out = src + (1 - exp(-add)) * (1 - src), add = core·L1 + strength·Halo.
// Множитель (1 - src) не даёт ядру уйти выше 1.0; при add = 0 выход совпадает со входом.
in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uInput;
uniform sampler2D uCore;   // L1: узкое ядро, уровень 1/4 (без накопления)
uniform sampler2D uHalo;   // накопленный ореол, уровень 1/4

uniform float p_strength;
uniform float p_core;
uniform float p_saturation;

void main() {
    vec3 src = texture(uInput, vUv).rgb;
    vec3 core = texture(uCore, vUv).rgb * p_core;

    // Насыщенность меняем только у ореола: ядро остаётся таким, каким его дал порог.
    vec3 halo = texture(uHalo, vUv).rgb;
    float lum = dot(halo, vec3(0.299, 0.587, 0.114));
    halo = max(mix(vec3(lum), halo, p_saturation), vec3(0.0)) * p_strength;

    // Тонмап одним скаляром по максимальному каналу: отношение каналов (оттенок) сохраняется,
    // ядро светлеет, но не уходит в жёлтый, как было бы при поканальном exp.
    vec3 m = core + halo;
    float mx = max(max(m.r, m.g), m.b);
    float s = 1.0 - exp(-mx);
    vec3 g = mx > 1e-6 ? m * (s / mx) : vec3(0.0);
    fragColor = vec4(src + g * (1.0 - src), 1.0);
}
