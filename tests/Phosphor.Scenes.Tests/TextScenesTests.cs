using Phosphor.Core;

namespace Phosphor.Scenes.Tests;

/// <summary>Сцены, где главное — текст (#247): на них помехи фильтров ослабляются (filters.textClarity).</summary>
public class TextScenesTests
{
    [Fact]
    public void NewsAndBoardsAreTextScenes()
    {
        var text = typeof(MatrixScene).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ITextScene).IsAssignableFrom(t))
            .Select(t => ((IScene)Activator.CreateInstance(t)!).Id)
            .OrderBy(s => s)
            .ToArray();
        Assert.Equal(new[] { "bbs", "sattv", "teletext", "teletype", "weatherboard" }, text);
    }
}
