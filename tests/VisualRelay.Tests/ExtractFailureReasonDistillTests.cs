using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

// Direct unit tests for the distiller extensions added in Task 4:
// (1) nono advisory noise (Verified N pack(s), bare deny_* lines) is stripped,
// (2) real test-failure lines (Failed …, \bFAIL\b) are anchored as strong signals,
// (3) benign "0 failed" summaries do NOT anchor.
// Companion to the existing ExtractFailureReason_* tests in
// SwivalSubagentRunnerToolPreflightTests.cs; in a separate file to keep both
// files under the 300-line guard.
public sealed class ExtractFailureReasonDistillTests
{
    [Fact]
    public void ExtractFailureReason_VerifyOutput_StripsPackAndDenyNoise_KeepsFailedTest()
    {
        // Spec acceptance: deny_* and "Verified N pack(s)" gone; the real "Failed X" present.
        var output = string.Join('\n', new[]
        {
            "deny_read_user_home",                                  // bare advisory (no bypass phrase)
            "'/Users/me/.ssh' is blocked by 'deny_credentials'; use --bypass-protection /Users/me/.ssh to allow access",
            "Verified 1 pack(s)",
            "bun test v1.x",
            "Failed JobFinder > parses Ashby — expected 3 but got 0",
        });

        var reason = SwivalSubagentRunner.ExtractFailureReason(output);

        Assert.DoesNotContain("deny_read_user_home", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("deny_credentials", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("bypass-protection", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Verified 1 pack(s)", reason, StringComparison.Ordinal);
        // The real failing test must be retained AND lead the reason (anchored).
        Assert.Contains("Failed JobFinder > parses Ashby", reason, StringComparison.Ordinal);
        Assert.StartsWith("Failed JobFinder", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractFailureReason_KeychainAdvisoryTrailsSummary_KeepsSummaryDropsAdvisory()
    {
        // nono prints its STANDING keychain/system-services advisory AFTER the runner's
        // own summary, so an unfiltered tail lands on the advisory and truncates the real
        // failure away. The advisory block + its hint lines must be dropped so the
        // strong "Failed!" summary anchors and survives.
        var output = string.Join('\n', new[]
        {
            "Verified 1 pack(s)",
            "Failed PaymentTest > charges card — expected 200 but got 500",
            "Failed!  - Failed:     3, Passed:  1860, Skipped:     0, Total:  1863, Duration: 12 s",
            "system services: mach-lookup (com.apple.SecurityServer) — Keychain / Security framework",
            "Keychain access requires granting the login keychain path: --read-file ~/Library/Keychains/login.keychain-db",
            "Next steps:",
            "  Discover paths: nono learn -p vr-guard -- dotnet test",
            "  Query policy:   nono why -p vr-guard --op read --path ~/Library/Keychains/login.keychain-db",
            "  --allow ~/Library/Keychains/login.keychain-db",
            "  --read-file ~/Library/Keychains/login.keychain-db",
            "  --write ~/Library/Keychains/login.keychain-db",
            "  nono learn -p vr-guard",
            "  nono why -p vr-guard",
        });

        var reason = SwivalSubagentRunner.ExtractFailureReason(output);

        // The runner's own failure leads and survives.
        Assert.StartsWith("Failed PaymentTest", reason, StringComparison.Ordinal);
        Assert.Contains("Failed!  - Failed:     3", reason, StringComparison.Ordinal);
        // The standing keychain/system-services advisory and its hint lines are gone.
        Assert.DoesNotContain("mach-lookup", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("com.apple.SecurityServer", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Keychain access requires", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("login.keychain-db", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Library/Keychains", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Next steps:", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("nono learn", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("nono why", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractFailureReason_GreenRunWithKeychainAdvisoryOnly_DistillsToPlaceholder()
    {
        // A green run carries only nono's standing advisory (no failure). Once filtered,
        // nothing diagnostic remains, so the distiller yields the no-output placeholder —
        // never the keychain advisory dressed up as a reason.
        var output = string.Join('\n', new[]
        {
            "Verified 1 pack(s)",
            "system services: mach-lookup (com.apple.SecurityServer) — Keychain / Security framework",
            "Keychain access requires granting the login keychain path: --read-file ~/Library/Keychains/login.keychain-db",
            "Next steps:",
            "  --read-file ~/Library/Keychains/login.keychain-db",
            "  nono why -p vr-guard",
        });

        var reason = SwivalSubagentRunner.ExtractFailureReason(output);

        Assert.Equal("(no diagnostic output captured)", reason);
    }

    [Fact]
    public void ExtractFailureReason_BenignZeroFailedSummary_DoesNotAnchorOnIt()
    {
        // A "0 failed" summary line is benign; it must NOT become the anchor. With no
        // real failure line present, the extractor falls back to the surviving tail.
        var output = string.Join('\n', new[]
        {
            "Verified 1 pack(s)",
            "Test Files  12 passed (12)",
            "Tests  340 passed | 0 failed",
            "wall-clock ceiling exceeded: 61s > 60s budget",
        });

        var reason = SwivalSubagentRunner.ExtractFailureReason(output);

        Assert.DoesNotContain("Verified 1 pack(s)", reason, StringComparison.Ordinal);
        // The non-test gate line (the real cause) survives as the tail.
        Assert.Contains("wall-clock ceiling exceeded", reason, StringComparison.Ordinal);
    }
}
