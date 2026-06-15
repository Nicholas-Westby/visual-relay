using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class TestCommandValidatorTests
{
    // ── Classify (static pure function) ────────────────────────────────

    [Fact]
    public void Classify_ExitZero_Accepts()
    {
        var result = TestCommandValidator.Classify(new TestRunResult(0, "322 tests passed"));

        Assert.True(result.Accepted);
        Assert.Null(result.RejectionReason);
        Assert.Equal(0, result.RunResult.ExitCode);
        Assert.Contains("322 tests", result.RunResult.Output);
    }

    [Fact]
    public void Classify_NonZeroExitWithOutput_Accepts()
    {
        // Runner is proven because it produced test output — tests may
        // legitimately fail, but the command itself is real.
        var result = TestCommandValidator.Classify(new TestRunResult(1, "FAIL: 3 tests failed\n  1) add works"));

        Assert.True(result.Accepted);
        Assert.Null(result.RejectionReason);
    }

    [Fact]
    public void Classify_TimedOut_Rejects()
    {
        var result = TestCommandValidator.Classify(
            new TestRunResult(-1, "", TimedOut: true));

        Assert.False(result.Accepted);
        Assert.NotNull(result.RejectionReason);
        Assert.Contains("timeout", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_TimedOutWithPartialOutput_Rejects()
    {
        // Even if the process managed to emit output before timing out,
        // the timeout means the command is not reliably runnable.
        var result = TestCommandValidator.Classify(
            new TestRunResult(-1, "starting tests...\n", TimedOut: true));

        Assert.False(result.Accepted);
        Assert.NotNull(result.RejectionReason);
    }

    [Fact]
    public void Classify_Exit127NoOutput_RejectsAsCommandNotFound()
    {
        // Exit code 127 is the shell convention for "command not found".
        var result = TestCommandValidator.Classify(new TestRunResult(127, ""));

        Assert.False(result.Accepted);
        Assert.NotNull(result.RejectionReason);
        Assert.Contains("not found", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_NonZeroExitNoOutput_RejectsAsUsageError()
    {
        // Non-zero exit with no output suggests the command started but
        // printed a usage/help message to a stream we didn't capture, or
        // the binary failed its own arg validation before producing
        // test-style output.
        var result = TestCommandValidator.Classify(new TestRunResult(2, ""));

        Assert.False(result.Accepted);
        Assert.NotNull(result.RejectionReason);
    }

    [Fact]
    public void Classify_NonZeroExitWhitespaceOnlyOutput_Rejects()
    {
        // Whitespace-only output is treated the same as empty — usage error.
        var result = TestCommandValidator.Classify(
            new TestRunResult(1, "   \n  \n "));

        Assert.False(result.Accepted);
    }

    // ── ValidateAsync (integration with ITestRunner) ───────────────────

    [Fact]
    public async Task ValidateAsync_ExitZero_Accepts()
    {
        var runner = new ScriptedTestRunner(new TestRunResult(0, "all green"));
        var validator = new TestCommandValidator(runner);

        var result = await validator.ValidateAsync("/tmp/repo", "cargo test");

        Assert.True(result.Accepted);
        Assert.Null(result.RejectionReason);
    }

    [Fact]
    public async Task ValidateAsync_FailingTestsWithOutput_Accepts()
    {
        // (b) A runner that exits non-zero WITH test output proves the
        // command is real — tests may fail, but the runner is validated.
        var runner = new ScriptedTestRunner(
            new TestRunResult(1, "2 passed, 1 failed\n  × add(1,2)"));
        var validator = new TestCommandValidator(runner);

        var result = await validator.ValidateAsync("/tmp/repo", "bun test");

        Assert.True(result.Accepted);
    }

    [Fact]
    public async Task ValidateAsync_Exit127NoOutput_Rejects()
    {
        // (a) Simulates pytest not being installed — command-not-found
        // should reject so the next candidate is tried.
        var runner = new ScriptedTestRunner(new TestRunResult(127, ""));
        var validator = new TestCommandValidator(runner);

        var result = await validator.ValidateAsync("/tmp/repo", "pytest");

        Assert.False(result.Accepted);
        Assert.NotNull(result.RejectionReason);
    }

    [Fact]
    public async Task ValidateAsync_TimedOut_Rejects()
    {
        var runner = new TimeoutSimulatingTestRunner();
        var validator = new TestCommandValidator(runner);

        var result = await validator.ValidateAsync("/tmp/repo", "pytest");

        Assert.False(result.Accepted);
        Assert.NotNull(result.RejectionReason);
    }

    [Fact]
    public async Task ValidateAsync_NonZeroExitNoOutput_Rejects()
    {
        // Usage error: the command exists but wasn't called correctly.
        var runner = new ScriptedTestRunner(new TestRunResult(2, ""));
        var validator = new TestCommandValidator(runner);

        var result = await validator.ValidateAsync("/tmp/repo", "pytest --typo");

        Assert.False(result.Accepted);
    }

    // ── Fallback chain (exhaustion scenario) ───────────────────────────

    [Fact]
    public async Task ValidateAsync_FirstCandidateRejected_SecondAccepted()
    {
        // (a) Full scenario: pytest (exit 127) → rejected, bun test
        // (exit 0 with output) → accepted.  This is the fix for the JobFinder
        // bug: pytest must NOT be persisted when it can't even start.
        var runner = new ScriptedTestRunner(
            new TestRunResult(127, ""), // pytest: command not found
            new TestRunResult(0, "322 tests, 0.2s")); // bun test: real runner

        var validator = new TestCommandValidator(runner);

        var r1 = await validator.ValidateAsync("/tmp/repo", "pytest");
        Assert.False(r1.Accepted);
        Assert.Contains("not found", r1.RejectionReason, StringComparison.OrdinalIgnoreCase);

        var r2 = await validator.ValidateAsync("/tmp/repo", "bun test");
        Assert.True(r2.Accepted);
        Assert.Null(r2.RejectionReason);
    }

    [Fact]
    public async Task ValidateAsync_AllCandidatesExhausted_AllRejected()
    {
        // (c) When every candidate fails validation, the caller must write
        // null and surface a message — never persist an unproven guess.
        var runner = new ScriptedTestRunner(
            new TestRunResult(127, ""),  // pytest not found
            new TestRunResult(127, ""),  // bun test not found
            new TestRunResult(2, ""));    // npm test: usage error

        var validator = new TestCommandValidator(runner);

        var r1 = await validator.ValidateAsync("/tmp/repo", "pytest");
        var r2 = await validator.ValidateAsync("/tmp/repo", "bun test");
        var r3 = await validator.ValidateAsync("/tmp/repo", "npm test");

        Assert.False(r1.Accepted);
        Assert.False(r2.Accepted);
        Assert.False(r3.Accepted);
    }

    // ── Validated command carried forward ──────────────────────────────

    [Fact]
    public async Task ValidateAsync_Accepted_RunResultPreserved()
    {
        // (d) The validated command's RunResult is preserved so the caller
        // can surface a summary like "testCmd validated: bun test (322 tests, 0.2s)".
        var expectedOutput = "322 tests passed in 0.2s\n";
        var runner = new ScriptedTestRunner(
            new TestRunResult(0, expectedOutput));
        var validator = new TestCommandValidator(runner);

        var result = await validator.ValidateAsync("/tmp/repo", "bun test");

        Assert.True(result.Accepted);
        Assert.Equal(0, result.RunResult.ExitCode);
        Assert.Equal(expectedOutput, result.RunResult.Output);
        Assert.False(result.RunResult.TimedOut);
    }

    // ── Cancellation ───────────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_CancellationPropagates()
    {
        // The cancellation token is forwarded to ITestRunner so a hung
        // validation can be aborted from the outside.
        var runner = new ScriptedTestRunner(new TestRunResult(0, "ok"));
        var validator = new TestCommandValidator(runner);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => validator.ValidateAsync("/tmp/repo", "bun test", cts.Token));
    }

    // ── Recording runner verifies command fidelity ─────────────────────

    [Fact]
    public async Task ValidateAsync_PassesExactCommandToRunner()
    {
        // (d) The command string must reach the runner verbatim — no
        // trimming, shell-wrapping, or transformation by the validator.
        var runner = new RecordingTestRunner(new TestRunResult(0, "ok"));
        var validator = new TestCommandValidator(runner);

        await validator.ValidateAsync("/path/to/repo", "dotnet test --filter Category=Unit");

        var call = Assert.Single(runner.Calls);
        Assert.Equal("/path/to/repo", call.RootPath);
        Assert.Equal("dotnet test --filter Category=Unit", call.Command);
    }
}
