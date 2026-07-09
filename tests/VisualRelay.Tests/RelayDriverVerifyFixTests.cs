using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverVerifyFixTests
{
    [Fact]
    public async Task RunTaskAsync_FixableVerifyFailure_CommitsAfterFixVerifyLoop()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true);
        repo.WriteTask("fixable-verify", "# Fixable verify\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),              // stage 5 author gate
            new TestRunResult(1, "Failed TestX"),      // stage 10 verify — first run fails
            new TestRunResult(1, "Failed TestX"),      // stage 10 verify — retry also fails
            new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 1 first run — red
            new TestRunResult(0, "green"));            // fix-verify attempt 1 retry — green
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "fixable-verify");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var seals = await File.ReadAllLinesAsync(Path.Combine(repo.Root, ".relay", "fixable-verify", "fixable-verify.seals"));
        Assert.Contains(seals, l => l.Contains("\"n\":10", StringComparison.Ordinal) && l.Contains("\"check\":\"red\"", StringComparison.Ordinal));
        Assert.Contains(seals, l => l.Contains("\"n\":11", StringComparison.Ordinal) && l.Contains("\"check\":\"green\"", StringComparison.Ordinal));
        Assert.Contains(sink.Events, e => e is { EventName: "stage_start", StageNumber: 11 });
        Assert.Contains(sink.Events, e => e is { EventName: "stage_done", StageNumber: 11 });
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "fixable-verify", "NEEDS-REVIEW")));
    }

    [Fact]
    public async Task RunVerifyFixLoop_RecoversOnSecondRun_StopsWithinMaxStageFailuresCap()
    {
        using var repo = TestRepository.Create();
        // The fix-verify run COUNT is MaxStageFailures (set explicitly to 3): the loop
        // recovers on run 2 and must never spend run 3 — the "attempt N/3" labels track
        // MaxStageFailures, not the (now removed) MaxVerifyLoops count.
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true, maxStageFailures: 3);
        repo.WriteTask("retry-twice", "# Retry twice\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),              // stage 5 author gate
            new TestRunResult(1, "Failed TestX"),      // stage 10 verify — first run fails
            new TestRunResult(1, "Failed TestX"),      // stage 10 verify — retry also fails
            new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 1 first run — red
            new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 1 retry — red
            new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 2 first run — red
            new TestRunResult(0, "green"));            // fix-verify attempt 2 retry — green
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "retry-twice");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var ledger = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "retry-twice", "ledger.md"));
        Assert.Contains("attempt 1/3", ledger, StringComparison.Ordinal);
        Assert.Contains("attempt 2/3", ledger, StringComparison.Ordinal);
        Assert.DoesNotContain("attempt 3/3", ledger, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTaskAsync_FixVerifyLoop_AgentReceivesFailingOutput()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true);
        repo.WriteTask("fail-visible", "# Fail visible in full command\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),                    // stage 5
            new TestRunResult(1, "Failed DeepCheck"),        // stage 10
            new TestRunResult(1, "Failed DeepCheck"),        // stage 10 retry
            new TestRunResult(1, "Failed DeepCheck"),        // fix-verify gate
            new TestRunResult(0, "green"));                  // fix-verify retry
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);
        var outcome = await driver.RunTaskAsync(repo.Root, "fail-visible");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var inv11 = runner.Invocations.Single(i => i.Stage.Number == 11);
        Assert.NotNull(inv11.LastTestOutput);
        Assert.Contains("Failed DeepCheck", inv11.LastTestOutput, StringComparison.Ordinal);
        // Regression guard: only stages 10 and 11 carry test output.
        foreach (var inv in runner.Invocations.Where(i => i.Stage.Number is not (10 or 11)))
            Assert.Null(inv.LastTestOutput);
    }

    [Fact]
    public async Task RunTaskAsync_VerifyGreen_SkipsFixVerifyLlmCall_ButRecordsStage10Green()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true);
        repo.WriteTask("green-skip", "# Verify green, skip fix-verify\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),     // stage 5 author gate (must be red)
            new TestRunResult(0, "green"));  // stage 10 verify — green on first try
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "green-skip");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.DoesNotContain(runner.Invocations, i => i.Stage.Number == 11);
        var entries = StageStatusRecord.Read(Path.Combine(repo.Root, ".relay", "green-skip"));
        Assert.Equal(12, entries.Count);
        Assert.All(entries, e => Assert.True(e.Status is "Done" or "Skipped"));
        var fixVerify = entries.Single(e => e.Stage == 11);
        Assert.Equal("green", fixVerify.Check);
        Assert.Null(fixVerify.CostUsd);
        Assert.Null(fixVerify.Turns);
        var seals = await File.ReadAllLinesAsync(
            Path.Combine(repo.Root, ".relay", "green-skip", "green-skip.seals"));
        Assert.Contains(seals, l =>
            l.Contains("\"n\":11", StringComparison.Ordinal) &&
            l.Contains("\"check\":\"green\"", StringComparison.Ordinal));
        Assert.Contains(sink.Events, e => e is { EventName: "stage_done", StageNumber: 11 });
    }
}
