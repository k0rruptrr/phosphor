#version 330 core

// Проход 1: кодирование строки в композитный сигнал.
// Выход: R = s (композит), G = D (SECAM: передаваемая разность на этой строке, для триггера факелов).

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uInput;
uniform vec2 uResolution;
uniform int uFrame;

uniform float p_mode;   // 0 = PAL, 1 = SECAM, 2 = NTSC

const float PI = 3.1415927;
const float TAU = 6.2831853;
const float SC_REF = 1920.0;          // опорная ширина: полосы и ядра масштабируются от неё
const float NTSC_CYC = 188.0;         // циклов поднесущей на активную строку (3.58 МГц * 52 мкс)
const float PAL_CYC = 230.0;          // (4.43 МГц * 52 мкс)
const float SECAM_CYC_DB = 221.0;     // (4.25 МГц * 52 мкс)
const float SECAM_CYC_DR = 229.0;     // (4.406 МГц * 52 мкс)
const float SIG_NARROW = 4.5;         // сигма узкого ядра I/U/Db, px при 1920 (полоса ~1.3 МГц)
const float SIG_WIDE = 9.0;           // сигма широкого ядра Q/V/Dr, px при 1920 (полоса ~0.4 МГц)
const float SECAM_AMIN = 0.03;         // амплитуда несущей на сером (D = 0): почти не видна
const float SECAM_KA = 0.2;           // рост амплитуды с насыщенностью: A = AMIN + KA*abs(D) (колокол)
const float SECAM_DEV = 1.2;          // девиация фазы на единицу D (рад)

// Гаусс-тап: k в [-8..8], шаг сигма/4, вес exp(-k^2/32).
float gw(int k) { return exp(-float(k * k) / 32.0); }

float lum(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }

// Цветоразностная компонента ch: 0 = I, 1 = Q, 2 = U, 3 = V, 4 = Db, 5 = Dr.
float chan(vec3 c, int ch) {
    float y = lum(c);
    if (ch == 0) return 0.596 * c.r - 0.274 * c.g - 0.322 * c.b;
    if (ch == 1) return 0.211 * c.r - 0.523 * c.g + 0.312 * c.b;
    if (ch == 2) return 0.492 * (c.b - y);
    if (ch == 3) return 0.877 * (c.r - y);
    if (ch == 4) return 1.505 * (c.b - y);
    return -1.902 * (c.r - y);
}

// Горизонтальное размытие компоненты ch гауссом сигма (px) вокруг x.
float blur(float x, float sigma, int ch) {
    float acc = 0.0;
    float ws = 0.0;
    for (int k = -8; k <= 8; k++) {
        float w = gw(k);
        float xp = x + float(k) * sigma * 0.25;
        vec3 c = texture(uInput, vec2(xp / uResolution.x, vUv.y)).rgb;
        acc += w * chan(c, ch);
        ws += w;
    }
    return acc / ws;
}

void main() {
    float W = uResolution.x;
    float x = vUv.x * W;
    float fx = x / W;                       // доля строки, 0..1
    int row = int(floor(vUv.y * uResolution.y));
    int m = int(p_mode + 0.5);
    float sc = W / SC_REF;

    vec3 c = texture(uInput, vUv).rgb;
    float y = lum(c);

    if (m == 2) {
        // NTSC: s = Y + I cos(φ) + Q sin(φ); фаза переворачивается на 180° каждую строку и кадр.
        float ph = TAU * NTSC_CYC * fx + PI * float((row + uFrame) & 1);
        float i = blur(x, SIG_NARROW * sc, 0);
        float q = blur(x, SIG_WIDE * sc, 1);
        fragColor = vec4(y + i * cos(ph) + q * sin(ph), 0.0, 0.0, 1.0);
    } else if (m == 0) {
        // PAL: s = Y + U sin(φ) ± V cos(φ); знак V чередуется по строкам, поднесущая сдвинута на 1/4 цикла на строку.
        float ph = TAU * PAL_CYC * fx + float(row) * PI * 0.5;
        float u = blur(x, SIG_NARROW * sc, 2);
        float v = blur(x, SIG_WIDE * sc, 3);
        float sv = ((row & 1) == 0) ? 1.0 : -1.0;
        fragColor = vec4(y + u * sin(ph) + sv * v * cos(ph), 0.0, 0.0, 1.0);
    } else {
        // SECAM: чётные строки несут Db, нечётные Dr; s = Y + A cos(φ0 + DEV * D), поднесущая разная для Db и Dr.
        bool even = (row & 1) == 0;
        float cyc = even ? SECAM_CYC_DB : SECAM_CYC_DR;
        float d = blur(x, SIG_NARROW * sc, even ? 4 : 5);
        float ph = TAU * cyc * fx + SECAM_DEV * d;
        float amp = SECAM_AMIN + SECAM_KA * abs(d);
        fragColor = vec4(y + amp * cos(ph), d, 0.0, 1.0);
    }
}
