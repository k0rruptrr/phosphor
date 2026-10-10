#version 330 core

// Проход 2: декодирование сигнала (tv_encode.frag) как в телевизоре, смешивание с исходником по amount.
// NTSC и PAL: гребенчатый фильтр (яркость = сумма строк с противоположной фазой, цветность = разность).
// SECAM: модель по исходнику (uSrc), композит не используется: яркость с лёгким ФНЧ, цвет с асимметричным
// ФНЧ по горизонтали и огрублением через строку, факелы и снег приёма по хешу.

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uInput;     // композит (PAL/NTSC): R = s
uniform sampler2D uSrc;       // исходная картинка (SECAM читает её напрямую)
uniform vec2 uResolution;
uniform float uTime;
uniform int uFrame;

uniform float p_mode;         // 0 = PAL, 1 = SECAM, 2 = NTSC
uniform float p_amount;
uniform float p_hue;          // градусы, только NTSC (Tint)
uniform float p_sharpness;
uniform float p_saturation;   // SECAM: насыщенность цвета
uniform float p_fires;        // SECAM: количество факелов
uniform float p_fireSize;     // SECAM: длина и яркость факелов (1 = как было)
uniform float p_signal;       // SECAM: качество приёма (снег яркости, факелы от шума)
uniform float p_edge;         // SECAM: края гашения (светлая палка слева, тёмная цветная кромка справа)
uniform float p_bottles;      // SECAM: сигналы опознавания («бутылки») сверху кадра
uniform float p_ghost;        // двоение (эфир): отражённая копия правее контуров
uniform float p_ghostDelay;   // задержка отражения, доля ширины кадра
uniform float p_fibers;       // SECAM: сила «волокон» — строчного шума (1 = как на кассете)

const float PI = 3.1415927;
const float TAU = 6.2831853;
const float SC_REF = 1920.0;
const float NTSC_CYC = 188.0;
const float PAL_CYC = 230.0;
const float SIG_NARROW = 4.5;         // сигма узкого ядра, px при 1920
const float SIG_WIDE = 9.0;           // сигма широкого ядра, px при 1920
const float NTSC_DRIFT = 4.0 * PI / 180.0;   // внутренняя ошибка фазы NTSC, множитель amount
const float PAL_ERR = 5.0 * PI / 180.0;      // внутренняя ошибка фазы PAL (линия задержки гасит её в насыщенность)
const float SECAM_SEG = 16.0;         // сегмент строки для факелов, px при 1920
const float FLARE_LEN_MAX = 60.0;     // длина факела при fireSize = 1: (10 + 50·rnd) px, верхняя граница 60
const int SECAM_SEG_MAX = 64;         // предел просмотра сегментов назад (ранний выход по длине факела)
// ФНЧ цвета, px на 1280. Хвост ВЛЕВО от пикселя (d < 0, длинный): цвет из левых точек приезжает в пиксель,
// то есть цвет течёт ВПРАВО от своего источника. Короткая сторона ВПРАВО от пикселя (d > 0).
const float SECAM_CHROMA_TAIL = 12.0;
const float SECAM_CHROMA_NEAR = 4.0;

uint ihash(uint x) {
    x ^= x >> 16u;
    x *= 0x7feb352du;
    x ^= x >> 15u;
    x *= 0x846ca68bu;
    x ^= x >> 16u;
    return x;
}

float rnd(uint h) { return float(h) * (1.0 / 4294967296.0); }

// Гаусс-тап: k в [-8..8], шаг сигма/4, вес exp(-k^2/32). Таблица по |k|.
const float GWT[9] = float[9](1.0, 0.969233, 0.882497, 0.754840, 0.606531,
                              0.457833, 0.324652, 0.216265, 0.135335);
float gw(int k) { return GWT[abs(k)]; }

float scale() { return uResolution.x / SC_REF; }

// Яркость Rec.601.
float lum(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }

