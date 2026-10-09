#version 330 core

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uInput;
uniform vec2 uResolution;

uniform float p_amount;
uniform float p_radial;
uniform float p_angle;
uniform float p_samples;

const int MAX_SAMPLES = 8;

// Спектральный вес для точки s в [0,1]: 0 = красный, 0.5 = зелёный, 1 = синий.
// Веса нормируются по каналам в main, поэтому белое остаётся белым.
vec3 spectrum(float s) {
    float r = clamp(1.5 * (1.0 - s), 0.0, 1.0);
    float b = clamp(1.5 * s, 0.0, 1.0);
    float g = 1.0 - 0.5 * abs(2.0 * s - 1.0);
    return vec3(r, g, b);
}

void main() {
    vec4 base = texture(uInput, vUv);

    // 1. Смещение в пикселях. Радиальное: в центре ноль, к краям растёт квадратично
    // по расстоянию с поправкой на аспект (как у линзы). Равномерное: длина amount в направлении angle.
    vec2 c = vUv * 2.0 - 1.0;
    float aspect = uResolution.x / max(uResolution.y, 1.0);
    vec2 pa = vec2(c.x * aspect, c.y);
    float r2n = dot(pa, pa) / (aspect * aspect + 1.0);
    float a = radians(p_angle);
    vec2 uniformPx = vec2(cos(a), sin(a)) * p_amount;
    vec2 radialPx = c * r2n * p_amount;
    vec2 offPx = mix(uniformPx, radialPx, clamp(p_radial, 0.0, 1.0));

    // 2. Нулевое смещение — выход ровно равен входу.
    if (length(offPx) < 1e-6) {
        fragColor = base;
        return;
    }
    vec2 off = offPx / uResolution;

    int n = clamp(int(p_samples + 0.5), 1, MAX_SAMPLES);

    // 3. Один сэмпл: R в +off, G на месте, B в -off.
    if (n == 1) {
        float r = texture(uInput, vUv + off).r;
        float g = texture(uInput, vUv).g;
        float b = texture(uInput, vUv - off).b;
        fragColor = vec4(r, g, b, base.a);
        return;
    }

    // 4. Спектральное размазывание: n выборок вдоль [-off, +off], красный у +off, синий у -off.
    vec3 acc = vec3(0.0);
    vec3 wsum = vec3(0.0);
    for (int i = 0; i < MAX_SAMPLES; i++) {
        if (i >= n) break;
        float s = float(i) / float(n - 1);
        float t = 1.0 - 2.0 * s;
        vec3 w = spectrum(s);
        vec3 tex = texture(uInput, vUv + off * t).rgb;
        acc += tex * w;
        wsum += w;
    }
    vec3 col = acc / max(wsum, vec3(1e-6));
    fragColor = vec4(col, base.a);
}
