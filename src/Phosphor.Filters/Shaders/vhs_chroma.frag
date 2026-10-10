#version 330 core

// Даунсэмпл цветности VHS: вход — полный кадр, выход — в 4 раза меньше по каждой оси.
// Каждый выходной тексель — среднее box-фильтром 4×4 полных пикселей. I берётся из x + iqs, Q из x − iqs (разъезд).

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uInput;
uniform vec2 uFull;     // полный размер кадра, px
uniform float uIqs;     // разъезд I/Q, в uv входа
uniform float uRs;      // сдвиг RGB (rgbShift), в uv входа

vec3 fetchRGB(float x, float y, float s) {
    return vec3(texture(uInput, vec2(x - s, y)).r,
                texture(uInput, vec2(x, y)).g,
                texture(uInput, vec2(x + s, y)).b);
}

vec3 toYIQ(vec3 c) {
    return vec3(dot(c, vec3(0.299, 0.587, 0.114)),
                dot(c, vec3(0.596, -0.274, -0.322)),
                dot(c, vec3(0.211, -0.523, 0.312)));
}

void main() {
    // Центр выходного текселя совпадает с центром блока 4×4 полных пикселей; смещения центров проб — −1.5..1.5 px.
    float sumI = 0.0;
    float sumQ = 0.0;
    for (int j = 0; j < 4; j++) {
        float y = vUv.y + (float(j) - 1.5) / uFull.y;
        for (int i = 0; i < 4; i++) {
            float x = vUv.x + (float(i) - 1.5) / uFull.x;
            sumI += toYIQ(fetchRGB(x + uIqs, y, uRs)).y;
            sumQ += toYIQ(fetchRGB(x - uIqs, y, uRs)).z;
        }
    }
    fragColor = vec4(sumI / 16.0, sumQ / 16.0, 0.0, 1.0);
}
