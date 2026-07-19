using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Verify/Fix-verify agent-command facts:
///   • B — the read-only Verify stage (10) is NOT handed an imperative full-suite
///     command (that re-tempted the double-run the stage exists to avoid); it gets
///     the captured output only.
///   • D — the Fix-verify stage (11) agent is handed the PLAIN test command (which
///     builds AND tests in one pass); single-phase verify never prepends a buildCmd.
///   • G — the `## Verify output` section keeps the TAIL (Passed!/Failed: summary),
///     not the head (sandbox/restore/build banner).
/// </summary>
public sealed class VerifyAgentCommandTests
{
    private static StageInvocation Invocation(int stageNumber, string? testCommand, string? lastTestOutput) =>
        new(
            Stage: RelayStages.All[stageNumber - 1],
            Tier: "balanced",
            RunId: "run-1",
            TargetRoot: "/tmp/root",
            TaskName: "t",
            TaskInput: "# t",
            LedgerSoFar: string.Empty,
            Manifest: ["src/app.cs"],
            LogSources: [],
            TraceDirectory: "/tmp/trace",
            ReportFile: "/tmp/report.json",
            MaxTurns: 200,
            LastTestOutput: lastTestOutput,
            TestCommand: testCommand);

    // ── B: stage 10 (read-only Verify) gets no imperative command ───────

    [Fact]
    public async Task Stage10_Verify_AgentGetsNoImperativeCommand_ButKeepsCapturedOutput()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true);
        repo.WriteTask("verify-no-cmd", "# Verify no command\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),                  // stage 5 author gate
            new TestRunResult(0, "All 7 tests passed!")); // stage 10 verify — green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink(), new NullGitInvoker()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "verify-no-cmd");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var inv10 = runner.Invocations.Single(i => i.Stage.Number == 10);
        // No imperative full-suite command handed to the read-only stage.
        Assert.Null(inv10.TestCommand);
        var prompt = SwivalSubagentRunner.BuildPrompt(inv10);
        Assert.DoesNotContain("Run this exact command", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## Verify command", prompt, StringComparison.Ordinal);
        // …but it still receives the harness's captured output to summarize from.
        Assert.NotNull(inv10.LastTestOutput);
        Assert.Contains("All 7 tests passed!", inv10.LastTestOutput, StringComparison.Ordinal);
        Assert.Contains("## Verify output", prompt, StringComparison.Ordinal);
    }

    // ── D: Fix-verify (stage 11) agent gets the plain test command ──────

    [Fact]
    public async Task Stage11_FixVerify_AgentReceivesPlainTestCommand()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true);
        repo.WriteTask("fixverify-plain", "# Fix-verify uses the plain test command\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),            // stage 5 author gate
            new TestRunResult(1, "Failed TestX"),    // stage 10 verify run
            new TestRunResult(1, "Failed TestX"),    // stage 10 verify retry
            new TestRunResult(1, "Failed TestX"),    // fix-verify attempt 1 gate
            new TestRunResult(0, "green"));          // fix-verify attempt 1 retry → green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink(), new NullGitInvoker()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "fixverify-plain");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var inv11 = runner.Invocations.Single(i => i.Stage.Number == 11);
        // Single-phase verify: the fix-verify agent is handed the plain test command
        // (which builds AND tests in one pass), never a prepended buildCmd && testCmd.
        Assert.Equal("dotnet test", inv11.TestCommand);
    }

    // ── G: verify output keeps the tail, not the head ───────────────────

    [Fact]
    public void BuildPrompt_VerifyOutput_KeepsTailSummary_NotHeadNoise()
    {
        var head = "HEAD_NOISE_BANNER " + new string('x', 2000) + " restore/build noise ";
        var tail = "All tests Passed! Failed: 0 TAIL_SUMMARY_MARKER";
        var invocation = Invocation(10, testCommand: null, lastTestOutput: head + tail);

        var prompt = SwivalSubagentRunner.BuildPrompt(invocation);

        Assert.Contains("TAIL_SUMMARY_MARKER", prompt, StringComparison.Ordinal);
        Assert.Contains("Passed!", prompt, StringComparison.Ordinal);
        // The head banner (>600 chars before the tail) is dropped, so the agent is
        // not fed only restore/build noise.
        Assert.DoesNotContain("HEAD_NOISE_BANNER", prompt, StringComparison.Ordinal);
    }

    // ── Stage 10 command restrictions (observational-only) ──────────────

    [Fact]
    public void Commands_MustNotAllowShellOrTestExecution()
    {
        var verifyStage = RelayStages.All[9]; // 0-indexed
        Assert.Equal(10, verifyStage.Number);

        // The commands value must not be "all" — that grants unrestricted shell access.
        Assert.NotEqual("all", verifyStage.Commands);

        // Split the comma-separated whitelist; no token must grant shell/test execution.
        var tokens = verifyStage.Commands.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(tokens);

        foreach (var forbidden in new[] { "all", "dotnet", "bash", "sh" })
        {
            Assert.DoesNotContain(forbidden, tokens);
        }
    }

    [Fact]
    public void Commands_MustContainReadOnlyTools()
    {
        var verifyStage = RelayStages.All[9]; // 0-indexed
        Assert.Equal(10, verifyStage.Number);

        var tokens = verifyStage.Commands.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(tokens);

        // Stage 10 must include the same read-only subset proven safe for stages 2–4.
        foreach (var required in new[] { "cat", "grep", "ls" })
        {
            Assert.Contains(required, tokens);
        }
    }

    [Fact]
    public void Stage10_Verify_PromptProhibitsTestSuiteExecution()
    {
        var verifyStage = RelayStages.All[9]; // 0-indexed
        Assert.Equal(10, verifyStage.Number);

        // The system prompt must explicitly forbid the agent from running the test suite
        // and from editing files. This is a regression guard — a future edit could weaken
        // these prompts, and combined with commands="all" that would re-open the risk.
        Assert.Contains("Do NOT execute the test suite", verifyStage.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not edit files", verifyStage.SystemPrompt, StringComparison.Ordinal);
    }

    // ── Red verify → Fix-verify routing ─────────────────────────────────

    [Fact]
    public async Task Stage10_Verify_RedMechanicalGate_DoesNotBlockFixVerify()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true);
        repo.WriteTask("red-verify-routes-to-fix", "# Red verify routes to Fix-verify\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        // Stage 5 author gate: red (fail-before-implementation)
        // Stage 10 verify: red + retry (both fail)
        // Fix-verify: green on first try (no retry needed)
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),            // stage 5 first run
            new TestRunResult(1, "red"),            // stage 5 retry
            new TestRunResult(1, "verify failed"),  // stage 10 first run
            new TestRunResult(1, "verify failed"),  // stage 10 retry
            new TestRunResult(0, "All green!"));    // fix-verify first run (pass)
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink(), new NullGitInvoker()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "red-verify-routes-to-fix");

        // The red verify gate must route to Fix-verify, not flag immediately.
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Stage 11 (Fix-verify) was invoked.
        var inv11 = runner.Invocations.Single(i => i.Stage.Number == 11);
        Assert.NotNull(inv11);

        // Stage 10 (Verify) was invoked but received no imperative test command.
        var inv10 = runner.Invocations.Single(i => i.Stage.Number == 10);
        Assert.Null(inv10.TestCommand);
    }
}
