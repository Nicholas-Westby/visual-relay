using VisualRelay.Cli.Gates;

namespace VisualRelay.Tests;

/// <summary>
/// Pure-decision tests for the launch gates' OS-aware behavior. The gates are
/// hard prerequisites on macOS/Linux (exit 127 when missing) but must not block
/// the GUI on Windows, where the OS sandbox (nono) does not exist and swival is
/// only needed to <em>run</em> stages (Phase 3), not to open the window for
/// inspection. Asserted via the extracted <c>Decide</c> functions so the Windows
/// branch is covered on any OS.
/// </summary>
public sealed class CliGateDecisionTests
{
    // ── NonoGate ─────────────────────────────────────────────────────────

    [Fact]
    public void Nono_Present_AnyOs_Proceeds()
    {
        Assert.Equal(0, NonoGate.Decide(onPath: true, isWindows: false).ExitCode);
        Assert.Equal(0, NonoGate.Decide(onPath: true, isWindows: true).ExitCode);
        Assert.Null(NonoGate.Decide(onPath: true, isWindows: true).Message);
    }

    [Fact]
    public void Nono_Missing_NonWindows_HardFails127()
    {
        var (exitCode, message) = NonoGate.Decide(onPath: false, isWindows: false);

        Assert.Equal(127, exitCode);
        Assert.NotNull(message);
        Assert.Contains("nono", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nono_Missing_Windows_ProceedsWithInspectionNote()
    {
        var (exitCode, message) = NonoGate.Decide(onPath: false, isWindows: true);

        Assert.Equal(0, exitCode);
        Assert.NotNull(message);
        Assert.Contains("Windows", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inspection", message, StringComparison.OrdinalIgnoreCase);
    }

    // ── SwivalGate ───────────────────────────────────────────────────────

    [Fact]
    public void Swival_Present_AnyOs_Proceeds()
    {
        Assert.Equal(0, SwivalGate.Decide(onPath: true, isWindows: false).ExitCode);
        Assert.Equal(0, SwivalGate.Decide(onPath: true, isWindows: true).ExitCode);
    }

    [Fact]
    public void Swival_Missing_NonWindows_HardFails127WithTapHint()
    {
        var (exitCode, message) = SwivalGate.Decide(onPath: false, isWindows: false);

        Assert.Equal(127, exitCode);
        Assert.NotNull(message);
        Assert.Contains("swival/tap/swival", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Swival_Missing_Windows_SoftWarns_NoBrewAssumption_Proceeds()
    {
        var (exitCode, message) = SwivalGate.Decide(onPath: false, isWindows: true);

        Assert.Equal(0, exitCode);
        Assert.NotNull(message);
        Assert.DoesNotContain("brew", message, StringComparison.OrdinalIgnoreCase);
    }
}
