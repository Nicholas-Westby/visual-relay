using System.Runtime.InteropServices;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class FileRevealTests
{
    [Fact]
    public void BuildCommand_OnMacOs_RevealsFileWithOpenDashR()
    {
        const string path = "/Users/dev/.relay/task/stage1-attempt1.report.json";

        var (fileName, arguments) = FileReveal.BuildCommand(path, OSPlatform.OSX);

        Assert.Equal("open", fileName);
        Assert.Equal(new[] { "-R", path }, arguments);
    }

    [Fact]
    public void BuildCommand_OnWindows_SelectsFileWithSingleSlashSelectToken()
    {
        const string path = @"C:\dev\.relay\task\stage1-attempt1.report.json";

        var (fileName, arguments) = FileReveal.BuildCommand(path, OSPlatform.Windows);

        Assert.Equal("explorer", fileName);
        // Explorer wants /select,<path> as one token, not two arguments.
        Assert.Equal(new[] { $"/select,{path}" }, arguments);
    }

    [Fact]
    public void BuildCommand_OnLinux_OpensContainingDirectory()
    {
        var path = Path.Combine("/home", "dev", ".relay", "task", "stage1-attempt1.report.json");
        var directory = Path.GetDirectoryName(path)!;

        var (fileName, arguments) = FileReveal.BuildCommand(path, OSPlatform.Linux);

        Assert.Equal("xdg-open", fileName);
        Assert.Equal(new[] { directory }, arguments);
    }

    [Fact]
    public void BuildCommand_OnLinuxWithBarePath_FallsBackToThePathItself()
    {
        const string path = "task";

        var (fileName, arguments) = FileReveal.BuildCommand(path, OSPlatform.Linux);

        Assert.Equal("xdg-open", fileName);
        Assert.Equal(new[] { path }, arguments);
    }
}
