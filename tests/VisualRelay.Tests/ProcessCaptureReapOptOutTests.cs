using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Guards that <see cref="ProcessCapture"/> honours the
/// <c>reapProcessTree</c> opt-out on the normal-exit path.
/// </summary>
public sealed class ProcessCaptureReapOptOutTests
{
    /// <summary>
    /// With <c>reapProcessTree:false</c> the normal-exit path still completes: a
    /// trivial child exits 0, does not hit the timeout, and its captured output is
    /// exactly what the child wrote (nothing).
    /// </summary>
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
        // /usr/bin/true writes to neither stream, so the capture must come back empty.
        Assert.Equal("", output);
    }

    /// <summary>
    /// The default (reaping) path behaves identically for a trivial child — the
    /// control case that proves the opt-out above changes nothing observable.
    /// </summary>
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
        // The reaping path must not inject anything of its own into the capture.
        Assert.Equal("", output);
    }
}
