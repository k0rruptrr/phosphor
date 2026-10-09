using System.Text.RegularExpressions;
using Phosphor.Filters;
using Phosphor.Gfx;

namespace Phosphor.Filters.Tests;

public class CrtFilterTests
{
    private static readonly string[] ParamNames =
    {
        "curvature", "scanlines", "scanlinePeriod", "mask", "vignette",
        "bloom", "bloomRadius", "flicker", "brightness", "cornerRadius",
    };

    private static readonly Dictionary<string, float> Defaults = new()
    {
        ["curvature"] = 0.08f,
        ["scanlines"] = 0.5f,
        ["scanlinePeriod"] = 3f,
        ["mask"] = 0.3f,
        ["vignette"] = 0.35f,
        ["bloom"] = 0.8f,
        ["bloomRadius"] = 6f,
        ["flicker"] = 0.03f,
        ["brightness"] = 1.1f,
        ["cornerRadius"] = 0.03f,
    };

    private static string LoadShader(string fileName)
    {
        using var s = typeof(CrtFilter).Assembly.GetManifestResourceStream("Phosphor.Filters.Shaders." + fileName);
        Assert.NotNull(s);
        using var r = new StreamReader(s!);
        return r.ReadToEnd();
    }

    [Fact]
    public void Id_IsCrt()
    {
        Assert.Equal("crt", new CrtFilter().Id);
    }

    [Fact]
    public void Params_HasExactlyTheTenExpectedNames()
    {
        var names = new CrtFilter().Params.Select(p => p.Name).ToArray();
        Assert.Equal(ParamNames, names);
    }

    [Fact]
    public void Params_MinLessOrEqualDefaultLessOrEqualMax()
    {
        foreach (var p in new CrtFilter().Params)
        {
            Assert.True(p.Min <= p.Default, $"{p.Name}: Min {p.Min} > Default {p.Default}");
            Assert.True(p.Default <= p.Max, $"{p.Name}: Default {p.Default} > Max {p.Max}");
        }
    }

    [Fact]
    public void Params_DefaultsMatchSpec()
    {
        foreach (var p in new CrtFilter().Params)
            Assert.Equal(Defaults[p.Name], p.Default);
    }

    [Theory]
    [InlineData("crt.frag")]
    [InlineData("crt_bright.frag")]
    [InlineData("crt_blur.frag")]
    public void ShaderResource_Exists(string fileName)
    {
        Assert.NotNull(typeof(CrtFilter).Assembly.GetManifestResourceStream("Phosphor.Filters.Shaders." + fileName));
    }

    [Theory]
    [InlineData("crt.frag")]
    [InlineData("crt_bright.frag")]
    [InlineData("crt_blur.frag")]
    public void Shader_StartsWithVersion330Core(string fileName)
    {
        Assert.StartsWith("#version 330 core", LoadShader(fileName));
    }

    [Theory]
    [InlineData("crt.frag")]
    [InlineData("crt_bright.frag")]
    [InlineData("crt_blur.frag")]
    public void Shader_BracesAndParenthesesAreBalanced(string fileName)
    {
        var shader = LoadShader(fileName);
        Assert.Equal(shader.Count(ch => ch == '{'), shader.Count(ch => ch == '}'));
        Assert.Equal(shader.Count(ch => ch == '('), shader.Count(ch => ch == ')'));
    }

    [Fact]
    public void Shader_DeclaresEveryParam()
    {
        var shader = LoadShader("crt.frag");
        foreach (var name in ParamNames)
            Assert.Contains($"uniform float p_{name};", shader);
    }

    [Fact]
    public void Shader_HasNoUndeclaredParams()
    {
        var shader = LoadShader("crt.frag");
        var matches = Regex.Matches(shader, @"uniform\s+float\s+p_(\w+)\s*;");
        Assert.All(matches.Cast<Match>(), m => Assert.Contains(m.Groups[1].Value, ParamNames));
    }

    [Fact]
    public void Shader_HasRequiredInterface()
    {
        var shader = LoadShader("crt.frag");
        Assert.Contains("out vec4 fragColor", shader);
        Assert.Contains("in vec2 vUv", shader);
        Assert.Contains("uniform sampler2D uInput", shader);
        Assert.Contains("uniform sampler2D uBloom", shader);
    }

    [Fact]
    public void BloomPasses_HaveExpectedInterface()
    {
        Assert.Contains("uniform sampler2D uInput", LoadShader("crt_bright.frag"));
        var blur = LoadShader("crt_blur.frag");
        Assert.Contains("uniform sampler2D uInput", blur);
        Assert.Contains("uniform vec2 uDir", blur);
    }
}
