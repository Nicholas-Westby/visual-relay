using VisualRelay.Cli.Commands;

namespace VisualRelay.Tests;

/// <summary>
/// Pure-decision tests for <see cref="LaunchCommand"/>'s OS-aware backend
/// autostart. On macOS/Linux the best-effort backend start runs as before. On
/// Windows it is skipped in Phase 0 — the backend lifecycle still spawns the
/// proxy through <c>/bin/sh</c> (a <c>Win32Exception</c> there), and Phase 0 is
/// about opening the window only; Phase 2 makes the Windows backend real and
/// flips this on.
/// </summary>
public sealed class LaunchCommandTests
{
    [Fact]
    public void ShouldStartBackend_NonWindows_True()
    {
        Assert.True(LaunchCommand.ShouldStartBackend(isWindows: false));
    }

    [Fact]
    public void ShouldStartBackend_Windows_FalseInPhase0()
    {
        Assert.False(LaunchCommand.ShouldStartBackend(isWindows: true));
    }
}
