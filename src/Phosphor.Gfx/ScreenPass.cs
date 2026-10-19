using Phosphor.Core;
using Silk.NET.OpenGL;

namespace Phosphor.Gfx;

/// <summary>
/// «Корпус монитора» — последний проход перед выводом: изогнутый проём рамки, широкая фаска, отсвет картинки
/// на пластике, утопленное стекло с тенью у краёв, отлив стекла. Один на все пресеты. Картинка не зависит от времени.
/// Геометрия: окно рамки → кривизна (барабан, та же функция для проёма и для картинки) → проём/стекло/кромка.
/// Маска кинескопа (тип shadow/trinitron/diagonal) — поверх картинки стекла, до блика; чересстрочность — там же,
/// по прошлому кадру входа (отдельный RenderTarget). Не больше трёх проходов: уменьшение входа (1/8) для отсвета,
/// основной проход, копия входа в прошлый кадр.
/// RenderTarget пересоздаются только при смене размера; на кадр аллокаций нет.
/// </summary>
public sealed class ScreenPass : IDisposable
{
    private const int SmallDiv = 8;
    /// <summary>Минимальный радиус углов корпуса (доля высоты окна).</summary>
    private const float TvMinCorner = 0.08f;
    private const float MonitorMinCorner = 0.04f;
    /// <summary>Тёмная кромка стекла между картинкой и проёмом, доля b.</summary>
    private const float KromkaShare = 0.1f;
    /// <summary>Ширина фаски (доля толщины рамки b): tv — широкая, monitor — средняя, thin — без фаски.</summary>
    private const float TvFaceShare = 0.45f;
    private const float MonitorFaceShare = 0.35f;

    private readonly GL _gl;
    private readonly uint _prog;
    private readonly uint _progCopy;
    private readonly int _locTex, _locSmall, _locOut, _locWin, _locAspect, _locCurv, _locOverscan, _locCornerPx, _locKromka;
    private readonly int _locVig, _locGlare, _locGlow, _locStyle, _locB, _locOuterR, _locPlastic, _locFaceW;
    private readonly int _locPrev, _locIl, _locLine, _locPar, _locMask, _locMaskType, _locThreadPx;
    private readonly int _locCopyTex;
    private RenderTarget? _main, _small, _prev;
    private int _w, _h;
    /// <summary>В _prev лежит прошлый кадр входа (после первого кадра и смены размера — нет).</summary>
    private bool _hasPrev;
    /// <summary>Номер кадра: чётность поля для чересстрочности.</summary>
    private int _frame;

    private const string Frag = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColor;

        uniform sampler2D uTex;     // вход: картинка слота
        uniform sampler2D uSmall;   // вход, уменьшенный в 8 раз (отсвет на рамке)
        uniform vec2 uOut;          // размер выхода, px
        uniform vec4 uWin;          // окно рамки до кривизны, px: x0, y0, x1, y1 (низ-лево)
        uniform float uAspect;      // аспект окна рамки
        uniform float uCurv;
        uniform float uOverscan;
        uniform float uCornerPx;    // радиус скругления проёма = радиус углов картинки, px
        uniform float uKromka;      // тёмная кромка стекла между картинкой и проёмом, px (0 без рамки)
        uniform float uVig;
        uniform float uGlare;
        uniform float uGlow;        // bezelGlow; 0 — отсвета нет
        uniform int uStyle;         // 0 none, 1 thin, 2 tv, 3 monitor
        uniform float uB;           // толщина рамки, px
        uniform float uFaceW;       // ширина фаски, px (0 — без фаски)
        uniform float uOuterR;      // радиус внешних углов корпуса, px (0 — прямые)
        uniform vec3 uPlastic;      // базовый цвет пластика
        uniform sampler2D uPrev;    // прошлый кадр входа (для чересстрочности)
        uniform float uIl;          // чересстрочность: 0 — выкл
        uniform float uLine;        // строка развёртки, px выхода (2 px на 1080)
        uniform int uPar;           // чётность поля текущего кадра (0/1)
        uniform float uMask;        // сила маски кинескопа
        uniform int uMaskType;      // 0 shadow, 1 trinitron, 2 diagonal
        uniform float uThreadPx;    // толщина тёмных нитей trinitron, px выхода

