using Silk.NET.OpenGL;

namespace Phosphor.Gfx;

public enum TransitionKind { Cut = -1, Fade = 0, VhsNoise = 1, Glitch = 2 }

/// <summary>
/// Смешивание двух картинок при смене сцены/пресета.
/// Fade — плавный кроссфейд; VhsNoise — «перемотка кассеты»: рвутся строки, ползёт кадр, снег, блоки строк
/// перескакивают на новую картинку; Glitch — короткий сбой одной картинки (смена пресета на той же сцене).
/// </summary>
public sealed class Transition : IDisposable
{
    private readonly GL _gl;
    private readonly uint _prog;
    private readonly int _locA, _locB, _locP, _locKind, _locTime, _locRes;

    private const string Frag = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColor;
        uniform sampler2D uA;
        uniform sampler2D uB;
        uniform float uP;      // прогресс 0..1
        uniform int uKind;     // 0 fade, 1 vhs-noise, 2 glitch
        uniform float uTime;
        uniform vec2 uRes;

        // Целочисленный хеш (float-хеши на целых координатах пикселей дают периодичную решётку)
        uint ih(uint x) { x ^= x >> 16u; x *= 0x7feb352du; x ^= x >> 15u; x *= 0x846ca68bu; x ^= x >> 16u; return x; }
        float hash(vec2 p) {
            uvec2 q = uvec2(ivec2(floor(p)) + ivec2(65536));
            return float(ih(q.x ^ ih(q.y * 0x9E3779B9u))) * (1.0 / 4294967296.0);
        }

        void main() {
            float p = clamp(uP, 0.0, 1.0);
            if (uKind == 0) {
                float t = p * p * (3.0 - 2.0 * p);
                fragColor = vec4(mix(texture(uA, vUv).rgb, texture(uB, vUv).rgb, t), 1.0);
                return;
            }
            // Интенсивность помех: пик в середине перехода (для glitch — короткий всплеск в начале)
            float n = uKind == 1 ? sin(3.14159 * p) : (1.0 - p) * (1.0 - p);
            float frame = floor(uTime * 30.0);
            float row = floor(vUv.y * uRes.y / 3.0);
            float blockRow = floor(vUv.y * 18.0);

            vec2 uv = vUv;
            // вертикальный срыв синхронизации: кадр «уезжает» вверх
            uv.y = fract(uv.y + n * n * 0.35 * hash(vec2(frame, 7.0)));
            // разрывы строк
            float tear = (hash(vec2(row, frame)) - 0.5) * 0.12 * n;
            tear += step(0.93, hash(vec2(blockRow, frame + 3.0))) * 0.25 * n;
            uv.x = fract(uv.x + tear);

            // какую картинку показывать: блоки строк по одному перескакивают на новую
            float useB = uKind == 2 ? 1.0 : step(hash(vec2(blockRow, 11.0)), p * 1.15 - 0.05);
            vec3 a = texture(uA, uv).rgb;
            vec3 b = texture(uB, uv).rgb;
            vec3 col = mix(a, b, useB);

            // цвет «плывёт»: красный и синий расходятся
            float sh = 0.006 * n;
            vec3 colR = mix(texture(uA, uv + vec2(sh, 0.0)).rgb, texture(uB, uv + vec2(sh, 0.0)).rgb, useB);
            vec3 colB = mix(texture(uA, uv - vec2(sh, 0.0)).rgb, texture(uB, uv - vec2(sh, 0.0)).rgb, useB);
            col = vec3(colR.r, col.g, colB.b);

            // Снег — редкими искрами и лёгким зерном, а не серой пеленой: картинка должна оставаться видна сквозь помехи
            float snow = hash(gl_FragCoord.xy + frame * 17.0);
            col += vec3(step(0.985, snow)) * 0.7 * n;
            col += (snow - 0.5) * 0.12 * n;
            // в «разорванных» строках картинка гаснет — как пропадание синхры
            col *= 1.0 - 0.6 * n * step(0.85, hash(vec2(row, frame + 5.0)));
            float band = smoothstep(0.04, 0.0, abs(fract(vUv.y + uTime * 0.8) - 0.5)) * n;
            col += vec3(0.3) * band;
            fragColor = vec4(max(col, 0.0), 1.0);
        }
        """;

    public Transition(GL gl)
    {
        _gl = gl;
        _prog = GlUtil.CreateProgram(gl, GlUtil.FullscreenVertex, Frag, "transition");
        _locA = gl.GetUniformLocation(_prog, "uA");
        _locB = gl.GetUniformLocation(_prog, "uB");
        _locP = gl.GetUniformLocation(_prog, "uP");
        _locKind = gl.GetUniformLocation(_prog, "uKind");
        _locTime = gl.GetUniformLocation(_prog, "uTime");
        _locRes = gl.GetUniformLocation(_prog, "uRes");
    }

    /// <summary>Нарисовать смесь a и b в target. Для Glitch b игнорируется (используется a).</summary>
    public void Render(uint texA, uint texB, RenderTarget target, TransitionKind kind, float progress, float time)
    {
        target.Bind();
        _gl.Disable(EnableCap.Blend);
        _gl.UseProgram(_prog);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texA);
        _gl.Uniform1(_locA, 0);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, kind == TransitionKind.Glitch ? texA : texB);
        _gl.Uniform1(_locB, 1);
        _gl.Uniform1(_locP, progress);
        _gl.Uniform1(_locKind, (int)kind);
        _gl.Uniform1(_locTime, time);
        _gl.Uniform2(_locRes, (float)target.Width, target.Height);
        GlUtil.DrawFullscreen(_gl);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    public void Dispose() => _gl.DeleteProgram(_prog);
}
