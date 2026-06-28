using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Verify/Fix-verify agent-command facts:
///   • B — the read-only Verify stage (9) is NOT handed an imperative full-suite
///     command (that re-tempted the double-run the stage exists to avoid); it gets
///     the captured output only.
///   • D — the Fix-verify stage (10) agent command REBUILDS before testing when a
///     separate buildCmd is configured, so its self-check compiles its edits instead
///     of self-verifying against stale `--no-build` artifacts (a false green).
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

    // ── B: stage 9 (read-only Verify) gets no imperative command ────────

    [Fact]
    public async Task Stage9_Verify_AgentGetsNoImperativeCommand_ButKeepsCapturedOutput()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 1);
        repo.WriteTask("verify-no-cmd", "# Verify no command\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),                  // stage 5 author gate
            new TestRunResult(0, "All 7 tests passed!")); // stage 9 verify — green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "verify-no-cmd");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var inv9 = runner.Invocations.Single(i => i.Stage.Number == 9);
        // No imperative full-suite command handed to the read-only stage.
        Assert.Null(inv9.TestCommand);
        var prompt = SwivalSubagentRunner.BuildPrompt(inv9);
        Assert.DoesNotContain("Run this exact command", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## Verify command", prompt, StringComparison.Ordinal);
        // …but it still receives the harness's captured output to summarize from.
        Assert.NotNull(inv9.LastTestOutput);
        Assert.Contains("All 7 tests passed!", inv9.LastTestOutput, StringComparison.Ordinal);
        Assert.Contains("## Verify output", prompt, StringComparison.Ordinal);
    }

    // ── D: Fix-verify (stage 10) agent command rebuilds ─────────────────

    [Fact]
    public void AgentFixVerifyCommand_WithBuildCommand_PrependsBuild()
    {
        var config = RelayConfigLoader.Defaults("dotnet test --no-build") with { BuildCommand = "dotnet build" };

        Assert.Equal("dotnet build && dotnet test --no-build", RelayDriver.AgentFixVerifyCommand(config));
    }

    [Fact]
    public void AgentFixVerifyCommand_NoBuildCommand_IsTestCommandUnchanged()
    {
        var config = RelayConfigLoader.Defaults("dotnet test");

        Assert.Equal("dotnet test", RelayDriver.AgentFixVerifyCommand(config));
    }

    [Fact]
    public async Task Stage10_FixVerify_AgentCommandIncludesBuild_WhenBuildCmdSet()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test --no-build",
              "logSources": [],
              "baselineVerify": false,
              "maxVerifyLoops": 2,
              "buildCmd": "dotnet build -m:1"
            }
            """);
        repo.WriteTask("fixverify-build", "# Fix-verify rebuilds\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        // Build calls are transparent (always succeed); test calls are red for the
        // stage-5 gate + stage-9 run/retry (3), then green at fix-verify attempt 1.
        var tests = new BuildTransparentTestRunner(buildSentinel: "dotnet build", redTestCalls: 3);
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "fixverify-build");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var inv10 = runner.Invocations.Single(i => i.Stage.Number == 10);
        // The agent-facing self-verify command rebuilds before testing.
        Assert.Equal("dotnet build -m:1 && dotnet test --no-build", inv10.TestCommand);
    }

    // ── G: verify output keeps the tail, not the head ───────────────────

    [Fact]
    public void BuildPrompt_VerifyOutput_KeepsTailSummary_NotHeadNoise()
    {
        var head = "HEAD_NOISE_BANNER " + new string('x', 2000) + " restore/build noise ";
        var tail = "All tests Passed! Failed: 0 TAIL_SUMMARY_MARKER";
        var invocation = Invocation(9, testCommand: null, lastTestOutput: head + tail);

        var prompt = SwivalSubagentRunner.BuildPrompt(invocation);

        Assert.Contains("TAIL_SUMMARY_MARKER", prompt, StringComparison.Ordinal);
        Assert.Contains("Passed!", prompt, StringComparison.Ordinal);
        // The head banner (>600 chars before the tail) is dropped, so the agent is
        // not fed only restore/build noise.
        Assert.DoesNotContain("HEAD_NOISE_BANNER", prompt, StringComparison.Ordinal);
    }
}

/// <summary>
/// Test runner where BUILD calls (command contains <paramref name="buildSentinel"/>)
/// are transparent successes that don't consume the red/green script, and TEST calls
/// are red for the first <paramref name="redTestCalls"/> invocations then green.
/// </summary>
internal sealed class BuildTransparentTestRunner(string buildSentinel, int redTestCalls) : ITestRunner
{
    private int _testCalls;

    public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        if (command.Contains(buildSentinel, StringComparison.Ordinal))
            return Task.FromResult(new TestRunResult(0, "built"));
        _testCalls++;
        return Task.FromResult(_testCalls <= redTestCalls
            ? new TestRunResult(1, "Failed TestX")
            : new TestRunResult(0, "green"));
    }
}
