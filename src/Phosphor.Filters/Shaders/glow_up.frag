#version 330 core
// Апсэмпл tent 3x3 (углы 1, края 2, центр 4, сумма 16) с грубого уровня + взвешенный свой уровень.
// uUseCoarse = 0 — самый грубый уровень: только uWeight·skip.
in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uSkip;     // свой уровень Dk, того же размера, что выход
uniform sampler2D uCoarse;   // U(k+1), вдвое меньше
uniform vec2 uTexel;         // 1 / размер uCoarse
uniform float uWeight;       // вес уровня по radius
uniform int uUseCoarse;

void main() {
    vec3 skip = texture(uSkip, vUv).rgb * uWeight;
    vec3 up = vec3(0.0);
    if (uUseCoarse != 0) {
        vec2 t = uTexel;
        up  = texture(uCoarse, vUv + vec2(-t.x, -t.y)).rgb;
        up += texture(uCoarse, vUv + vec2( t.x, -t.y)).rgb;
        up += texture(uCoarse, vUv + vec2(-t.x,  t.y)).rgb;
        up += texture(uCoarse, vUv + vec2( t.x,  t.y)).rgb;
        up += 2.0 * texture(uCoarse, vUv + vec2(0.0, -t.y)).rgb;
        up += 2.0 * texture(uCoarse, vUv + vec2(0.0,  t.y)).rgb;
        up += 2.0 * texture(uCoarse, vUv + vec2(-t.x, 0.0)).rgb;
        up += 2.0 * texture(uCoarse, vUv + vec2( t.x, 0.0)).rgb;
        up += 4.0 * texture(uCoarse, vUv).rgb;
        up /= 16.0;
    }
    fragColor = vec4(skip + up, 1.0);
}