        const float BEND = 0.6;
        const float EDGE_SOFT_PX = 1.5;
        const vec2 BLUR_OFF[9] = vec2[9](vec2(0.0), vec2(1, 0), vec2(-1, 0), vec2(0, 1), vec2(0, -1),
                                         vec2(1, 1), vec2(-1, 1), vec2(1, -1), vec2(-1, -1));

        float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }

        // Signed distance до скруглённого прямоугольника с полуразмерами hs и радиусом r (px).
        float sdRR(vec2 p, vec2 hs, float r) {
            vec2 q = abs(p) - (hs - r);
            return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
        }

        // Кривизна кинескопа (формула crt.frag, аспект окна): окно (0..1) -> изогнутые координаты.
        // Той же функцией гнутся и проём рамки, и картинка — поэтому они совпадают.
        vec2 barrel(vec2 lu) {
            vec2 c = lu * 2.0 - 1.0;
            vec2 p = vec2(c.x * uAspect, c.y);
            float s = uCurv * BEND * dot(p, p) / (uAspect * uAspect + 1.0);
            return lu + c * s * 0.5;
        }

        // Изогнутые координаты -> uv картинки (overscan: картинка шире проёма на заданную долю).
        vec2 picUv(vec2 q) { return 0.5 + (q - 0.5) * (1.0 - 2.0 * uOverscan); }

        // Размытая копия входа в точке uv; rad — радиус размытия в uv картинки.
        vec3 blurSmall(vec2 uv, vec2 rad) {
            vec3 acc = vec3(0.0);
            for (int i = 0; i < 9; i++) acc += texture(uSmall, uv + BLUR_OFF[i] * rad).rgb;
            return acc / 9.0;
        }

        // Триада маски для канала-лидера leader (0 R, 1 G, 2 B): лидер 1.0, остальные 0.5, среднее по 3 px сохраняется.
        vec3 triad(float leader, float depth) {
            vec3 tri = vec3(leader == 0.0 ? 1.0 : 0.5, leader == 1.0 ? 1.0 : 0.5, leader == 2.0 ? 1.0 : 0.5);
            return mix(vec3(1.0), tri * 1.5, depth);
        }

        // Маска кинескопа в пикселях выхода. shadow — триады по x; trinitron — полосы без горизонтальной структуры,
        // depth ×0.8 (нити — отдельно, threadCover); diagonal — канал (x + y) mod 3
        // с размытием [1/4 1/2 1/4] по x.
        vec3 maskMul() {
            float fx = floor(gl_FragCoord.x);
            float fy = floor(gl_FragCoord.y);
            vec3 m;
            if (uMaskType == 2) {
                m = 0.25 * triad(mod(fx - 1.0 + fy, 3.0), uMask)
                  + 0.5 * triad(mod(fx + fy, 3.0), uMask)
                  + 0.25 * triad(mod(fx + 1.0 + fy, 3.0), uMask);
            } else if (uMaskType == 1) {
                m = triad(mod(fx, 3.0), 0.8 * uMask);
            } else {
                m = triad(mod(fx, 3.0), uMask);
            }
            return m;
        }

        // Средний цвет картинки: сетка 3×3 по уменьшенной копии. Для подкраски рамки свечением экрана.
        vec3 meanScreen() {
            vec3 acc = vec3(0.0);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    acc += texture(uSmall, vec2(0.17 + 0.33 * float(i), 0.17 + 0.33 * float(j))).rgb;
            return acc / 9.0;
        }

        // Нити trinitron: две линии на 1/3 и 2/3 высоты окна в изогнутых координатах (гнутся с картинкой).
        // Толщина uThreadPx, сглаживание по расстоянию ~1 px. Возвращает покрытие 0..1.
        float threadCover(vec2 q, vec2 wS) {
            float h = 0.5 * uThreadPx;
            float dA = abs((q.y - 1.0 / 3.0) * wS.y);
            float dB = abs((q.y - 2.0 / 3.0) * wS.y);
            return max(1.0 - smoothstep(h, h + 1.0, dA), 1.0 - smoothstep(h, h + 1.0, dB));
        }

        // Снаружи проёма: при bezel none — чёрный; при рамке — пластик. dist — расстояние до проёма, px, >= 0.
        vec3 outsideCol(vec2 px, vec2 q, float dist, vec2 wS) {
            if (uStyle == 0) return vec3(0.0);
            // Пластик: тёмный внешний край, верхний свет, статичное зерно.
            float outerD = min(min(px.x, uOut.x - px.x), min(px.y, uOut.y - px.y));
            float edgeDark = mix(0.6, 1.0, smoothstep(0.0, max(uB * 0.5, 1.0), outerD));
            float grain = 1.0 + (hash(gl_FragCoord.xy) - 0.5) * 0.04;

            // Четыре клина рамки по диагоналям выхода: верх светлее, бока средние, низ темнее.
            // Швы сглажены (ширина перехода ~ радиус углов). Без нормалей.
            vec2 nd = (px - 0.5 * uOut) / (0.5 * uOut);
            float seam = 0.02;
            float wTop = smoothstep(-seam, seam, nd.y - abs(nd.x));
            float wBot = smoothstep(-seam, seam, -nd.y - abs(nd.x));
            float wSide = clamp(1.0 - wTop - wBot, 0.0, 1.0);
            float wedge = 1.3 * wTop + 0.5 * wBot + 0.85 * wSide;
            // Клинья только на полосе фаски; плоская часть рамки — ровный тон. Перелом сглажен на 1–2 px.
            float inFace = uFaceW > 0.0 ? 1.0 - smoothstep(uFaceW - 1.5, uFaceW + 1.5, dist) : 0.0;
            float shade = mix(1.0, wedge, inFace);

            // Подкраска фосфором: пластик смешан с тёмным оттенком картинки (средний цвет × 0.2).
            vec3 plastic = uPlastic;
            if (uGlow > 0.0) plastic = mix(uPlastic, meanScreen() * 0.2, 0.5);
            vec3 col = plastic * shade * grain * edgeDark;

            // Фаска у проёма: к стеклу темнее, переход к плоской рамке мягкий.
            if (uFaceW > 0.0) {
                col *= mix(0.6, 1.0, smoothstep(0.0, uFaceW, dist));
            }

            // Тень рамки у стекла: ~5 px (масштаб по высоте), плавно темнеет к проёму.
            col *= mix(0.35, 1.0, smoothstep(0.0, 0.005 * uOut.y, dist));

            // Зеркальный отсвет: точка за кромкой отражается внутрь стекла; размытая копия картинки,
            // вес 0.7·bezelGlow у самой кромки.
            if (uGlow > 0.0) {
                vec2 e = clamp(q, 0.0, 1.0);
                vec2 outV = q - e;
                vec2 mir = clamp(e - outV, 0.0, 1.0);
                vec2 rad = 0.5 * uB / wS;
                vec3 refl = blurSmall(picUv(mir), rad);
                col = mix(col, refl, 0.7 * uGlow * exp(-dist / (0.12 * uB + 0.5)));
            }

            // Светодиод питания (monitor): тусклый зелёный, нижняя правая часть рамки.
            if (uStyle == 3) {
                vec2 lc = vec2(uOut.x - 1.5 * uB, 0.5 * uB);
                float lr = max(1.5, 0.12 * uB);
                vec2 dl = px - lc;
                col += vec3(0.1, 0.9, 0.25) * 0.35 * exp(-dot(dl, dl) / (lr * lr));
            }
            return col;
        }

        // Стекло: картинка, чересстрочность, утопленная тень у края, виньетка, маска, нити, отлив.
        // sdG — расстояние до края стекла, px (<0 внутри).
        vec3 glassCol(vec2 q, vec2 wS, float sdG) {
            vec2 pu = picUv(q);
            vec2 puc = clamp(pu, 0.0, 1.0);
            vec3 col = texture(uTex, puc).rgb;

            // Чересстрочность: строки текущего поля — текущий кадр, другого — смесь с прошлым кадром,
            // и они чуть темнее (мерцание поля). На неподвижной картинке ничего не меняется (кроме мерцания).
            if (uIl > 0.0) {
                vec3 pr = texture(uPrev, puc).rgb;
                int li = int(floor(gl_FragCoord.y / uLine));
                if ((li % 2) != uPar) col = mix(col, pr, uIl) * (1.0 - 0.12 * uIl);
            }

            // Глубокая внутренняя тень у края проёма: стекло утоплено, ~3.5% высоты.
            if (uStyle > 0) {
                float depth = -sdG;
                col *= mix(0.05, 1.0, smoothstep(0.0, 0.035 * wS.y, depth));
            }

            // Виньетка (как в crt.frag), по картинке.
            vec2 vq = pu - 0.5;
            float ve = dot(vq, vq) * 4.0;
            col *= 1.0 - uVig * smoothstep(0.3, 1.5, ve);

            // Маска кинескопа — поверх картинки, до блика.
            if (uMask > 0.0) col *= maskMul();
            // Нити trinitron: физические, 12% на линии, гнутся вместе с картинкой.
            if (uMask > 0.0 && uMaskType == 1) col *= 1.0 - 0.12 * threadCover(q, wS);

            // Отлив стекла: широкий и мягкий, светлее и серее в центре/вверху, к краям гаснет.
            // Смешивание экранное c + r·(1 − c); на белом входе не меняет картинку.
            // Свечение стекла: g = sqrt(25·x(1−x)·y(1−y)) — максимум в центре, пятен и окон нет.
            // Серый слой с альфой 0.3·glare, смешивание экранное.
            if (uGlare > 0.0) {
                vec2 xy = clamp(q, 0.0, 1.0);
                float g = sqrt(clamp(25.0 * xy.x * (1.0 - xy.x) * xy.y * (1.0 - xy.y), 0.0, 1.0));
                float a = 0.3 * uGlare * g;
                col = col + vec3(0.2) * a * (1.0 - col);
            }
            return col;
        }

        void main() {
            vec2 px = vUv * uOut;
            vec2 w0 = uWin.xy, w1 = uWin.zw;
            vec2 wC = 0.5 * (w0 + w1);
            vec2 wH = 0.5 * (w1 - w0);
            vec2 wS = 2.0 * wH;
            vec2 lu = (px - w0) / wS;               // окно до кривизны
            vec2 q = barrel(lu);                    // изогнутые координаты: проём и картинка
            vec2 dq = (q - 0.5) * wS;               // изогнутые px от центра проёма

            // Внешние углы корпуса: вне скруглённой формы — чёрный.
            float oi = 1.0;
            if (uOuterR > 0.0) {
                vec2 hO = uOut * 0.5;
                vec2 qo = abs(px - hO) - (hO - uOuterR);
                float sdo = length(max(qo, 0.0)) + min(max(qo.x, qo.y), 0.0) - uOuterR;
                oi = 1.0 - smoothstep(0.0, EDGE_SOFT_PX, sdo);
            }

            // Проём (изогнутый скруглённый прямоугольник) и стекло внутри тёмной кромки.
            float Ra = min(uCornerPx, min(wH.x, wH.y));
            float sdA = sdRR(dq, wH, Ra);
            float sdG = sdRR(dq, wH - uKromka, max(Ra - uKromka, 0.0));

            // Сглаживание границ покрытием пикселя (ширина ~1 px, px выхода): проём — стекло/кромка ↔ снаружи;
            // кромка — стекло ↔ кромка. Снаружи при bezel none — чёрный, при рамке — пластик.
            vec3 cGlass = glassCol(q, wS, sdG);
            vec3 inner = uKromka > 0.0 ? mix(cGlass, vec3(0.012), clamp(sdG + 0.5, 0.0, 1.0)) : cGlass;
            vec3 cOut = outsideCol(px, q, max(sdA, 0.0), wS);
            vec3 col = mix(inner, cOut, clamp(sdA + 0.5, 0.0, 1.0));
            fragColor = vec4(col * oi, 1.0);
        }
        """;

    private const string CopyFrag = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColor;
        uniform sampler2D uTex;
        void main() { fragColor = vec4(texture(uTex, vUv).rgb, 1.0); }
        """;

    public ScreenPass(GL gl)
    {
        _gl = gl;
        _prog = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex, Frag, "screen");
        _progCopy = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex, CopyFrag, "screen-copy");
        _locTex = gl.GetUniformLocation(_prog, "uTex");
        _locSmall = gl.GetUniformLocation(_prog, "uSmall");
        _locOut = gl.GetUniformLocation(_prog, "uOut");
        _locWin = gl.GetUniformLocation(_prog, "uWin");
        _locAspect = gl.GetUniformLocation(_prog, "uAspect");
        _locCurv = gl.GetUniformLocation(_prog, "uCurv");
        _locOverscan = gl.GetUniformLocation(_prog, "uOverscan");
        _locCornerPx = gl.GetUniformLocation(_prog, "uCornerPx");
        _locKromka = gl.GetUniformLocation(_prog, "uKromka");
        _locVig = gl.GetUniformLocation(_prog, "uVig");
        _locGlare = gl.GetUniformLocation(_prog, "uGlare");
        _locGlow = gl.GetUniformLocation(_prog, "uGlow");
        _locStyle = gl.GetUniformLocation(_prog, "uStyle");
        _locB = gl.GetUniformLocation(_prog, "uB");
        _locFaceW = gl.GetUniformLocation(_prog, "uFaceW");
        _locOuterR = gl.GetUniformLocation(_prog, "uOuterR");
        _locPlastic = gl.GetUniformLocation(_prog, "uPlastic");
        _locPrev = gl.GetUniformLocation(_prog, "uPrev");
        _locIl = gl.GetUniformLocation(_prog, "uIl");
        _locLine = gl.GetUniformLocation(_prog, "uLine");
        _locPar = gl.GetUniformLocation(_prog, "uPar");
        _locMask = gl.GetUniformLocation(_prog, "uMask");
        _locMaskType = gl.GetUniformLocation(_prog, "uMaskType");
        _locThreadPx = gl.GetUniformLocation(_prog, "uThreadPx");
        _locCopyTex = gl.GetUniformLocation(_progCopy, "uTex");
    }

    /// <summary>
    /// tex — готовая картинка слота (после шлейфа), растягивается на весь выход.
    /// Рисует в свой RenderTarget outW×outH и возвращает его текстуру. Всё выключено — возвращает tex без проходов.
    /// </summary>
    public uint Apply(uint tex, int outW, int outH, ScreenConfig cfg)
    {
        // Плоский экран (screen.enabled = false): корпус выключен целиком, поля сохраняются.
        if (!cfg.Enabled || IsOff(cfg) || outW <= 0 || outH <= 0) return tex;
        EnsureTargets(outW, outH);

        bool bezel = cfg.Bezel != "none";
        int style = Math.Max(0, Array.IndexOf(ScreenConfig.BezelStyles, cfg.Bezel));
        // Окно рамки: рамка отступает на b px со всех сторон (thin — в 2.5 раза тоньше).
        float b = !bezel ? 0f : style == 1 ? cfg.BezelSize * outH * 0.4f : cfg.BezelSize * outH;
        float x0 = b, y0 = b, x1 = outW - b, y1 = outH - b;
        float winW = Math.Max(1f, x1 - x0), winH = Math.Max(1f, y1 - y0);
        float rr = cfg.CornerRadius;
        if (style == 2) rr = Math.Max(rr, TvMinCorner);
        else if (style == 3) rr = Math.Max(rr, MonitorMinCorner);
        var (pr, pg, pb) = PlasticColor(cfg.BezelColor);
        float outerR = style switch { 2 => 0.06f * outH, 3 => 0.05f * outH, 1 => 0.03f * outH, _ => 0f };
        float faceW = style switch { 2 => TvFaceShare * b, 3 => MonitorFaceShare * b, _ => 0f };
        float kromka = bezel ? KromkaShare * b : 0f;

        // Прошлый кадр входа нужен только чересстрочности. Interlace = 0 — ни копии, ни RT; при включении
        // первый кадр копирует вход (эффекта нет), старого следа не остаётся.
        bool il = cfg.Interlace > 0f;
        if (!il) _hasPrev = false;
        else
        {
            _prev ??= new RenderTarget(_gl, outW, outH);
            if (!_hasPrev) CopyToPrev(tex);
        }

        // Проход 1: уменьшенная копия входа — только если есть рамка и отсвет.
        if (bezel && cfg.BezelGlow > 0f)
        {
            _small!.Bind();
            _gl.Disable(EnableCap.Blend);
            _gl.UseProgram(_progCopy);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, tex);
            _gl.Uniform1(_locCopyTex, 0);
            GlUtil.DrawFullscreen(_gl);
        }

        // Проход 2: проём, стекло, фаска, отсвет, отлив.
        _main!.Bind();
        _gl.Disable(EnableCap.Blend);
        _gl.UseProgram(_prog);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _small!.Texture);
        _gl.Uniform1(_locTex, 0);
        _gl.Uniform1(_locSmall, 1);
        _gl.Uniform2(_locOut, (float)outW, (float)outH);
        _gl.Uniform4(_locWin, x0, y0, x1, y1);
        _gl.Uniform1(_locAspect, winW / winH);
        _gl.Uniform1(_locCurv, cfg.Curvature);
        _gl.Uniform1(_locOverscan, cfg.Overscan);
        _gl.Uniform1(_locCornerPx, rr * winH);
        _gl.Uniform1(_locKromka, kromka);
        _gl.Uniform1(_locVig, cfg.Vignette);
        _gl.Uniform1(_locGlare, cfg.Glare);
        _gl.Uniform1(_locGlow, bezel ? cfg.BezelGlow : 0f);
        _gl.Uniform1(_locStyle, style);
        _gl.Uniform1(_locB, b);
        _gl.Uniform1(_locFaceW, faceW);
        _gl.Uniform1(_locOuterR, outerR);
        _gl.Uniform3(_locPlastic, pr, pg, pb);
        if (il)
        {
            _gl.ActiveTexture(TextureUnit.Texture2);
            _gl.BindTexture(TextureTarget.Texture2D, _prev!.Texture);
            _gl.Uniform1(_locPrev, 2);
        }
        _gl.Uniform1(_locIl, il ? cfg.Interlace : 0f);
        _gl.Uniform1(_locLine, 2f * outH / 1080f);
        _gl.Uniform1(_locPar, _frame & 1);
        _gl.Uniform1(_locMask, cfg.Mask);
        _gl.Uniform1(_locMaskType, Math.Max(0, Array.IndexOf(ScreenConfig.MaskTypes, cfg.MaskType)));
        _gl.Uniform1(_locThreadPx, Math.Max(1f, outH / 1080f));
        GlUtil.DrawFullscreen(_gl);

        // Вход этого кадра — прошлым для следующего (только при чересстрочности).
        if (il) CopyToPrev(tex);
        _frame++;
        return _main.Texture;
    }

    /// <summary>Копия входа в _prev (прошлый кадр для чересстрочности).</summary>
    private void CopyToPrev(uint tex)
    {
        _prev!.Bind();
        _gl.Disable(EnableCap.Blend);
        _gl.UseProgram(_progCopy);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.Uniform1(_locCopyTex, 0);
        GlUtil.DrawFullscreen(_gl);
        _hasPrev = true;
    }

    /// <summary>Выключено: геометрия, виньетка, блик, рамка, маска и чересстрочность не заданы — кадр проходит без изменений.</summary>
    private static bool IsOff(ScreenConfig c) =>
        c.Curvature <= 0f && c.CornerRadius <= 0f && c.Vignette <= 0f && c.Overscan <= 0f && c.Glare <= 0f && c.Bezel == "none"
        && c.Mask <= 0f && c.Interlace <= 0f;

    /// <summary>Базовый цвет пластика; beige умножен на 0.45, чтобы в тёмной комнате не светился.</summary>
    private static (float r, float g, float b) PlasticColor(string name) => name switch
    {
        "graphite" => (0.204f, 0.212f, 0.228f),   // #34363A
        "beige" => (0.365f, 0.349f, 0.311f),      // #CFC6B0 × 0.45
        _ => (0.110f, 0.110f, 0.118f),            // #1C1C1E
    };

    private void EnsureTargets(int outW, int outH)
    {
        if (_main is not null && _w == outW && _h == outH) return;
        _main?.Dispose();
        _small?.Dispose();
        _prev?.Dispose();
        _prev = null;
        _main = new RenderTarget(_gl, outW, outH);
        _small = new RenderTarget(_gl, Math.Max(1, outW / SmallDiv), Math.Max(1, outH / SmallDiv));
        _hasPrev = false;
        _w = outW; _h = outH;
    }

    public void Dispose()
    {
        _main?.Dispose();
        _small?.Dispose();
        _prev?.Dispose();
        _main = null; _small = null; _prev = null;
        _gl.DeleteProgram(_prog);
        _gl.DeleteProgram(_progCopy);
    }
}
