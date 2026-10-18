#version 330 core
// Копия входа для следующего кадра: rgb — вход, alpha — его яркость (для сброса по |ΔL|).
out vec4 fragColor;
uniform sampler2D uInput;

float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }

void main() {
    vec3 c = texelFetch(uInput, ivec2(gl_FragCoord.xy), 0).rgb;
    fragColor = vec4(c, luma(c));
}
