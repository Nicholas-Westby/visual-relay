using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for the enriched distilled reason that replaces the bare
/// "setup check failure" string with the failing check name, its command,
/// and the first denial path when present.  Also asserts flag-reason
/// truncation safety (first line must stay within 200 chars).
/// </summary>
public sealed class VerifyReasonConstructionTests
{
    // ── Guard red with denial ─────────────────────────────────────────

    [Fact]
    public void BuildSetupCheckFailureReason_GuardRedWithDenial_NamesGuardAndCommandAndDenialPath()
    {
        var denials = new List<SandboxDenial>
        {
            new("file-write-create", "/Volumes/Tera/.TemporaryItems/NSIRD_swift-build_abc")
        };
        var checks = new RelayDriver.SetupCheckResults(
            BootstrapCheck: null,
            BootstrapCommand: null,
            BootstrapOutput: null,
            GuardCheck: "red",
            GuardCommand: "swift build",
            GuardOutput: "error: sandbox",
            NewGuardProbeCheck: null,
            NewGuardProbeOutput: null,
            TestCheck: "green",
            TestCommand: "swift test",
            TestExitCode: 0)
        {
            GuardDenials = denials
        };

        var reason = RelayDriver.BuildSetupCheckFailureReason(checks);

        Assert.Contains("setup check failure", reason);
        Assert.Contains("guard", reason);
        Assert.Contains("swift build", reason);
        Assert.Contains("sandbox denial", reason);
        Assert.Contains("/Volumes/Tera/.TemporaryItems/NSIRD_swift-build_abc", reason);
        Assert.DoesNotContain("bootstrap", reason);
        Assert.DoesNotContain("test", reason);
    }

    // ── Bootstrap red (no denial) ─────────────────────────────────────

    [Fact]
    public void BuildSetupCheckFailureReason_BootstrapRedNoDenial_NamesBootstrapAndCommand()
    {
        var checks = new RelayDriver.SetupCheckResults(
            BootstrapCheck: "red",
            BootstrapCommand: "nix develop",
            BootstrapOutput: "error: flake not found",
            GuardCheck: null,
            GuardCommand: null,
            GuardOutput: null,
            NewGuardProbeCheck: null,
            NewGuardProbeOutput: null,
            TestCheck: "red",
            TestCommand: "bun test",
            TestExitCode: 1);

        var reason = RelayDriver.BuildSetupCheckFailureReason(checks);

        Assert.Contains("setup check failure", reason);
        Assert.Contains("bootstrap", reason);
        Assert.Contains("nix develop", reason);
        Assert.DoesNotContain("sandbox denial", reason);
        Assert.DoesNotContain("guard", reason);
    }

    // ── Test green, guard red ─────────────────────────────────────────

    [Fact]
    public void BuildSetupCheckFailureReason_TestGreenGuardRed_NamesGuardNotTest()
    {
        var denials = new List<SandboxDenial>
        {
            new("file-write-create", "/tmp/denied-path")
        };
        var checks = new RelayDriver.SetupCheckResults(
            BootstrapCheck: null,
            BootstrapCommand: null,
            BootstrapOutput: null,
            GuardCheck: "red",
            GuardCommand: "./tools/guards/check.sh",
            GuardOutput: "violation found",
            NewGuardProbeCheck: null,
            NewGuardProbeOutput: null,
            TestCheck: "green",
            TestCommand: "bun test",
            TestExitCode: 0)
        {
            GuardDenials = denials
        };

        var reason = RelayDriver.BuildSetupCheckFailureReason(checks);

        // The guard is the failing check, not the test.
        Assert.Contains("guard", reason);
        Assert.Contains("check.sh", reason);
        Assert.Contains("sandbox denial", reason);
        Assert.DoesNotContain("bun test", reason);
    }

    // ── Guard red, no denial path ─────────────────────────────────────

