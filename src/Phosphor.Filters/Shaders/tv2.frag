#version 330 core
// tv2 — PAL / SECAM «по вкусу», а не по физике. Перенесено из шейдера, который написал пользователю Claude Opus в другом чате;
// генератор настроечной таблицы заменён входной картинкой. Модель:
//  - 288 видимых строк на поле, 312 всего (24 строки гашения видны только при прокрутке кадра roll);
//  - горизонтальная единица — 1/720 ширины строки;
//  - SECAM: Db/Dr через строку, недостающая берётся из прошлой строки (линия задержки), каузальный ФНЧ цветности
//    с перерегулированием «быстрый минус медленный» → факелы вправо от контуров; поднесущая ЧМ протекает в яркость;
//  - PAL: симметричный ФНЧ цветности, переключение V по строкам, усреднение с линией задержки (PAL-D),
//    перекрёстные искажения яркость↔цветность (cross-color, dot crawl).

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uInput;
uniform float uTime;
uniform int uFrame;

uniform float p_mode;     // 0 = SECAM, 1 = PAL
uniform float p_amount;   // A: сила артефактов (полоса, факелы, перекрёстные искажения)
uniform float p_noise;    // N: помехи приёма («рыбки» SECAM, шум цветности PAL, снег)
uniform float p_fault;    // F: неисправность — SECAM: перепутана опознавание Db/Dr; PAL: PAL-S + ошибка фазы → «жалюзи»
uniform float p_roll;     // RL: прокрутка кадра в строках (видно гашение: бутылки SECAM, VITS, телетекст)
uniform float p_blanking; // > 0.5: 24 строки кадрового гашения (КГИ) видны сверху; иначе картинка на весь кадр
uniform float p_bottles;  // > 0.5: в КГИ SECAM видны сигналы опознавания («бутылки»)
uniform float p_vits;     // > 0.5: в КГИ испытательные строки I–IV (ГОСТ 18471, VITS), телетекст сдвигается ниже

