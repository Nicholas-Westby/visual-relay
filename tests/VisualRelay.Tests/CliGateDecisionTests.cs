using VisualRelay.Cli.Gates;

namespace VisualRelay.Tests;

/// <summary>
/// Pure-decision tests for the launch gate's OS-aware behavior. On macOS/Linux
/// nono on PATH is the hard prerequisite (exit 127 when missing). On Windows the
/// sandbox is nono inside a WSL2 distro, so the decision is delegated to the WSL
/// gate and made from the probe: a usable probe proceeds, anything else exits 127
/// with the WSL message. Asserted via the extracted <c>Decide</c> function so
/// every branch is covered on any OS.
/// </summary>
public sealed class CliGateDecisionTests
{
    // ── NonoGate ─────────────────────────────────────────────────────────

    [Fact]
    public void Nono_Present_NonWindows_Proceeds()
    {
        var (exitCode, message) = NonoGate.Decide(onPath: true, isWindows: false, probe: null);

        Assert.Equal(0, exitCode);
        Assert.Null(message);
    }

    [Fact]
    public void Nono_Missing_NonWindows_HardFails127()
    {
        var (exitCode, message) = NonoGate.Decide(onPath: false, isWindows: false, probe: null);

        Assert.Equal(127, exitCode);
        Assert.NotNull(message);
        Assert.Contains("nono", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Windows_UsableWslProbe_Proceeds()
    {
        // nono on the Windows PATH is irrelevant: the sandbox binary lives inside the distro.
        var (exitCode, message) = NonoGate.Decide(onPath: false, isWindows: true, WslProbeFixtures.Usable());

        Assert.Equal(0, exitCode);
        Assert.Null(message);
    }

    [Fact]
    public void Windows_WithoutAUsableWslProbe_HardFails127WithTheWslMessage()
    {
        // The old Windows arm proceeded silently and left the sandbox to a
        // different runtime; now WSL2 + nono is the Windows sandbox and its
        // absence blocks the launch exactly like a missing nono does on Unix.
        var (exitCode, message) = NonoGate.Decide(onPath: false, isWindows: true, WslProbeFixtures.NoWsl());

        Assert.Equal(127, exitCode);
        Assert.NotNull(message);
        Assert.Contains("wsl --install", message);
        Assert.Contains("Windows-only toolchain", message);
    }

    [Fact]
    public void Windows_NonoOnTheWindowsPath_DoesNotSubstituteForTheDistro()
    {
        var (exitCode, _) = NonoGate.Decide(onPath: true, isWindows: true, WslProbeFixtures.NonoMissing());

        Assert.Equal(127, exitCode);
    }

    [Fact]
    public void Windows_NotProbed_IsTreatedAsWslMissing()
    {
        var (exitCode, message) = NonoGate.Decide(onPath: true, isWindows: true, probe: null);

        Assert.Equal(127, exitCode);
        Assert.Contains("wsl --install", message);
    }

    [Fact]
    public void Nono_Missing_NonWindows_InstallMessagePointsToNolabsAiOrg()
    {
        // After the nono 0.66.0 org migration from jedisct1 → nolabs-ai, the install
        // hint in the error message must point users to the current org.
        var (_, message) = NonoGate.Decide(onPath: false, isWindows: false, probe: null);

        Assert.Contains("https://github.com/nolabs-ai/nono", message);
    }

    [Fact]
    public void Nono_Missing_NonWindows_InstallMessageDoesNotPointToStaleOrg()
    {
        // The install hint must NOT reference the stale jedisct1/nono org.
        var (_, message) = NonoGate.Decide(onPath: false, isWindows: false, probe: null);

        Assert.DoesNotContain("jedisct1/nono", message);
    }
}
