#version 330 core

// Копия входа без изменений: запоминание прошлого кадра для остатка.
out vec4 fragColor;

uniform sampler2D uInput;

void main() {
    fragColor = texelFetch(uInput, ivec2(gl_FragCoord.xy), 0);
}
