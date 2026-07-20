using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Regression tests for concurrent-runner (Swift Testing style) verify-output
/// distilling: mid-log failure rows must anchor the reason so the GUI banner,
/// NEEDS-REVIEW headline, and flag reason actually name the failing test.
/// </summary>
public sealed class ExtractFailureReasonConcurrentTests
{
    // ── Incident fixture ─────────────────────────────────────────────────

    [Fact]
    public void ConcurrentTestLog_FirstLineNamesFailingTest()
    {
        // Reconstruct the patternsmith incident's log shape:
        // - A small setup prefix
        // - ~200 "Test … passed after …" lines
        // - One mid-log "Test … recorded an issue … Expectation failed: …"
        // - Another ~200 "Test … passed after …" lines (passing tail)
        // - A "Test run … failed after … with 1 issue." summary near EOF
        // - A trailing JSON session epilogue
        // The real failure is in the MIDDLE of a 746-line concurrent log.
        var lines = new List<string>();

        // Setup / banner lines
        lines.Add("Build complete!");
        lines.Add("Test run started…");

        // ~200 passing tests BEFORE the failure (mid-log)
        for (var i = 1; i <= 200; i++)
            lines.Add($"Test passingTest{i:D4}() passed after 0.{i % 100:D3} seconds.");

        // The real failure line — mid-log, the one that must survive
        lines.Add("Test everySourceFileUnder200Lines() recorded an issue at FileSizeTests.swift:19:9: Expectation failed: (offenders → [\"Sources/RegexUI/ViewModels/AppModel.swift — 210 lines\"]).isEmpty → false");

        // ~200 passing tests AFTER the failure (passing tail)
        for (var i = 201; i <= 400; i++)
            lines.Add($"Test passingTest{i:D4}() passed after 0.{i % 100:D3} seconds.");

        // The run summary near EOF
        lines.Add("Test run with 244 tests in 25 suites failed after 0.437 seconds with 1 issue.");

        // A trailing JSON epilogue (session / telemetry)
        lines.Add("{\"session\": {\"id\": \"abc123\", \"total\": 244}}");

        var output = string.Join('\n', lines);

        var reason = SwivalSubagentRunner.ExtractFailureReason(output);

        // The first line of the reason MUST name the failing test.
        Assert.Contains("everySourceFileUnder200Lines", reason, StringComparison.Ordinal);

        // The first line must NOT be a mid-word fragment of a passing test's duration.
        Assert.DoesNotContain("passed after", reason.Split('\n')[0], StringComparison.Ordinal);

        // The reason must contain the actual failure anchor text.
        Assert.Contains("recorded an issue", reason, StringComparison.Ordinal);
    }

    // ── Anchored-but-buried regression ───────────────────────────────────

    [Fact]
    public void AnchoredButBuried_FailureLineSurvivesLongPassingTail()
    {
        // A "recorded an issue" failure line followed by MORE than 600 chars of
        // passing lines. The failure must still be the FIRST line of the reason;
        // tail-keeping would evict it entirely.
        var lines = new List<string>
        {
            "Test buriedFailure() recorded an issue at Tests.swift:5:1: Expectation failed: (x → 1) == (expected → 2)",
        };

        // > 1000 chars of passing-test noise after the failure
        for (var i = 1; i <= 50; i++)
            lines.Add($"Test passing{i:D4}() passed after 0.{i:D3} seconds. Padding to make this line longer so the tail definitely exceeds 600 chars. Extra filler here.");

        var output = string.Join('\n', lines);

        var reason = SwivalSubagentRunner.ExtractFailureReason(output);

        // The first line must be the failure test name — not a mid-word passing fragment.
        Assert.Contains("buriedFailure", reason, StringComparison.Ordinal);
        Assert.Contains("recorded an issue", reason, StringComparison.Ordinal);

        // The first line must NOT be from a "passed after" line.
        Assert.DoesNotContain("passed after", reason.Split('\n')[0], StringComparison.Ordinal);
    }

    // ── Benign guard ────────────────────────────────────────────────────

    [Fact]
    public void BenignZeroFailedAndPassing_KeepsTailFallback()
    {
        // Output containing only "0 failed" summaries and passing lines —
        // nothing should anchor. The distiller falls back to the existing
        // tail behavior (TrimForTail).
        var output = string.Join('\n', new[]
        {
            "Verified 1 pack(s)",
            "Executed 0 tests, with 0 failures (0 unexpected) in 0.000 seconds",
            "Test Files  12 passed (12)",
            "Tests  340 passed | 0 failed",
            "wall-clock ceiling exceeded: 61s > 60s budget",
        });

        var reason = SwivalSubagentRunner.ExtractFailureReason(output);

        // The non-test gate line (the real cause) survives as the tail.
        Assert.Contains("wall-clock ceiling exceeded", reason, StringComparison.Ordinal);

        // Benign summaries must NOT be present — they were noise-dropped or
        // are just part of the tail.
        Assert.DoesNotContain("Verified 1 pack(s)", reason, StringComparison.Ordinal);

        // The "0 failed" / "0 failures" summary must NOT anchor.
        // (Today: benign. After the change: still benign — the new markers
        // must not match these.)
    }

    // ── Existing strong-marker unchanged ─────────────────────────────────

    [Fact]
    public void ExistingStrongMarker_CommandNotFound_StillBehavesAsBefore()
    {
        // The existing strong marker "command not found" must anchor and
        // lead the reason exactly as it does today. The head-first change
        // preserves this because the anchored block fits within the budget.
        var output = string.Join('\n', new[]
        {
            "WARN '/Users/me/.ssh' is blocked by 'deny_credentials'; use --bypass-protection /Users/me/.ssh to allow access",
            "nono: command not found: swival",
        });

        var reason = SwivalSubagentRunner.ExtractFailureReason(output);

        Assert.Contains("command not found", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("bypass-protection", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("deny_credentials", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingStrongMarker_UppercaseFail_StillBehavesAsBefore()
    {
        // Uppercase \bFAIL\b (bun/jest output) must anchor and lead the
        // reason exactly as before.
        var output = string.Join('\n', new[]
        {
            "bun test v1.x",
            "FAIL src/__tests__/JobFinder.test.ts",
            "  Expected 3 but got 0",
        });

        var reason = SwivalSubagentRunner.ExtractFailureReason(output);

        Assert.Contains("FAIL", reason, StringComparison.Ordinal);
        Assert.Contains("JobFinder", reason, StringComparison.Ordinal);
    }
}
