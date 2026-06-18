using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverVerifyFixTests
{
    [Fact]
    public async Task RunTaskAsync_FixableVerifyFailure_CommitsAfterFixVerifyLoop()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 2);
        repo.WriteTask("fixable-verify", "# Fixable verify\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),              // stage 5 author gate
            new TestRunResult(1, "Failed TestX"),      // stage 9 verify — first run fails
            new TestRunResult(1, "Failed TestX"),      // stage 9 verify — retry also fails
            new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 1 first run — red
            new TestRunResult(0, "green"));            // fix-verify attempt 1 retry — green
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "fixable-verify");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        // Stage 9 seal should show red (honest record)
        var seals = await File.ReadAllLinesAsync(Path.Combine(repo.Root, ".relay", "fixable-verify", "fixable-verify.seals"));
        Assert.Contains(seals, line => line.Contains("\"n\":9", StringComparison.Ordinal) && line.Contains("\"check\":\"red\"", StringComparison.Ordinal));
        // Stage 10 seal should show green (fix succeeded)
        Assert.Contains(seals, line => line.Contains("\"n\":10", StringComparison.Ordinal) && line.Contains("\"check\":\"green\"", StringComparison.Ordinal));
        // Verify the fix-verify stage ran (stage_start/done events for stage 10)
        Assert.Contains(sink.Events, e => e is { EventName: "stage_start", StageNumber: 10 });
        Assert.Contains(sink.Events, e => e is { EventName: "stage_done", StageNumber: 10 });
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "fixable-verify", "NEEDS-REVIEW")));
    }

    [Fact]
    public async Task RunTaskAsync_UnfixableVerifyFailure_FlagsAfterMaxLoops()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 2);
        repo.WriteTask("unfixable", "# Unfixable\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),              // stage 5 author gate
            new TestRunResult(1, "Failed TestX"),      // stage 9 verify — first run fails
            new TestRunResult(1, "Failed TestX"),      // stage 9 verify — retry also fails
            new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 1 first run — red
            new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 1 retry — still red
            new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 2 first run — red
            new TestRunResult(1, "Failed TestX"));     // fix-verify attempt 2 retry — still red
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "unfixable");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.Contains("verify failed after 2 fix-verify attempts", outcome.Reason, StringComparison.Ordinal);
        var review = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "unfixable", "NEEDS-REVIEW"));
        Assert.Contains("verify failed after 2 fix-verify attempts", review, StringComparison.Ordinal);
        // Seals should record both failed stage 10 attempts
        var seals = await File.ReadAllLinesAsync(Path.Combine(repo.Root, ".relay", "unfixable", "unfixable.seals"));
        Assert.Contains(seals, line => line.Contains("\"n\":9", StringComparison.Ordinal) && line.Contains("\"check\":\"red\"", StringComparison.Ordinal));
        Assert.Contains(seals, line => line.Contains("\"n\":10", StringComparison.Ordinal) && line.Contains("\"check\":\"red\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunTaskAsync_MaxVerifyLoopsRespected_ExactAttemptCount()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 3);
        repo.WriteTask("retry-twice", "# Retry twice\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),              // stage 5 author gate
            new TestRunResult(1, "Failed TestX"),      // stage 9 verify — first run fails
            new TestRunResult(1, "Failed TestX"),      // stage 9 verify — retry also fails
            new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 1 first run — red
            new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 1 retry — red
            new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 2 first run — red
            new TestRunResult(0, "green"));            // fix-verify attempt 2 retry — green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "retry-twice");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        // Ledger should contain exactly 2 fix-verify sections
        var ledger = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "retry-twice", "ledger.md"));
        Assert.Contains("attempt 1/3", ledger, StringComparison.Ordinal);
        Assert.Contains("attempt 2/3", ledger, StringComparison.Ordinal);
        Assert.DoesNotContain("attempt 3/3", ledger, StringComparison.Ordinal); // never reached
    }

    [Fact]
    public async Task RunTaskAsync_FixVerifyLoop_AgentReceivesFailingOutput()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 2);
        repo.WriteTask("fail-visible", "# Fail visible in full command\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),                    // stage 5 author gate
            new TestRunResult(1, "Failed DeepCheck"),        // stage 9 verify — first run fails
            new TestRunResult(1, "Failed DeepCheck"),        // stage 9 verify — retry also fails
            new TestRunResult(1, "Failed DeepCheck"),        // fix-verify attempt 1 first run — red
            new TestRunResult(0, "green"));                  // fix-verify attempt 1 retry — green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "fail-visible");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        // The stage-10 invocation must contain the captured failure from stage 9.
        var stage10Invocation = runner.Invocations.SingleOrDefault(i => i.Stage.Number == 10);
        Assert.NotNull(stage10Invocation);
        Assert.NotNull(stage10Invocation!.LastTestOutput);
        Assert.Contains("Failed DeepCheck", stage10Invocation.LastTestOutput, StringComparison.Ordinal);
        // Verify no other stage received LastTestOutput (regression guard).
        foreach (var inv in runner.Invocations.Where(i => i.Stage.Number != 10))
        {
            Assert.Null(inv.LastTestOutput);
        }
    }

    [Fact]
    public async Task RunTaskAsync_VerifyGreen_SkipsFixVerifyLlmCall_ButRecordsStage10Green()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 1);
        repo.WriteTask("green-skip", "# Verify green, skip fix-verify\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),     // stage 5 author gate (must be red)
            new TestRunResult(0, "green"));  // stage 9 verify — green on first try
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "green-skip");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // (1) NO stage-10 LLM subagent invocation happened.
        Assert.DoesNotContain(runner.Invocations, i => i.Stage.Number == 10);

        // (2) status.json still has all 11 stages Done, and stage 10 is green.
        var entries = StageStatusRecord.Read(Path.Combine(repo.Root, ".relay", "green-skip"));
        Assert.Equal(11, entries.Count);
        Assert.All(entries, e => Assert.Equal("Done", e.Status));
        var stage10 = entries.Single(e => e.Stage == 10);
        Assert.Equal("green", stage10.Check);
        Assert.Null(stage10.CostUsd);   // no LLM ran
        Assert.Null(stage10.Turns);

        // (3) The seal chain reached 10 stage seals; stage 10 seal is green.
        var seals = await File.ReadAllLinesAsync(
            Path.Combine(repo.Root, ".relay", "green-skip", "green-skip.seals"));
        Assert.Contains(seals, l =>
            l.Contains("\"n\":10", StringComparison.Ordinal) &&
            l.Contains("\"check\":\"green\"", StringComparison.Ordinal));

        // (4) stage_done event for stage 10 was still published (UI parity).
        Assert.Contains(sink.Events, e => e is { EventName: "stage_done", StageNumber: 10 });
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
              "maxVerifyLoops": 1,
              "boostTurnsTaskIds": ["big-one"]
            }
            """);
        repo.WriteTask("big-one", "# Big task\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),     // stage 5 author gate
            new TestRunResult(0, "green"));  // stage 9 verify — green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "big-one");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.NotEmpty(runner.Invocations);
        // Every stage invocation must carry the boosted turn count (200 * 10 = 2000).
        foreach (var inv in runner.Invocations)
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
              "maxVerifyLoops": 1
            }
            """);
        repo.WriteTask("normal-task", "# Normal task\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),     // stage 5 author gate
            new TestRunResult(0, "green"));  // stage 9 verify — green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "normal-task");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.NotEmpty(runner.Invocations);
        // Every stage must use the default 200.
        foreach (var inv in runner.Invocations)
        {
            Assert.Equal(200, inv.MaxTurns);
        }
    }

    [Fact]
    public async Task RunVerifyFixLoop_EmitsVerifyResultEvent_AtStage9AndStage10_WithOutputFilePointer()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 2);
        repo.WriteTask("verify-event", "# Verify event test\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),              // stage 5 author gate
            new TestRunResult(1, "Failed TestX"),      // stage 9 verify — first run fails
            new TestRunResult(1, "Failed TestX"),      // stage 9 verify — retry also fails
            new TestRunResult(0, "green"),             // fix-verify attempt 1 gate — green
            new TestRunResult(0, "green"));            // pad (not reached)
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        await driver.RunTaskAsync(repo.Root, "verify-event");

        // (1) The first authoritative red (stage 9) is observable.
        var stage9 = sink.Events.SingleOrDefault(e => e is { EventName: "verify_result", StageNumber: 9 });
        Assert.NotNull(stage9);
        Assert.Equal("dotnet test", stage9!.Data?["command"]);
        Assert.Equal("1", stage9.Data?["exitCode"]);
        Assert.Equal("red", stage9.Data?["check"]);
        Assert.Contains("Failed TestX", stage9.Data?["reason"] ?? "", StringComparison.Ordinal);
        // (2) The stage-10 gate verdict is observable.
        var stage10 = sink.Events.SingleOrDefault(e => e is { EventName: "verify_result", StageNumber: 10 });
        Assert.NotNull(stage10);
        Assert.Equal("dotnet test", stage10!.Data?["command"]);
        Assert.Equal("0", stage10.Data?["exitCode"]);
        Assert.Equal("green", stage10.Data?["check"]);
        Assert.True(stage10.Data!.ContainsKey("treeHash"));
        Assert.True(stage10.Data.ContainsKey("outputFile"));
        Assert.True(File.Exists(stage10.Data["outputFile"]));
        // (3) verify_result carries a treeHash and outputFile POINTER; full output in file, never inlined.
        Assert.True(stage9.Data!.ContainsKey("treeHash"));
        var outputFile = stage9.Data["outputFile"];
        Assert.False(string.IsNullOrEmpty(outputFile));
        Assert.True(File.Exists(outputFile));
        var persisted = await File.ReadAllTextAsync(outputFile);
        Assert.Contains("Failed TestX", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(stage9.Data.Values, v => v.Contains("Failed TestX") && v.Length > 200);
    }
}
