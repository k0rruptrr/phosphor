#version 330 core
// Финальное смешивание: в зоне сортировки — отсортированное состояние, вне её и при uMix = 0 — вход без изменений.
in vec2 vUv;
out vec4 fragColor;
uniform sampler2D uInput;
uniform sampler2D uState;
uniform vec2 uResolution;
uniform float p_intensity;
uniform float p_threshold;
uniform float p_span;
uniform float p_rate;
uniform float p_dir;
uniform int uHoriz;
uniform int uZoneLo;
uniform int uZoneHi;
uniform float uMix;

void main() {
    ivec2 px = ivec2(gl_FragCoord.xy);
    vec3 a = texelFetch(uInput, px, 0).rgb;
    vec3 b = texelFetch(uState, px, 0).rgb;
    int line = uHoriz == 1 ? px.y : px.x;
    float m = (line >= uZoneLo && line < uZoneHi) ? uMix : 0.0;
    fragColor = vec4(mix(a, b, m), 1.0);
}
