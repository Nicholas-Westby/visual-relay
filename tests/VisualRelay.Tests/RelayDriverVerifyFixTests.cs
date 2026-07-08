using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class RelayDriverVerifyFixTests
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
            RelayDriverDependencies.ForTests(runner, tests, sink),
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
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
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
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
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
            RelayDriverDependencies.ForTests(runner, tests, sink),
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

    // ── 10× turn-budget multiplier ──────────────────────────────────────

    [Fact]
    public async Task RunTaskAsync_Boosted_Applies10xMultiplierToEveryStage()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "logSources": [],
              "baselineVerify": false,
              "enableFixVerify": true,
              "boostTurnsTaskIds": ["big-one"]
            }
            """);
        repo.WriteTask("big-one", "# Big task\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),     // stage 5 author gate
            new TestRunResult(0, "green"));  // stage 10 verify — green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "big-one");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.NotEmpty(runner.Invocations);
        // Every regular stage invocation must carry the boosted turn count (200 * 10 = 2000).
        // Triage invocations have a separate capped MaxTurns (12); filter them out.
        foreach (var inv in runner.Invocations.Where(i => i.Stage is not null && i.Stage.Number > 0))
        {
            Assert.Equal(2000, inv.MaxTurns);
        }
    }

    [Fact]
    public async Task RunTaskAsync_NonBoosted_UsesDefaultMaxTurns()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "logSources": [],
              "baselineVerify": false,
              "enableFixVerify": true
            }
            """);
        repo.WriteTask("normal-task", "# Normal task\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),     // stage 5 author gate
            new TestRunResult(0, "green"));  // stage 10 verify — green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "normal-task");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.NotEmpty(runner.Invocations);
        // Every regular stage invocation must use the default 200.
        // Triage invocations have a separate capped MaxTurns (12); filter them out.
        foreach (var inv in runner.Invocations.Where(i => i.Stage is not null && i.Stage.Number > 0))
        {
            Assert.Equal(200, inv.MaxTurns);
        }
    }

    [Fact]
    public async Task RunVerifyFixLoop_EmitsVerifyResultEvent_AtStage10AndStage11_WithOutputFilePointer()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true);
        repo.WriteTask("verify-event", "# Verify event test\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),              // stage 5 author gate
            new TestRunResult(1, "Failed TestX"),      // stage 10 verify — first run fails
            new TestRunResult(1, "Failed TestX"),      // stage 10 verify — retry also fails
            new TestRunResult(0, "green"),             // fix-verify attempt 1 gate — green
            new TestRunResult(0, "green"));            // pad (not reached)
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        await driver.RunTaskAsync(repo.Root, "verify-event");

        // (1) The first authoritative red (stage 10) is observable.
        var stage10 = sink.Events.SingleOrDefault(e => e is { EventName: "verify_result", StageNumber: 10 });
        Assert.NotNull(stage10);
        Assert.Equal("dotnet test", stage10!.Data?["command"]);
        Assert.Equal("1", stage10.Data?["exitCode"]);
        Assert.Equal("red", stage10.Data?["check"]);
        Assert.Contains("Failed TestX", stage10.Data?["reason"] ?? "", StringComparison.Ordinal);
        // (2) The stage-11 gate verdict is observable.
        var stage11 = sink.Events.SingleOrDefault(e => e is { EventName: "verify_result", StageNumber: 11 });
        Assert.NotNull(stage11);
        Assert.Equal("dotnet test", stage11!.Data?["command"]);
        Assert.Equal("0", stage11.Data?["exitCode"]);
        Assert.Equal("green", stage11.Data?["check"]);
        Assert.True(stage11.Data!.ContainsKey("treeHash"));
        Assert.True(stage11.Data.ContainsKey("outputFile"));
        Assert.True(File.Exists(stage11.Data["outputFile"]));
        // (3) verify_result carries a treeHash and outputFile POINTER; full output in file, never inlined.
        Assert.True(stage10.Data!.ContainsKey("treeHash"));
        var outputFile = stage10.Data["outputFile"];
        Assert.False(string.IsNullOrEmpty(outputFile));
        Assert.True(File.Exists(outputFile));
        var persisted = await File.ReadAllTextAsync(outputFile);
        Assert.Contains("Failed TestX", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(stage10.Data.Values, v => v.Contains("Failed TestX") && v.Length > 200);
    }

    [Fact]
    public async Task RunTaskAsync_VerifyGreen_Stage10AgentReceivesCapturedTestOutput()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true);
        repo.WriteTask("stage9-green-output", "# Stage 9 receives green output\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),         // stage 5 author gate
            new TestRunResult(0, "All 42 tests passed!"));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);
        var outcome = await driver.RunTaskAsync(repo.Root, "stage9-green-output");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var inv = runner.Invocations.Single(i => i.Stage.Number == 10);
        Assert.NotNull(inv.LastTestOutput);
        Assert.Contains("All 42 tests passed!", inv.LastTestOutput, StringComparison.Ordinal);
        Assert.Null(inv.TestCommand); // read-only Verify gets captured output, not an imperative command
    }

    [Fact]
    public async Task RunTaskAsync_VerifyRed_Stage10AgentReceivesFailingTestOutput()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true);
        repo.WriteTask("stage9-fail-output", "# Stage 9 receives failing output\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(new TestRunResult(1, "red"),                    // stage 5 author gate
            new TestRunResult(1, "FAIL: TestDeepCheck"),
            new TestRunResult(1, "FAIL: TestDeepCheck"),
            new TestRunResult(1, "FAIL: TestDeepCheck"),
            new TestRunResult(0, "All green!"));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);
        var outcome = await driver.RunTaskAsync(repo.Root, "stage9-fail-output");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var inv = runner.Invocations.Single(i => i.Stage.Number == 10);
        Assert.NotNull(inv.LastTestOutput);
        Assert.Contains("FAIL: TestDeepCheck", inv.LastTestOutput, StringComparison.Ordinal);
        Assert.Null(inv.TestCommand);
    }
}
