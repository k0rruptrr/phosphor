#version 330 core

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uInput;
uniform vec2 uDir;

const float W0 = 0.2270270;
const float W1 = 0.1945946;
const float W2 = 0.1216216;
const float W3 = 0.0540540;
const float W4 = 0.0162162;

void main() {
    vec3 acc = texture(uInput, vUv).rgb * W0;
    acc += (texture(uInput, vUv + uDir * 1.0).rgb + texture(uInput, vUv - uDir * 1.0).rgb) * W1;
    acc += (texture(uInput, vUv + uDir * 2.0).rgb + texture(uInput, vUv - uDir * 2.0).rgb) * W2;
    acc += (texture(uInput, vUv + uDir * 3.0).rgb + texture(uInput, vUv - uDir * 3.0).rgb) * W3;
    acc += (texture(uInput, vUv + uDir * 4.0).rgb + texture(uInput, vUv - uDir * 4.0).rgb) * W4;
    fragColor = vec4(acc, 1.0);
}
