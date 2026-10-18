#version 330 core
// Даунсэмпл в 2 раза: центр с весом 1/2 и четыре диагональных билинейных тапа с весом 1/8 (сумма 1).
in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uInput;
uniform vec2 uTexel;   // 1 / размер входа

void main() {
    vec3 c = texture(uInput, vUv).rgb * 0.5;
    c += (texture(uInput, vUv + vec2(-1.0, -1.0) * uTexel).rgb
        + texture(uInput, vUv + vec2( 1.0, -1.0) * uTexel).rgb
        + texture(uInput, vUv + vec2(-1.0,  1.0) * uTexel).rgb
        + texture(uInput, vUv + vec2( 1.0,  1.0) * uTexel).rgb) * 0.125;
    fragColor = vec4(c, 1.0);
}
