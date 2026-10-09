using Phosphor.Filters;

namespace Phosphor.Filters.Tests;

public class ChromaFilterTests
{
    private static readonly string[] ParamNames = { "amount", "radial", "angle", "samples" };

    [Fact]
    public void Id_IsChroma()
    {
        Assert.Equal("chroma", new ChromaFilter().Id);
    }

    [Fact]
    public void Params_HasExactlyTheFourExpectedNames()
    {
        var names = new ChromaFilter().Params.Select(p => p.Name).ToArray();
        Assert.Equal(ParamNames, names);
    }

    [Fact]
    public void Params_ContainsAmount()
    {
        Assert.Contains(new ChromaFilter().Params, p => p.Name == "amount");
    }

    [Fact]
    public void Shader_PassesStaticSyntaxCheck()
    {
        var src = ShaderStatic.Load("Phosphor.Filters.Shaders.chroma.frag");
        ShaderStatic.CheckSyntax(src, "chroma.frag");
    }

    [Fact]
    public void Shader_DeclaresExactlyTheParams()
    {
        var src = ShaderStatic.Load("Phosphor.Filters.Shaders.chroma.frag");
        ShaderStatic.CheckParams(new ChromaFilter(), src);
    }
}
