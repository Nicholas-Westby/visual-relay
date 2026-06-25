using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the OS-specific venv executable layout: uv creates
/// <c>Scripts\python.exe</c> / <c>Scripts\litellm.exe</c> on Windows and
/// <c>bin/python</c> / <c>bin/litellm</c> on Unix. The pure
/// <see cref="BackendPaths.VenvExe"/> takes the OS so both layouts are asserted on
/// any OS.
/// </summary>
public sealed class BackendPathsVenvTests
{
    private const string VenvDir = @"C:\data\visual-relay\backend-venv";

    [Fact]
    public void VenvExe_Windows_UsesScriptsAndExeSuffix()
    {
        Assert.Equal(
            Path.Combine(VenvDir, "Scripts", "python.exe"),
            BackendPaths.VenvExe(VenvDir, "python", isWindows: true));
        Assert.Equal(
            Path.Combine(VenvDir, "Scripts", "litellm.exe"),
            BackendPaths.VenvExe(VenvDir, "litellm", isWindows: true));
    }

    [Fact]
    public void VenvExe_Unix_UsesBinAndNoSuffix()
    {
        Assert.Equal(
            Path.Combine(VenvDir, "bin", "python"),
            BackendPaths.VenvExe(VenvDir, "python", isWindows: false));
        Assert.Equal(
            Path.Combine(VenvDir, "bin", "litellm"),
            BackendPaths.VenvExe(VenvDir, "litellm", isWindows: false));
    }
}
