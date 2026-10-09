#version 330 core

out vec4 fragColor;

uniform sampler2D uFg;
uniform sampler2D uBg;
uniform usampler2D uGlyph;
uniform sampler2D uAtlas;

uniform ivec2 uGridSize;   // (Cols, Rows)
uniform ivec2 uCellSize;   // (CellWidth, CellHeight)
uniform int uAtlasCols;
uniform int uTargetHeight;

void main() {
    // gl_FragCoord: начало снизу. Сетка — сверху.
    ivec2 px = ivec2(int(gl_FragCoord.x), uTargetHeight - 1 - int(gl_FragCoord.y));

    ivec2 cell = px / uCellSize;
    if (px.x < 0 || px.y < 0 || cell.x >= uGridSize.x || cell.y >= uGridSize.y) {
        fragColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    ivec2 localPx = px - cell * uCellSize;

    int glyph = int(texelFetch(uGlyph, cell, 0).r);
    ivec2 atlasPx = ivec2((glyph % uAtlasCols) * uCellSize.x + localPx.x,
                          (glyph / uAtlasCols) * uCellSize.y + localPx.y);
    float cov = texelFetch(uAtlas, atlasPx, 0).r;

    vec3 fg = texelFetch(uFg, cell, 0).rgb;
    vec3 bg = texelFetch(uBg, cell, 0).rgb;

    fragColor = vec4(mix(bg, fg, cov), 1.0);
}
