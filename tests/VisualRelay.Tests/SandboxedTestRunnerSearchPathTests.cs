using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A search path handed to a sandboxed run is put ahead of the value the command would otherwise
/// see by the shell that runs the command, so what the user's environment or the distro already
/// holds is kept on either arm. The verify snapshot uses it to import its own editable Python
/// project (<see cref="PythonEditableImports"/>).
/// </summary>
public sealed class SandboxedTestRunnerSearchPathTests
{
    private static readonly Dictionary<string, string> SnapshotSrc = new() { ["PYTHONPATH"] = "/tmp/snap/src" };

    [Fact]
    public void TheLocalLaunch_RunsTheCommandBehindTheSearchPath()
    {
        var sut = new SandboxedTestRunner(new ShellTestRunner(), TestConfig(), host: SandboxHost.Local);

        var launch = sut.ResolveSandboxedLaunch("pytest -q", "/home/alice/repo", SnapshotSrc);

        Assert.Equal(["/bin/sh", "-c", SandboxedTestRunner.WithSearchPaths(SnapshotSrc, "pytest -q")], launch.Arguments.TakeLast(3));
    }

    [Fact]
    public void TheDistroLaunch_RunsTheCommandBehindTheSearchPath()
    {
        var context = new WslContext(@"C:\Windows\System32\wsl.exe", "Ubuntu", "/usr/local/bin/nono", "/home/alice");
        var sut = new SandboxedTestRunner(new ShellTestRunner(), TestConfig(), host: SandboxHost.Windows(context));

        var launch = sut.ResolveSandboxedLaunch("pytest -q", @"\\wsl.localhost\Ubuntu\home\alice\repo", SnapshotSrc);

        Assert.Equal(["/bin/sh", "-c", SandboxedTestRunner.WithSearchPaths(SnapshotSrc, "pytest -q")], launch.Arguments.TakeLast(3));
    }

    [Fact]
    public void NoSearchPaths_LeaveTheCommandAsItIs()
    {
        Assert.Equal("pytest -q", SandboxedTestRunner.WithSearchPaths(new Dictionary<string, string>(), "pytest -q"));
    }

    [Theory]
    [InlineData("/opt/sdk", "/tmp/it's here/src:/opt/sdk")]
    [InlineData(null, "/tmp/it's here/src")]
    public async Task TheShellLine_PutsTheSearchPathAheadOfWhatTheCommandInherits(string? inherited, string expected)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "runs a POSIX shell");
        var line = SandboxedTestRunner.WithSearchPaths(
            new Dictionary<string, string> { ["PYTHONPATH"] = "/tmp/it's here/src" }, "printf '%s' \"$PYTHONPATH\"");

        var (exitCode, output, _) = await ProcessCapture.RunAsync(
            "/bin/sh", ["-c", line], Path.GetTempPath(), TimeSpan.FromSeconds(30), CancellationToken.None,
            environment: inherited is null ? null : new Dictionary<string, string> { ["PYTHONPATH"] = inherited },
            envRemove: inherited is null ? new HashSet<string> { "PYTHONPATH" } : null);

        Assert.Equal(0, exitCode);
        Assert.Equal(expected, output.TrimEnd());
    }

    private static RelayConfig TestConfig() =>
        new("llm-tasks", "true", "true", [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            true, 1, 1, false, true,
            SubagentTimeoutMilliseconds: 5_000,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int> { ["cheap"] = 90_000 },
            FirstOutputTimeoutMs: 660_000);
}
