using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for <see cref="BackendProcess"/>'s OS-dispatched liveness. The Windows
/// branch must use <c>System.Diagnostics.Process</c>-based liveness instead of
/// the <c>libc kill</c> P/Invoke (which throws <c>DllNotFoundException</c> on
/// Windows). The OS dispatch is factored behind an injectable seam so the
/// Windows path is asserted on any OS without spawning a process —
/// <c>Process.GetProcessById</c> is cross-platform, so the current process is
/// "alive" and a reaped pid is "gone" everywhere.
/// </summary>
public sealed class BackendProcessLivenessTests
{
    [Fact]
    public void IsAlive_WindowsBranch_TrueForCurrentProcess()
    {
        Assert.True(BackendProcess.IsAlive(Environment.ProcessId, isWindows: true));
    }

    [Fact]
    public void IsAlive_WindowsBranch_FalseForReapedPid()
    {
        Assert.False(BackendProcess.IsAlive(2_000_000_000, isWindows: true));
    }

    [Fact]
    public void IsAlive_WindowsBranch_FalseForNonPositivePid()
    {
        Assert.False(BackendProcess.IsAlive(0, isWindows: true));
        Assert.False(BackendProcess.IsAlive(-1, isWindows: true));
    }
}
