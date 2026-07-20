using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Guards that <see cref="ProcessCapture"/> honours the
/// <c>reapProcessTree</c> opt-out on the normal-exit path.
/// </summary>
public sealed class ProcessCaptureReapOptOutTests
{
    [Fact]
    public async Task ReapFalse_RunsTrivialCommand()
    {
        var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
            "/usr/bin/true",
            "",
            "/tmp",
            TimeSpan.FromSeconds(5),
            CancellationToken.None,
            reapProcessTree: false);

        Assert.False(timedOut, "Trivial command with reapProcessTree:false should not time out.");
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DefaultReap_RunsTrivialCommand()
    {
        var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
            "/usr/bin/true",
            "",
            "/tmp",
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.False(timedOut, "Trivial command with default reap should not time out.");
        Assert.Equal(0, exitCode);
    }
}