    [Fact]
    public void BuildSetupCheckFailureReason_GuardRedNoDenial_OmitsSandboxDenialSuffix()
    {
        var checks = new RelayDriver.SetupCheckResults(
            BootstrapCheck: null,
            BootstrapCommand: null,
            BootstrapOutput: null,
            GuardCheck: "red",
            GuardCommand: "./tools/guards/lint.sh",
            GuardOutput: "lint violations found",
            NewGuardProbeCheck: null,
            NewGuardProbeOutput: null,
            TestCheck: "green",
            TestCommand: "bun test",
            TestExitCode: 0);

        var reason = RelayDriver.BuildSetupCheckFailureReason(checks);

        Assert.Contains("guard", reason);
        Assert.Contains("lint.sh", reason);
        Assert.DoesNotContain("sandbox denial", reason);
    }

    // ── Truncation safety ─────────────────────────────────────────────

    [Fact]
    public void BuildSetupCheckFailureReason_ReasonFirstLine_Within200Chars()
    {
        // The flag-reason path (RelayDriver.VerifyFix.cs:238-239) truncates
        // the first line to 200 chars.  The enriched reason must stay within
        // that bound even with a long denial path.
        var longTarget = new string('x', 300);
        var denials = new List<SandboxDenial>
        {
            new("file-write-create", longTarget)
        };
        var checks = new RelayDriver.SetupCheckResults(
            BootstrapCheck: null,
            BootstrapCommand: null,
            BootstrapOutput: null,
            GuardCheck: "red",
            GuardCommand: "swift build",
            GuardOutput: "denied",
            NewGuardProbeCheck: null,
            NewGuardProbeOutput: null,
            TestCheck: "green",
            TestCommand: "swift test",
            TestExitCode: 0)
        {
            GuardDenials = denials
        };

        var reason = RelayDriver.BuildSetupCheckFailureReason(checks);
        var firstLine = reason.Split('\n')[0];

        Assert.True(firstLine.Length <= 200,
            $"First line is {firstLine.Length} chars; must be ≤ 200 for flag-reason truncation safety.  " +
            $"Line: '{firstLine}'");
    }

    // ── Bootstrap red AND guard red — picks the first failing check ───

    [Fact]
    public void BuildSetupCheckFailureReason_BootstrapAndGuardRed_PicksFirstFailingCheck()
    {
        var checks = new RelayDriver.SetupCheckResults(
            BootstrapCheck: "red",
            BootstrapCommand: "nix develop",
            BootstrapOutput: "error",
            GuardCheck: "red",
            GuardCommand: "swift build",
            GuardOutput: "sandbox denial",
            NewGuardProbeCheck: null,
            NewGuardProbeOutput: null,
            TestCheck: "green",
            TestCommand: "swift test",
            TestExitCode: 0);

        var reason = RelayDriver.BuildSetupCheckFailureReason(checks);

        // Bootstrap is checked first — it should be the named failure.
        Assert.Contains("bootstrap", reason);
        Assert.Contains("nix develop", reason);
        Assert.DoesNotContain("guard", reason);
    }

    // ── All green — returns empty ─────────────────────────────────────

    [Fact]
    public void BuildSetupCheckFailureReason_AllGreen_ReturnsEmpty()
    {
        var checks = new RelayDriver.SetupCheckResults(
            BootstrapCheck: "green",
            BootstrapCommand: "nix develop",
            BootstrapOutput: null,
            GuardCheck: "green",
            GuardCommand: "swift build",
            GuardOutput: null,
            NewGuardProbeCheck: null,
            NewGuardProbeOutput: null,
            TestCheck: "green",
            TestCommand: "swift test",
            TestExitCode: 0);

        var reason = RelayDriver.BuildSetupCheckFailureReason(checks);

        Assert.Equal(string.Empty, reason);
    }

    // ── Null setupChecks ──────────────────────────────────────────────

    [Fact]
    public void BuildSetupCheckFailureReason_NullSetupChecks_ReturnsBareSetupCheckFailure()
    {
        var reason = RelayDriver.BuildSetupCheckFailureReason(null);

        Assert.Equal("setup check failure", reason);
    }
}
