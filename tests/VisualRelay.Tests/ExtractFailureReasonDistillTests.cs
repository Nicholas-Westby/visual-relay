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
