using Phosphor.Core;

namespace Phosphor.App.Tests;

public class TextConfigTests
{
    [Fact]
    public void DefaultFont_IsConsolas()
    {
        Assert.Equal("Consolas", PhosphorConfig.Parse(PhosphorConfig.DefaultJson).Text.Font);
        Assert.Equal("Consolas", PhosphorConfig.Parse("{}").Text.Font);
    }

    [Fact]
    public void FontFromJson_IsRead()
    {
        var cfg = PhosphorConfig.Parse("{ \"text\": { \"font\": \"IBM Plex Mono\" } }");
        Assert.Equal("IBM Plex Mono", cfg.Text.Font);
    }

    [Fact]
    public void MissingFont_IsKeptAsWritten_AndDoesNotThrow()
    {
        // Несуществующий шрифт не ошибка конфига: при растеризации — Consolas с предупреждением (см. Phosphor.Text.Tests)
        var warnings = new List<string>();
        var cfg = PhosphorConfig.Parse("{ \"text\": { \"font\": \"НетТакогоШрифта\" } }");
        cfg.Validate(warnings);
        Assert.Empty(warnings);
        Assert.Equal("НетТакогоШрифта", cfg.Text.Font);
    }
}