// Композит в строке ry (целая), позиция x в px.
float sigAt(float x, int ry) {
    return texture(uInput, vec2(x / uResolution.x, (float(ry) + 0.5) / uResolution.y)).r;
}

// Сигнал цветности для демодуляции (PAL/NTSC): m = 2 (NTSC): разность с предыдущей строкой, m = 0 (PAL): с строкой n-2.
float csig(float xp, int ry, int m) {
    if (m == 2) return 0.5 * (sigAt(xp, ry) - sigAt(xp, max(ry - 1, 0)));
    return 0.5 * (sigAt(xp, ry) - sigAt(xp, max(ry - 2, 0)));
}

// Гребенчатая яркость: сумма строк с противоположной фазой поднесущей, цветность сокращается.
float combY(float x, int row, int m) {
    int rp = (m == 2) ? max(row - 1, 0) : max(row - 2, 0);
    return 0.5 * (sigAt(x, row) + sigAt(x, rp));
}

// Низкочастотный фильтр яркости (гаусс сигма).
float lumaLP(float x, int ry, float sigma) {
    float acc = 0.0;
    float ws = 0.0;
    for (int k = -8; k <= 8; k++) {
        float w = gw(k);
        acc += w * sigAt(x + float(k) * sigma * 0.25, ry);
        ws += w;
    }
    return acc / ws;
}

// Демодуляция: 2 * LP(c * cos или sin опорной фазы). Опорная фаза в тапах k идёт с постоянным шагом,
// поэтому cos/sin считаются вращением фазора (два sin/cos на пиксель вместо 17).
float demod(float x, int ry, float cyc, float shift, float sigma, bool useSin, int m) {
    float W = uResolution.x;
    float dl = TAU * cyc * sigma * 0.25 / W;
    float a0 = TAU * cyc * x / W + shift - 8.0 * dl;
    vec2 z = vec2(cos(a0), sin(a0));
    vec2 st = vec2(cos(dl), sin(dl));
    float acc = 0.0;
    float ws = 0.0;
    for (int k = -8; k <= 8; k++) {
        float w = gw(k);
        float xp = x + float(k) * sigma * 0.25;
        float r = useSin ? z.y : z.x;
        acc += w * csig(xp, ry, m) * r;
        ws += w;
        z = vec2(z.x * st.x - z.y * st.y, z.x * st.y + z.y * st.x);
    }
    return 2.0 * acc / ws;
}

// Шум времени: гладкая интерполяция целочисленных значений в [-1, 1].
float hash1(int i) { return float(ihash(uint(i))) * (2.0 / 4294967296.0) - 1.0; }
float smoothNoise(float t) {
    float i = floor(t);
    float f = t - i;
    f = f * f * (3.0 - 2.0 * f);
    return mix(hash1(int(i)), hash1(int(i) + 1), f);
}

// Фаза строчного шума: своя на строку, новая каждый кадр (аналоговый шум меняется с каждым полем).
float rowPh(int r) { return rnd(ihash(uint(r) * 0x9E3779B9u ^ 0x51ED27u)) * 97.0 + float(uFrame & 4095) * 13.7; }

// ---------- SECAM (по исходнику) ----------

// Исходный цвет в пикселе (px, ry); координаты зажаты в кадр.
vec3 srcRgb(float px, int ry) {
    float xc = clamp(px, 0.0, uResolution.x - 1.0);
    float yc = clamp(float(ry), 0.0, uResolution.y - 1.0);
    return texture(uSrc, vec2((xc + 0.5) / uResolution.x, (yc + 0.5) / uResolution.y)).rgb;
}

// Яркость SECAM: лёгкий ФНЧ 0.25 / 0.5 / 0.25 по горизонтали.
float secamLuma(float px, int row) {
    return 0.25 * lum(srcRgb(px - 1.0, row)) + 0.5 * lum(srcRgb(px, row))
         + 0.25 * lum(srcRgb(px + 1.0, row));
}

