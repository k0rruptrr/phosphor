#version 330 core

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uInput;

const float THRESHOLD = 0.25;

void main() {
    vec3 c = texture(uInput, vUv).rgb;
    fragColor = vec4(max(c - THRESHOLD, vec3(0.0)), 1.0);
}
