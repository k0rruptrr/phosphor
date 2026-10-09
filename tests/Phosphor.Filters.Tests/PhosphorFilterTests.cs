using Phosphor.Filters;
using Phosphor.Gfx;

namespace Phosphor.Filters.Tests;

public class PhosphorFilterTests
{
    private static readonly string[] ParamNames = { "color", "persistence", "tint", "brightness" };

    private static readonly Dictionary<string, float> Defaults = new()
    {
        ["color"] = 0f,
        ["persistence"] = 0.82f,
        ["tint"] = 1f,
        ["brightness"] = 1f,
    };

    [Fact]
    public void Id_IsPhosphor()
    {
        Assert.Equal("phosphor", new PhosphorFilter().Id);
    }

    [Fact]
    public void Params_HasExactlyTheFourExpectedNames()
    {
        var names = new PhosphorFilter().Params.Select(p => p.Name).ToArray();
        Assert.Equal(ParamNames, names);
    }

    [Fact]
    public void Params_DefaultsMatchSpec()
    {
        foreach (var p in new PhosphorFilter().Params)
            Assert.Equal(Defaults[p.Name], p.Default);
    }

    [Fact]
    public void Params_MinLessOrEqualDefaultLessOrEqualMax()
    {
        foreach (var p in new PhosphorFilter().Params)
        {
            Assert.True(p.Min <= p.Default, $"{p.Name}: Min {p.Min} > Default {p.Default}");
            Assert.True(p.Default <= p.Max, $"{p.Name}: Default {p.Default} > Max {p.Max}");
        }
    }

    [Fact]
    public void Color_OptionsAreGreenAmberWhite()
    {
        var color = new PhosphorFilter().Params.Single(p => p.Name == "color");
        Assert.Equal(new[] { "green", "amber", "white" }, color.Options);
    }

    [Fact]
    public void ShaderResource_Exists()
    {
        Assert.NotNull(typeof(PhosphorFilter).Assembly.GetManifestResourceStream("Phosphor.Filters.Shaders.phosphor.frag"));
    }

    [Fact]
    public void Shader_PassesStaticSyntaxCheck()
    {
        var src = ShaderStatic.Load("Phosphor.Filters.Shaders.phosphor.frag");
        ShaderStatic.CheckSyntax(src, "phosphor.frag");
    }

    [Fact]
    public void Shader_DeclaresExactlyTheFilterParams()
    {
        var src = ShaderStatic.Load("Phosphor.Filters.Shaders.phosphor.frag");
        ShaderStatic.CheckParams(new PhosphorFilter(), src);
    }

    [Fact]
    public void Dispose_WithoutInit_DoesNotThrow()
    {
        var ex = Record.Exception(() => new PhosphorFilter().Dispose());
        Assert.Null(ex);
    }
}