// Цветность (Cb, Cr) строки ry, ФНЧ по горизонтали: треугольное ядро, хвост влево 12 px, вправо 4 px (на 1280).
// Левый (длинный) хвост даёт растекание цвета ВПРАВО от источника.
vec2 chromaRow(float px, int ry) {
    float s = uResolution.x / 1280.0;
    int tail = int(SECAM_CHROMA_TAIL * s + 0.5);
    int near = int(SECAM_CHROMA_NEAR * s + 0.5);
    vec2 acc = vec2(0.0);
    float ws = 0.0;
    for (int d = -tail; d <= near; d++) {
        float w = (d < 0) ? 1.0 - float(-d) / float(tail + 1) : 1.0 - float(d) / float(near + 1);
        vec3 c = srcRgb(px + float(d), ry);
        acc += w * vec2(-0.1687 * c.r - 0.3313 * c.g + 0.5 * c.b, 0.5 * c.r - 0.4187 * c.g - 0.0813 * c.b);
        ws += w;
    }
    return acc / ws;
}

// Огрубление через строку: строка L = floor(row / 2) (на 1080 это 2 px). На Db-строке (L чётна) Cb берётся с этой строки,
// Cr с предыдущей; на Dr-строке наоборот.
vec2 secamChroma(float px, int row) {
    int L = row / 2;
    int Lp = max(L - 1, 0);
    vec2 now = chromaRow(px, 2 * L);
    vec2 prev = chromaRow(px, 2 * Lp);
    return ((L & 1) == 0) ? vec2(now.x, prev.y) : vec2(prev.x, now.y);
}
// Факелы по сегментам: строка L делится на сегменты по 16 px (масштаб по ширине). Для пикселя рассматриваются
// его сегмент и предыдущие, пока сегмент не дальше максимальной длины факела (px - x0 >= k*segW, поиск кончен).
// В сегменте 4 пробы края e_i = |Y(x) - Y(x-2)| (x = xs + 4i), x0 = максимум, e = максимум.
// p = 0.05*fires*smoothstep(0.08, 0.3, e) + (Y0 < 0.25 ? 0.003*fires : 0) + (1-signal)*0.9; на Db-строках p втрое меньше.
// Выпадение по хешу (сегмент, линия, кадр). Факел от x0 вправо: длина (10..60 px)·fireSize, амплитуда
// (0.25..0.45)·(0.8 + 0.2·fireSize), затухание exp(-3*(x-x0)/длина). Dr-строка добавляет Cr, Db-строка Cb.
vec2 flares(float px, int L) {
    vec2 acc = vec2(0.0);
    bool dbLine = (L & 1) == 0;
    float mul = dbLine ? (1.0 / 3.0) : 1.0;
    int rowE = 2 * L;
    float segW = floor(SECAM_SEG * uResolution.x / SC_REF + 0.5);
    float step = max(1.0, floor(segW / 4.0));
    float sIdx = floor(px / segW);
    float lenMax = 3.0 * FLARE_LEN_MAX * p_fireSize;   // тёмные штрихи до ×3 длиннее
    for (int k = 0; k < SECAM_SEG_MAX; k++) {
        if (float(k) * segW >= lenMax) break;
        float s = sIdx - float(k);
        if (s < 0.0) break;
        float xs = s * segW;
        float e = 0.0;
        float x0 = xs;
        for (int i = 0; i < 4; i++) {
            float xi = xs + float(i) * step;
            float ei = abs(lum(srcRgb(xi, rowE)) - lum(srcRgb(xi - 2.0, rowE)));
            if (ei > e) { e = ei; x0 = xi; }
        }
        float y0 = lum(srcRgb(x0, rowE));
        float d = px - x0;
        uint h = ihash(uint(s) * 0x9E3779B9u ^ uint(L) * 0x85EBCA6Bu ^ uint(uFrame) * 0xC2B2AE35u);
        // Три члена вероятности, один бросок rnd(h) делит их по накопленным порогам. Каждый со своими правилами:
        // (1) контур: 0.05·fires·smoothstep(e), знак + (Dr → Cr+, Db → Cb+), Db ×1/3, длина ×fireSize;
        // (2) слабый сигнал: (1 − signal)·0.9, знак +, Db ×1/3, длина ×fireSize (как до T4);
        // (3) тёмный: (y0 < 0.25) ? 0.003·fires : 0, канал по строке, случайный знак, без ×1/3, длина ×3·fireSize.
        float pC = 0.05 * p_fires * smoothstep(0.08, 0.3, e) * mul;
        // Шум слабого сигнала: под ползунком «факелы» (0 — нет), квадрат — лёгкая просадка приёма почти не сыплет
        float pS = (1.0 - p_signal) * (1.0 - p_signal) * 0.9 * p_fires * mul;
        float pD = (y0 < 0.25 ? 0.003 * p_fires : 0.0);
        float r = rnd(h);
        if (r < pC + pS + pD) {
            bool dark = r >= pC + pS;
            float len = (10.0 + 50.0 * rnd(ihash(h ^ 0x68E31DA4u))) * p_fireSize;
            float amp = (0.25 + 0.2 * rnd(ihash(h ^ 0xB5297A4Du))) * (0.8 + 0.2 * p_fireSize);
            if (dark) {
                len *= 3.0;
                amp = (0.08 + 0.17 * rnd(ihash(h ^ 0xB5297A4Du))) * (0.8 + 0.2 * p_fireSize);
            }
            if (d >= 0.0 && d < len) {
                float a = amp * exp(-3.0 * d / len);
                if (dark) {
                    float sgn = ((ihash(h ^ 0x2545F491u) & 1u) == 0u) ? 1.0 : -1.0;
                    if (dbLine) acc.x += a * sgn; else acc.y += a * sgn;
                } else if (dbLine) acc.x += a; else acc.y += a;
            }
        }
    }
    return clamp(acc, -0.8, 0.8);
}

