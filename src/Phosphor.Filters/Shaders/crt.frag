#version 330 core

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uInput;
uniform sampler2D uBloom;
uniform float uTime;
uniform int uFrame;

uniform float p_scanlines;
uniform float p_scanlinePeriod;
uniform float p_mask;
uniform float p_bloom;
uniform float p_bloomRadius;
uniform float p_flicker;
uniform float p_brightness;

const float TAU = 6.2831853;

void main() {
    // Геометрия кинескопа (кривизна, углы, виньетка) делает ScreenPass один раз на весь экран.
    vec2 suv = vUv;

    // 1. Базовый цвет.
    vec3 color = texture(uInput, suv).rgb;

    // 2. Bloom: uBloom уже размыт многопроходно (bright-pass и гаусс H, V в CrtFilter).
    color += texture(uBloom, suv).rgb * p_bloom;

    // 3. Скан-линии по координате выходного пикселя. Профиль резче, яркие места засвечивают линии слабее.
    float lum = dot(color, vec3(0.299, 0.587, 0.114));
    float glowAtten = 1.0 - 0.5 * clamp(lum, 0.0, 1.0);
    float phase = 0.5 + 0.5 * cos(TAU * gl_FragCoord.y / max(p_scanlinePeriod, 1.0));
    float prof = mix(0.05, 1.0, pow(phase, 1.5));
    float scan = mix(1.0, prof, p_scanlines * glowAtten);
    color *= scan;

    // 4. Апертурная маска: вертикальные триады по 3 пикселя, среднее по столбцам сохраняется.
    int col = int(mod(floor(gl_FragCoord.x), 3.0));
    vec3 tri;
    if (col == 0) {
        tri = vec3(1.0, 0.5, 0.5);
    } else if (col == 1) {
        tri = vec3(0.5, 1.0, 0.5);
    } else {
        tri = vec3(0.5, 0.5, 1.0);
    }
    vec3 maskMul = mix(vec3(1.0), tri * 1.5, p_mask);
    color *= maskMul;

    // 5. Мерцание около 12 Гц с небольшой случайной составляющей по кадрам.
    float noise = fract(sin(float(uFrame) * 12.9898) * 43758.5453);
    float wob = 0.7 * (0.5 + 0.5 * sin(TAU * 12.0 * uTime)) + 0.3 * noise;
    color *= 1.0 - p_flicker * wob;

    // 6. Яркость и выход. Значения не обрезаются, обрезка будет на выводе.
    color *= p_brightness;
    fragColor = vec4(color, 1.0);
}
