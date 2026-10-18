using Phosphor.App;

namespace Phosphor.App.Tests;

public class LogPruneTests
{
    [Theory]
    [InlineData("phosphor-1234.log", true)]
    [InlineData("phosphor-1234-2.log", true)]
    [InlineData("phosphor-preview-77.json", true)]
    [InlineData("phosphor.log", false)]
    [InlineData("phosphor-settings-probe.json", false)]
    [InlineData("phosphor-atlas.png", false)]
    [InlineData("phosphor-1234.log.bak", false)]
    public void IsOurs_OnlyPidLogsAndPreviewConfigs(string name, bool expected) =>
        Assert.Equal(expected, LogPrune.IsOurs(name));

    [Fact]
    public void Run_DeletesOnlyOldPidFiles_KeepsOwnAndFresh()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phosphor-prune-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var now = DateTime.UtcNow;
            string Make(string name, double ageDays)
            {
                var p = Path.Combine(dir, name);
                File.WriteAllText(p, "x");
                File.SetLastWriteTimeUtc(p, now - TimeSpan.FromDays(ageDays));
                return p;
            }
            var oldLog = Make("phosphor-100.log", 5);
            var oldPreview = Make("phosphor-preview-100.json", 5);
            var freshLog = Make("phosphor-200.log", 1);
            var own = Make("phosphor-300.log", 9);
            var shared = Make("phosphor.log", 9);
            var foreign = Make("phosphor-notes.txt", 9);

            Assert.Equal(2, LogPrune.Run(dir, now, own));

            Assert.False(File.Exists(oldLog));
            Assert.False(File.Exists(oldPreview));
            Assert.True(File.Exists(freshLog));
            Assert.True(File.Exists(own));
            Assert.True(File.Exists(shared));
            Assert.True(File.Exists(foreign));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Run_MissingDir_IsQuiet() =>
        Assert.Equal(0, LogPrune.Run(Path.Combine(Path.GetTempPath(), "phosphor-no-such-" + Guid.NewGuid().ToString("N")), DateTime.UtcNow, null));
}