// Бутылки сигналов опознавания: хеш номера бутылки задаёт длину и промежутки (постоянны во времени).
float bottleRnd(int i, uint salt) { return rnd(ihash(uint(i + 1) * 0x9E3779B9u ^ salt)); }

// Бутылки полосы в точке xf (доля ширины кадра): возвращает (яркость, толщина). Фон между ними — 4% яркости тела.
vec2 bottleAt(float xf) {
    int nB = 3 + int(bottleRnd(7, 0x5Cu) * 3.0);     // 3..5 бутылок
    float cur = 0.03;
    for (int i = 0; i < 5; i++) {
        if (i >= nB) break;
        float len = 0.14 + 0.12 * bottleRnd(i, 0xA1u);  // длина 14–26% ширины
        float u = (xf - cur) / len;
        if (u >= 0.0 && u < 1.0) {
            // подъём 10%, тело, горлышко последние 30% (яркость и толщина падают), плавный спад в конце
            float env = smoothstep(0.0, 0.10, u) * (1.0 - smoothstep(0.92, 1.0, u));
            float neckT = smoothstep(0.70, 1.0, u);
            return vec2(max(0.04, env * mix(1.0, 0.5, neckT)), mix(1.0, 0.35, neckT));
        }
        cur += len + 0.06 + 0.06 * bottleRnd(i, 0xB7u);  // промежуток 6–12%
    }
    return vec2(0.04, 1.0);
}

// Эхо: blur3 по x исходника в точке xp, цветность ×0.5 (смесь с яркостью).
vec3 echoAt(float xp, float v) {
    vec3 acc = vec3(0.0);
    for (int k = -1; k <= 1; k++) {
        float xc = clamp(xp + float(k), 0.0, uResolution.x - 1.0);
        acc += texture(uSrc, vec2((xc + 0.5) / uResolution.x, v)).rgb;
    }
    vec3 m = acc / 3.0;
    return mix(vec3(lum(m)), m, 0.5);
}

