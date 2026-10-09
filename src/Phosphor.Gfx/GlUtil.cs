using System.Reflection;
using Silk.NET.OpenGL;

namespace Phosphor.Gfx;

/// <summary>Компиляция шейдеров, загрузка из ресурсов, полноэкранный треугольник.</summary>
public static class GlUtil
{
    /// <summary>
    /// Общий вершинный шейдер для всех полноэкранных проходов. Без VBO: треугольник из gl_VertexID.
    /// Отдаёт во фрагментный шейдер: in vec2 vUv; (0,0) — левый низ, (1,1) — правый верх.
    /// </summary>
    public const string FullscreenVertex = """
        #version 330 core
        out vec2 vUv;
        void main() {
            vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            vUv = p;
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private static readonly Dictionary<GL, uint> _vaos = new();

    /// <summary>Нарисовать полноэкранный треугольник текущей программой в текущий FBO.</summary>
    public static void DrawFullscreen(GL gl)
    {
        if (!_vaos.TryGetValue(gl, out var vao))
        {
            vao = gl.GenVertexArray();
            _vaos[gl] = vao;
        }
        gl.BindVertexArray(vao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    /// <summary>Скомпилировать и слинковать программу. Ошибка компиляции — исключение с логом и именем.</summary>
    public static uint CreateProgram(GL gl, string vertexSrc, string fragmentSrc, string debugName)
    {
        uint vs = Compile(gl, ShaderType.VertexShader, vertexSrc, debugName + ".vert");
        uint fs = Compile(gl, ShaderType.FragmentShader, fragmentSrc, debugName + ".frag");
        uint prog = gl.CreateProgram();
        gl.AttachShader(prog, vs);
        gl.AttachShader(prog, fs);
        gl.LinkProgram(prog);
        gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int ok);
        gl.DetachShader(prog, vs); gl.DetachShader(prog, fs);
        gl.DeleteShader(vs); gl.DeleteShader(fs);
        if (ok == 0)
        {
            string log = gl.GetProgramInfoLog(prog);
            gl.DeleteProgram(prog);
            throw new InvalidOperationException($"Линковка шейдера '{debugName}' упала:\n{log}");
        }
        return prog;
    }

    private static uint Compile(GL gl, ShaderType type, string src, string name)
    {
        uint s = gl.CreateShader(type);
        gl.ShaderSource(s, src);
        gl.CompileShader(s);
        gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
        {
            string log = gl.GetShaderInfoLog(s);
            gl.DeleteShader(s);
            throw new InvalidOperationException($"Компиляция '{name}' упала:\n{log}");
        }
        return s;
    }

    /// <summary>Текст ресурса из сборки по LogicalName (см. EmbeddedResource в .csproj).</summary>
    public static string LoadResource(Assembly asm, string logicalName)
    {
        using var s = asm.GetManifestResourceStream(logicalName)
            ?? throw new FileNotFoundException($"Ресурс '{logicalName}' не найден в {asm.GetName().Name}. Есть: {string.Join(", ", asm.GetManifestResourceNames())}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
