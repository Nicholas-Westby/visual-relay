using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverResumeCommitGateVerifyTests
{
    /// <summary>
    /// A resumed flagged task whose test command FAILS must re-flag with a
    /// verify_result event recorded and must NOT reach the commit stage.
    /// </summary>
    [Fact]
    public async Task RunTaskAsync_Resume_CommitGateWithFailingTest_ReflagsWithVerifyResult()
    {
        // Scenario: stages 1–11 Done, stage 12 Flagged.
        // The commit-gate re-validation runs the test suite isolated.
        // The test command FAILS.
        // Expect: outcome Flagged (NOT Committed), verify_result event with check="red".
        using var repo = TestRepository.Create();
        repo.WriteConfig("exit 1", []);
        repo.WriteTask("fail-resume", "# Fail resume\n");

        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "hello");

        var manifest = new[] { "src/app.cs" };
        var treeHash = RelayDriverResumeTestHelpers.ComputeTreeHash(repo.Root, manifest);
        RelayDriverResumeTestHelpers.SetupCommitGateResumeScenario(
            repo.Root, "fail-resume", manifest, treeHash);

        // Two failing test results — the commit gate re-validation runs through
        // RunTestCommandWithRetryAsync which retries a non-zero exit when
        // RetryFlakyVerify is true (default); both the first run and the retry
        // must fail.
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, new ThrowingSubagentRunner(),
                new ScriptedTestRunner(
                    new TestRunResult(1, "FAIL: TestGateFailed"),
                    new TestRunResult(1, "FAIL: TestGateFailed")), sink),
            new RelayDriverOptions(CreateGitCommit: false, Resume: true));

        var outcome = await driver.RunTaskAsync(repo.Root, "fail-resume");

        // Must flag, not commit.
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);

        // verify_result event must exist with check="red".
        var verifyResult = sink.Events.SingleOrDefault(e => e.EventName == "verify_result");
        Assert.NotNull(verifyResult);
        Assert.Equal("red", verifyResult!.Data?["check"]);
        Assert.Equal("1", verifyResult.Data?["exitCode"]);
        Assert.Contains("FAIL: TestGateFailed", verifyResult.Data?["reason"] ?? "",
            StringComparison.Ordinal);

        // No stage_done for stage 12 must be emitted — we never reached commit.
        Assert.DoesNotContain(sink.Events,
            e => e is { EventName: "stage_done", StageNumber: 12 });
    }

    /// <summary>
    /// A resumed flagged task whose test command PASSES must commit, and the run
    /// log must contain a verify_result (green) event before the commit event.
    /// </summary>
    [Fact]
    public async Task RunTaskAsync_Resume_CommitGateWithPassingTest_CommitsWithVerifyResult()
    {
        // Scenario: stages 1–11 Done, stage 12 Flagged.
        // The commit-gate re-validation runs the test suite isolated.
        // The test command PASSES and the tree hash matches.
        // Expect: outcome Committed, verify_result event with check="green" before
        // stage 12 stage_done.
        using var repo = TestRepository.Create();
        repo.WriteConfig("exit 0", []);
        repo.WriteTask("pass-resume", "# Pass resume\n");

        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "hello");

        var manifest = new[] { "src/app.cs" };
        var treeHash = RelayDriverResumeTestHelpers.ComputeTreeHash(repo.Root, manifest);
        RelayDriverResumeTestHelpers.SetupCommitGateResumeScenario(
            repo.Root, "pass-resume", manifest, treeHash);

        // Single passing test result — the commit gate re-validation consumes it.
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, new ThrowingSubagentRunner(),
                new ScriptedTestRunner(new TestRunResult(0, "All tests passed!")), sink),
            new RelayDriverOptions(CreateGitCommit: false, Resume: true));

        var outcome = await driver.RunTaskAsync(repo.Root, "pass-resume");

        // Must commit.
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // verify_result event must exist with check="green".
        var verifyResult = sink.Events.SingleOrDefault(e => e.EventName == "verify_result");
        Assert.NotNull(verifyResult);
        Assert.Equal("green", verifyResult!.Data?["check"]);
        Assert.Equal("0", verifyResult.Data?["exitCode"]);
        Assert.True(verifyResult.Data!.ContainsKey("treeHash"));
        Assert.True(verifyResult.Data.ContainsKey("outputFile"));

        // verify_result must appear BEFORE stage 12 stage_done (the commit event).
        var verifyIdx = sink.Events.FindIndex(e => e.EventName == "verify_result");
        var stage12DoneIdx = sink.Events.FindIndex(
            e => e is { EventName: "stage_done", StageNumber: 12 });
        Assert.True(verifyIdx >= 0);
        Assert.True(stage12DoneIdx >= 0);
        Assert.True(verifyIdx < stage12DoneIdx,
            "verify_result event must precede stage 12 stage_done in the run log");
    }
}