void main() {
    float W = uResolution.x;
    float x = vUv.x * W;
    int row = int(floor(vUv.y * uResolution.y));
    int m = int(p_mode + 0.5);
    float sg = scale();

    vec3 col;
    if (m == 2) {
        // NTSC: гребёнка для яркости, демодуляция разности строк. Опорная фаза = фаза строки + hue + дрейф.
        float cyc = NTSC_CYC;
        float P = W / cyc;
        float y1 = combY(x, row, 2);
        float y2 = lumaLP(x, row, P / 1.25);
        float y = y1 + p_sharpness * (y1 - y2);
        float drift = NTSC_DRIFT * p_amount * smoothNoise(uTime * 0.2);
        float shift = PI * float((row + uFrame) & 1) + p_hue * PI / 180.0 + drift;
        float i = demod(x, row, cyc, shift, SIG_NARROW * sg, false, 2);
        float q = demod(x, row, cyc, shift, SIG_WIDE * sg, true, 2);
        col = vec3(y + 0.956 * i + 0.621 * q,
                   y - 0.272 * i - 0.647 * q,
                   y - 1.106 * i + 1.703 * q);
    } else if (m == 0) {
        // PAL: гребёнка по строкам n и n-2 (фаза сдвинута на π). Цветность: линия задержки, веса 1/4, 1/2, 1/4.
        float cyc = PAL_CYC;
        float P = W / cyc;
        float y1 = combY(x, row, 0);
        float y2 = lumaLP(x, row, P / 1.25);
        float y = y1 + p_sharpness * (y1 - y2);
        float uu = 0.0;
        float vv = 0.0;
        for (int k = 0; k < 3; k++) {
            int rk = max(row - k, 0);
            float sv = ((rk & 1) == 0) ? 1.0 : -1.0;
            float shift = float(rk) * PI * 0.5 + PAL_ERR;
            float a = demod(x, rk, cyc, shift, SIG_NARROW * sg, true, 0);
            float b = demod(x, rk, cyc, shift, SIG_WIDE * sg, false, 0);
            float wk = (k == 1) ? 0.5 : 0.25;
            uu += wk * a;
            vv += wk * sv * b;
        }
        col = vec3(y + 1.140 * vv,
                   y - 0.395 * uu - 0.581 * vv,
                   y + 2.032 * uu);
    } else {
        // SECAM: модель по исходнику. Яркость: ФНЧ и снег приёма; цвет: огрубление через строку, насыщенность,
        // бледнение светлых мест (как на таблице), факелы поверх.
        float px = floor(x);
        int L = row / 2;
        float y = secamLuma(px, row);
        float snow = rnd(ihash(uint(px) * 0x27D4EB2Du ^ uint(row) * 0x165667B1u ^ uint(uFrame) * 0x9E3779B9u)) - 0.5;
        y += (1.0 - p_signal) * 0.15 * snow;
        // Строчный шум («волокна»): вдоль строки ~200 px, независим между строками; на тёмном поле ещё и цветной.
        float fib = smoothNoise(px / 200.0 + rowPh(row));
        float fib2 = smoothNoise(px / 200.0 + rowPh(row) + 50.0);
        float dk = 1.0 - smoothstep(0.1, 0.2, y);   // тёмные места (luma < 0.2)
        // Амплитуда волокон зависит от цвета поля: зелёный ×1.6, красный ×1.0, синий ×0.6 (плавно по долям каналов).
        vec3 sc = srcRgb(px, row);
        float tot = max(sc.r + sc.g + sc.b, 0.05);
        float kf = 1.0 + 0.6 * sc.g / tot - 0.4 * sc.b / tot;
        y += p_fibers * kf * (0.025 * fib + dk * 0.02 * fib2);
        // Редкие выпадения: в среднем 1.5 на кадр, белые чёрточки 8–30 px.
        uint dh = ihash(uint(row) * 0x85EBCA6Bu ^ uint(uFrame) * 0xC2B2AE35u ^ 0x3B9AC9u);
        if (rnd(dh) < 1.5 / uResolution.y) {
            float dx0 = rnd(ihash(dh ^ 0x68E31DA4u)) * W;
            float dlen = 8.0 + 22.0 * rnd(ihash(dh ^ 0xB5297A4Du));
            if (px >= dx0 && px < dx0 + dlen) y += 0.6;
        }
        vec2 cc = secamChroma(px, row) * p_saturation;
        cc *= 1.0 - 0.35 * smoothstep(0.6, 1.0, y);
        cc += dk * 0.03 * vec2(-0.6, -1.0) * (0.5 + 0.5 * fib2);   // тёмное поле: зеленоватый сдвиг цветности
        if (p_fires > 0.0) cc += flares(px, L);
        // Обратное преобразование Y, Cb, Cr в RGB (Cb = 0.564(B-Y), Cr = 0.713(R-Y)).
        col = vec3(y + 1.402 * cc.y,
                   y - 0.344 * cc.x - 0.714 * cc.y,
                   y + 1.772 * cc.x);
        // Тёмное поле: зеленоватый подъём чёрного. Хвост цвета за ярким текстом: зелёно-жёлтый мазок вправо, затухает
        // (только там, где справа темнее, чем слева; на ровном поле нуля нет).
        col += dk * vec3(0.015, 0.045, 0.025);
        float l0 = lum(srcRgb(px, row));
        float sm = 0.0;
        float sw = 0.0;
        for (int k = 1; k <= 8; k++) {
            float dd = float(k) * 25.0 * sg;
            float w = exp(-dd / (80.0 * sg));
            sm += w * max(0.0, lum(srcRgb(px - dd, row)) - l0);
            sw += w;
        }
        // Хвост = сдвиг цветности в противоположную сторону от фона (на чёрном — зеленоватый базовый сдвиг).
        // Хвост относится к семейству факелов: включён тем же условием (fires/signal).
        if (p_fires > 0.0 || p_signal < 1.0) {
            // Цветность фона, повёрнутая по оттенку на −120° (cos = −0.5, sin = −0.866), плюс небольшой зелёный базис.
            vec2 rc = vec2(-0.5 * cc.x + 0.8660254 * cc.y, -0.8660254 * cc.x - 0.5 * cc.y);
            vec2 tail = 0.5 * (sm / sw) * (rc + vec2(-0.05, -0.08));
            col += vec3(1.402 * tail.y, -0.344 * tail.x - 0.714 * tail.y, 1.772 * tail.x);
        }
        // Края гашения (управляет p_edge; gate = clamp(2·edge, 0, 1), так что edge = 0 — выкл).
        // Левая палка: светлая линия ~1.5% ширины, ядро 0.25% ширины, рваная (сдвиг строки ±3 px, быстро по времени),
        // блоки по 6 строк пропадают, блоки по 8 строк — ярче (0.9). Правая кромка: последние 1% ширины тёмная, рваная ±2 px,
        // в ней короткие цветные чёрточки 0.15–0.35 (синие, зелёные, пурпурные).
        float gEdge = clamp(p_edge * 2.0, 0.0, 1.0);
        uint tk = uint(uFrame);
        float jitL = 3.0 * smoothNoise(float(row) * 0.7 + uTime * 4.0);
        float dLft = abs(px + 0.5 - (0.015 * W + jitL));
        float hw = 0.00125 * W;
        float prof = 1.0 - smoothstep(hw - 0.4, hw + 2.6, dLft);
        float gapOn = step(0.12, rnd(ihash(uint(row / 6) * 0x9E3779B9u ^ tk * 0x85EBCA6Bu ^ 0x7A3D1Fu)));
        float brightBlk = step(0.88, rnd(ihash(uint(row / 8) * 0x27D4EB2Du ^ tk * 0x165667B1u ^ 0x1B873593u)));
        float lineAmt = gEdge * prof * gapOn * mix(0.55, 0.9, brightBlk);
        col += vec3(lineAmt);
        float edgeR = W * 0.99 + 2.0 * smoothNoise(float(row) * 0.8 + uTime * 4.0);
        uint rh = ihash(uint(row) * 0x9E3779B9u ^ tk * 0x85EBCA6Bu ^ 0x51ED27u);
        float dashOn = step(0.65, rnd(rh));
        float ds = W * (0.975 + 0.02 * rnd(ihash(rh ^ 0x68E31DA4u)));
        float dl = 6.0 + 14.0 * rnd(ihash(rh ^ 0xB5297A4Du));
        float inD = step(ds, x) * step(x, ds + dl);
        float cr3 = rnd(ihash(rh ^ 0x2545F491u));
        vec3 dc = cr3 < 0.33 ? vec3(0.2, 0.45, 1.0) : (cr3 < 0.66 ? vec3(0.2, 1.0, 0.3) : vec3(0.9, 0.2, 0.9));
        col += gEdge * dashOn * inD * (0.15 + 0.2 * rnd(ihash(rh ^ 0x1B873593u))) * dc;
        col = mix(col, vec3(0.01, 0.015, 0.01), gEdge * smoothstep(edgeR - 0.5, edgeR + 0.5, x));
        // Сигналы опознавания (бутылки) сверху кадра (управляет p_bottles): полоса высотой 2.8% кадра, 3–5 бутылок по ширине.
        // Строка дрожит по x (строчная синхронизация), яркость строки ×(0.85..1.15). Нижний край размыт на 3.5 px (на 1080),
        // полоса дышит с периодом ~7 с и дрожит по высоте на 1–2 px. Строки чередуются: Db и Dr с малым контрастом.
        float yTop = (1.0 - vUv.y) * uResolution.y;
        float sy = uResolution.y / 1080.0;
        float bandH = 0.028 * uResolution.y + 1.5 * sy * smoothNoise(uTime * 0.5);
        float jit = 0.006 * W * smoothNoise(float(row) * 0.8 + uTime * 0.7);
        vec2 bp = bottleAt((x - jit) / W);
        float rowB = 1.0 + 0.15 * smoothNoise(float(row) * 0.8 + 31.7);
        float ht = bandH * bp.y;
        float bandMask = 1.0 - smoothstep(ht - 3.5 * sy * bp.y, ht, yTop);
        float breath = 1.0 + 0.2 * sin(TAU * uTime / 7.0);
        float bandAmt = p_bottles * 0.6 * breath * rowB * bp.x * bandMask;
        bool bandDb = (row & 1) == 0;
        col += bandAmt * (bandDb ? vec3(0.0875, 1.0, 0.2625) : vec3(0.2625, 1.0, 0.0875));
    }

    col = max(col, vec3(0.0));
    vec3 src = texture(uSrc, vUv).rgb;
    vec3 outc = mix(src, col, p_amount);
    // Двоение (эфир): отражённая копия правее контуров. Эхо берётся с исходника (blur3), цветность эха ×0.5,
    // второе эхо слабое и тёмное. a = ghost·0.35·(1 + 0.2·sin(2πt/11)), всё ×amount.
    float ga = p_ghost * 0.35 * (1.0 + 0.2 * sin(TAU * uTime / 11.0)) * p_amount;
    if (ga > 0.0) {
        float dG = p_ghostDelay * W;
        outc = outc * (1.0 - 0.5 * ga) + ga * (echoAt(x - dG, vUv.y) - 0.4 * echoAt(x - 2.3 * dG, vUv.y));
    }
    fragColor = vec4(max(outc, vec3(0.0)), 1.0);
}
