#version 330 core

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uInput;
uniform sampler2D uBloom;
uniform vec2 uResolution;
uniform float uTime;
uniform int uFrame;

uniform float p_curvature;
uniform float p_scanlines;
uniform float p_scanlinePeriod;
uniform float p_mask;
uniform float p_vignette;
uniform float p_bloom;
uniform float p_bloomRadius;
uniform float p_flicker;
uniform float p_brightness;
uniform float p_cornerRadius;

const float TAU = 6.2831853;
const float BEND = 0.6;
const float EDGE_SOFT_PX = 1.5;

void main() {
    // 1. Кривизна с поправкой на аспект. Радиальная дисторсия в физическом пространстве,
    // множитель нормирован так, что в углу он равен 1, в центре 0. При p_curvature = 0 смещение ровно 0.
    vec2 c = vUv * 2.0 - 1.0;
    float aspect = uResolution.x / max(uResolution.y, 1.0);
    vec2 p = vec2(c.x * aspect, c.y);
    float r2n = dot(p, p) / (aspect * aspect + 1.0);
    float s = p_curvature * BEND * r2n;
    vec2 uv = vUv + c * s * 0.5;

    // 2. Углы: дуги окружностей в пикселях, радиус доля высоты. Мягкий край 1.5 пикселя.
    vec2 res = uResolution;
    vec2 hs = res * 0.5;
    float R = p_cornerRadius * res.y;
    vec2 q = abs(uv * res - hs) - (hs - R);
    float sd = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - R;
    float inside = 1.0 - smoothstep(0.0, EDGE_SOFT_PX, sd);
    if (inside <= 0.0) {
        fragColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    // 3. Базовый цвет.
    vec2 suv = clamp(uv, 0.0, 1.0);
    vec3 color = texture(uInput, suv).rgb;

    // 4. Bloom: uBloom уже размыт многопроходно (bright-pass и гаусс H, V в CrtFilter).
    color += texture(uBloom, suv).rgb * p_bloom;

    // 5. Скан-линии по координате выходного пикселя. Профиль резче, яркие места засвечивают линии слабее.
    float lum = dot(color, vec3(0.299, 0.587, 0.114));
    float glowAtten = 1.0 - 0.5 * clamp(lum, 0.0, 1.0);
    float phase = 0.5 + 0.5 * cos(TAU * gl_FragCoord.y / max(p_scanlinePeriod, 1.0));
    float prof = mix(0.05, 1.0, pow(phase, 1.5));
    float scan = mix(1.0, prof, p_scanlines * glowAtten);
    color *= scan;

    // 6. Апертурная маска: вертикальные триады по 3 пикселя, среднее по столбцам сохраняется.
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

    // 7. Виньетка к краям.
    vec2 vq = uv - 0.5;
    float e = dot(vq, vq) * 4.0;
    color *= 1.0 - p_vignette * smoothstep(0.3, 1.5, e);

    // 8. Мерцание около 12 Гц с небольшой случайной составляющей по кадрам.
    float noise = fract(sin(float(uFrame) * 12.9898) * 43758.5453);
    float wob = 0.7 * (0.5 + 0.5 * sin(TAU * 12.0 * uTime)) + 0.3 * noise;
    color *= 1.0 - p_flicker * wob;

    // 9. Яркость и выход. Значения не обрезаются, обрезка будет на выводе.
    color *= p_brightness * inside;
    fragColor = vec4(color, 1.0);
}
