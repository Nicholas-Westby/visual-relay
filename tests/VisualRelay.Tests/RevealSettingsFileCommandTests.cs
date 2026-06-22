using System.Runtime.InteropServices;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class RevealSettingsFileCommandTests
{
    [Fact]
    public void ResolvedPath_ProducesOpenDashR_OnMacOs()
    {
        var path = KeyEnvFile.ResolvePathForCurrentUser();

        var (fileName, arguments) = FileReveal.BuildCommand(path, OSPlatform.OSX);

        Assert.Equal("open", fileName);
        Assert.Equal(new[] { "-R", path }, arguments);
    }

    [Fact]
    public void ResolvedConfigDir_PathIsUnderVisualRelayConfig()
    {
        var path = KeyEnvFile.ResolvePathForCurrentUser();

        var directory = Path.GetDirectoryName(path)!;
        var fileName = Path.GetFileName(path);

        Assert.EndsWith("visual-relay", directory, StringComparison.Ordinal);
        Assert.Equal(".env", fileName);
    }
}