float h(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

// Источник: u — (x, y) с y сверху вниз, как в оригинале
vec3 src(vec2 u) { return texture(uInput, vec2(clamp(u.x, 0.0, 1.0), clamp(1.0 - u.y, 0.0, 1.0))).rgb; }

// RGB → (Y, B−Y, R−Y)
vec3 yc(vec3 c) { float y = dot(c, vec3(0.299, 0.587, 0.114)); return vec3(y, c.b - y, c.r - y); }

// «Рыбки» ЧМ-шума SECAM: короткие горизонтальные штрихи цветности на строке L
float fish(float L, float x) {
    float s = x * 26.0 + h(vec2(L, 3.1)) * 9.0;
    float g = floor(s);
    float r = h(vec2(g, L + float(uFrame % 4096) * 17.0));
    if (r < 1.0 - 0.12 * p_noise) return 0.0;
    return (h(vec2(g + 5.0, L + uTime)) - 0.5) * 1.4 * p_noise * sin(fract(s) * 3.1416);
}

// ---- Испытательные строки I–IV (VITS). ГОСТ 7845-92 ставит их в строки 17, 18, 330, 331, форму задаёт ГОСТ 18471:
// сетка H/32 (1 деление = 2 мкс от фронта синхроимпульса), уровни — доли белого (0,7 В). На экране поля чередуются,
// поэтому сверху вниз идут 17 (I), 330 (III), 18 (II), 331 (IV) — у нас это строки КГИ 17–20 подряд.

float win(float t, float a, float b, float r) { return smoothstep(a - r, a + r, t) * (1.0 - smoothstep(b - r, b + r, t)); }
float stp(float t, float a) { return smoothstep(a - 0.15, a + 0.15, t); }

// Строка k (0 — I, 1 — III, 2 — II, 3 — IV) в момент t, мкс: x — яркостная часть, y — полуразмах пакета, z — его частота, МГц
vec3 vitsSig(int k, float t) {
    if (k <= 1) {
        float lum = win(t, 12.0, 22.0, 0.1);                                        // B2: белая площадка
        float d = t - 26.0;
        if (abs(d) < 0.2) lum += pow(cos(3.1416 * d / 0.4), 2.0);                  // B1: импульс 2T (200 нс)
        float st = 0.2 * (stp(t, 40.0) + stp(t, 44.0) + stp(t, 48.0) + stp(t, 52.0) + stp(t, 56.0));
        if (k == 0) {
            float e = t - 32.0;
            float p = abs(e) < 2.0 ? 0.5 * pow(cos(3.1416 * e / 4.0), 2.0) : 0.0;   // F: импульс 20T с поднесущей
            return vec3(lum + p + st * (1.0 - stp(t, 62.0)), p, 4.43);              // D1: лесенка, 5 ступеней по 0,14 В
        }
        float on = win(t, 30.0, 60.0, 0.5);
        return vec3(lum + st * on, 0.2 * on, 4.43);                                 // D2: лесенка с поднесущей 0,28 В
    }
    float ped = 0.5 * win(t, 20.0, 62.0, 0.1);                                      // B5: пьедестал 0,35 В
    if (k == 2) {
        float lum = ped + 0.8 * win(t, 12.0, 16.0, 0.1) + 0.2 * win(t, 16.0, 20.0, 0.1); // C1: опорные уровни
        if (t < 24.0 || t >= 60.0) return vec3(lum, 0.0, 1.0);
        int i = int((t - 24.0) / 6.0);
        float fr[6] = float[6](0.5, 1.0, 2.0, 4.0, 4.8, 5.8);                       // C2: пакеты частот, размах 0,42 В
        float a0 = 24.0 + 6.0 * float(i);
        return vec3(lum, 0.3 * win(t, a0 + 0.3, a0 + 5.7, 0.15), fr[i]);
    }
    float g = win(t, 14.0, 28.0, 0.5), e = win(t, 34.0, 60.0, 0.5);
    return vec3(0.5 * win(t, 12.0, 62.0, 0.1), 0.5 * g + 0.3 * e, 4.43);            // G: поднесущая 0,7 В; E: 0,42 В
}

// SECAM: частотный детектор строки k (Db или Dr) с ограничителем и перерегулированием каузального ФНЧ → факелы
float secamDet(int k, float t, bool db) {
    if (k < 0 || k > 3) return 0.0;
    float sl = 0.0, fs = 0.0, ws = 0.0, wf = 0.0;
    for (int i = 0; i < 14; i++) {
        float fi = float(i);
        vec3 q = vitsSig(k, t - fi * 0.144);
        float lim = smoothstep(0.015, 0.05, q.y * exp(-pow((q.z - 4.33) / 0.9, 2.0)));   // нет сигнала в полосе — нет цвета
        float d = clamp(db ? (q.z - 4.25) / 0.23 : (q.z - 4.406) / 0.28, -1.6, 1.6) * lim;
        float w1 = exp(-fi / (0.5 + 5.0 * p_amount)), w2 = exp(-fi / (0.3 + 1.2 * p_amount));
        sl += w1 * d; ws += w1; fs += w2 * d; wf += w2;
    }
    sl /= ws; fs /= wf;
    float D = sl + (fs - sl) * 2.2 * p_amount;
    return db ? D / 1.505 : -D / 1.902;                                             // D'b = 1,505 (B−Y), D'r = −1,902 (R−Y)
}

// PAL: синхронный детектор строки k; пакеты рядом с 4,43 МГц дают биения — радужный cross-color
vec2 palDet(int k, float t, float sw) {
    if (k < 0 || k > 3) return vec2(0.0);
    vec3 q = vitsSig(k, t);
    float df = q.z - 4.43;
    float a = q.y * exp(-pow(df / 1.1, 2.0)) * mix(p_amount, 1.0, exp(-pow(df / 0.05, 2.0)));
    float th = 6.2832 * df * t;
    return a * vec2(cos(th) / 0.493, sin(th) * sw / 0.877);
}

vec3 vits(int k, float f, float x) {
    float t = 10.5 + 52.0 * x;
    vec3 s = vitsSig(k, t);
    // Яркость: ФНЧ и режектор поднесущей; остаток поднесущей — мелкая сетка на лесенке
    float lp = 1.0 / (1.0 + pow(s.z / (6.0 - 2.0 * p_amount), 4.0));
    float trap = 1.0 - (0.9 - 0.3 * p_amount) * exp(-pow((s.z - (p_mode < 0.5 ? 4.3 : 4.43)) / 0.45, 2.0));
    float Y = s.x + s.y * lp * trap * sin(6.2832 * s.z * t);
    float Db, Dr;
    if (p_mode < 0.5) {
        bool db = mod(f, 2.0) < 0.5;
        float own = secamDet(k, t, db), prev = secamDet(k - 1, t, !db);             // вторая составляющая — с прошлой строки
        Db = db ? own : prev; Dr = db ? prev : own;
    } else {
        float sw = mod(f, 2.0) < 0.5 ? 1.0 : -1.0;
        vec2 c = palDet(k, t, sw);
        if (p_fault < 0.5) c = 0.5 * (c + palDet(k - 1, t, -sw));                   // PAL-D: усреднение с линией задержки
        Db = c.x; Dr = c.y;
    }
    Y = 0.03 + 0.8 * Y + (h(vec2(x * 720.0, f + uTime)) - 0.5) * p_noise * 0.28;
    Db *= 0.8; Dr *= 0.8;
    float r = Y + Dr, b = Y + Db;
    return vec3(r, (Y - 0.299 * r - 0.114 * b) / 0.587, b);
}

// Содержимое кадрового гашения (строка поля f в 0..23)
vec3 blank(float f, float x) {
    if (f < 3.0) return vec3(0.0); // кадровый синхроимпульс
    if (p_mode < 0.5 && p_bottles > 0.5 && f >= 7.0 && f < 16.0) { // SECAM: сигналы опознавания («бутылки»), декодированные
        float rp = smoothstep(0.12, 0.35, x) * (1.0 - smoothstep(0.9, 0.95, x));
        return mod(f, 2.0) < 0.5 ? vec3(0.05, 0.1, 0.12 + 0.7 * rp) : vec3(0.12 + 0.7 * rp, 0.08, 0.05);
    }
    bool vt = p_vits > 0.5;
    if (vt && f >= 17.0 && f < 21.0) return vits(int(f) - 17, f, x);
    if ((vt ? f >= 21.0 && f < 23.0 : f >= 18.0 && f < 21.0) && x > 0.08 && x < 0.95) // что-то вроде телетекста
        return vec3(h(vec2(floor(x * 160.0), f + float(uFrame % 4096) * 0.37)) > 0.5 ? 0.75 : 0.06);
    return vec3(0.03);
}

void main() {
    vec2 uv = vec2(vUv.x, 1.0 - vUv.y);
    vec2 R = vec2(textureSize(uInput, 0));
    float s = floor(uv.y * 288.0);
    // Без КГИ: 288 видимых строк по кругу, гашения не видно (roll просто прокручивает картинку)
    bool showBlank = p_blanking > 0.5;
    float f = showBlank ? mod(s + floor(p_roll), 312.0) : mod(s + floor(p_roll), 288.0) + 24.0;
    float x = uv.x;
    float dx = 1.0 / 720.0;
    float A = p_amount, N = p_noise;
    vec3 o;

    if (f < 24.0) {
        o = blank(f, x);
    } else {
        float L = f - 24.0;
        float y0 = (L + 0.5) / 288.0;   // текущая строка
        float y1 = (L - 0.5) / 288.0;   // прошлая строка (линия задержки)
        float ev = mod(L, 2.0) < 0.5 ? 1.0 : 0.0;

        // Яркость: мягкий ФНЧ со звоном
        float Y = 0.0;
        float y00 = yc(src(vec2(x, y0))).x;
        float yb = 0.0;
        for (int k = -2; k <= 2; k++) {
            float fk = float(k);
            float w = k == 0 ? 0.6 : (abs(fk) < 1.5 ? 0.26 : -0.06);
            float v = yc(src(vec2(x + fk * dx * 1.3, y0))).x;
            Y += w * v; yb += v * 0.2;
        }
        Y = mix(y00, Y, A);

        float Db, Dr;
        if (p_mode < 0.5) {
            // SECAM
            vec4 sl = vec4(0.0), fs = vec4(0.0);
            float ws = 0.0, wf = 0.0;
            for (int k = 0; k < 20; k++) {
                float fk = float(k);
                float of = fk * dx * 2.0;
                vec3 a = yc(src(vec2(x - of, y0)));
                vec3 b = yc(src(vec2(x - of, y1)));
                float w1 = exp(-fk / (0.5 + 5.0 * A));
                float w2 = exp(-fk / (0.3 + 1.2 * A));
                vec4 v = vec4(a.yz, b.yz);
                sl += w1 * v; ws += w1;
                fs += w2 * v; wf += w2;
            }
            sl /= ws; fs /= wf;
            vec4 C = sl + (fs - sl) * 2.2 * A; // перерегулирование → факелы
            float fc = fish(L, x), fp = fish(L - 1.0, x);
            if (ev > 0.5) { Db = C.x + fc; Dr = C.w + fp; }
            else          { Db = C.z + fp; Dr = C.y + fc; }
            if (p_fault > 0.5) { float q = Db; Db = Dr * 0.8; Dr = q * 1.25; } // перепутано опознавание
            float tx = ev > 0.5 ? Db : Dr;
            Y += 0.07 * A * sin(x * 6.2832 * (250.0 + 90.0 * tx) + L * 2.1) * (0.25 + length(vec2(Db, Dr)));
        } else {
            // PAL
            vec4 C = vec4(0.0);
            float ws = 0.0;
            for (int k = 0; k < 20; k++) {
                float fk = float(k) - 9.5;
                float w = exp(-pow(fk / (1.0 + 3.5 * A), 2.0));
                vec3 a = yc(src(vec2(x + fk * dx * 1.6, y0)));
                vec3 b = yc(src(vec2(x + fk * dx * 1.6, y1)));
                C += w * vec4(a.yz, b.yz); ws += w;
            }
            C /= ws;
            float hf = (y00 - yb) * 1.8 * A;
            float ph = x * 6.2832 * 220.0 + L * 1.5708 + uTime * 3.0;
            float sw = ev > 0.5 ? 1.0 : -1.0;
            float th = p_fault > 0.5 ? 0.45 : 0.05;
            mat2 rt = mat2(cos(th), sin(th), -sin(th), cos(th));
            vec2 c0 = rt * vec2(C.x,  C.y * sw); c0.y *=  sw;
            vec2 c1 = rt * vec2(C.z, -C.w * sw); c1.y *= -sw;
            vec2 cc = p_fault > 0.5 ? c0 : (c0 + c1) * 0.5;          // PAL-S или PAL-D
            cc += hf * vec2(cos(ph), sin(ph));                        // cross-color
            cc += (vec2(h(uv * R + uTime), h(uv * R - uTime)) - 0.5) * N * 0.35; // шум цветности
            Db = cc.x; Dr = cc.y;
            Y += 0.1 * A * (Db * sin(ph) + Dr * cos(ph) * sw);        // dot crawl
        }

        Y += (h(uv * R + fract(uTime) * 91.0) - 0.5) * N * 0.28;      // снег
        float r = Y + Dr, b = Y + Db, g = (Y - 0.299 * r - 0.114 * b) / 0.587;
        o = vec3(r, g, b);
    }

    o *= 0.8 + 0.2 * sin(fract(uv.y * 288.0) * 3.1416);               // строки развёртки
    fragColor = vec4(clamp(o, 0.0, 1.0), 1.0);
}
