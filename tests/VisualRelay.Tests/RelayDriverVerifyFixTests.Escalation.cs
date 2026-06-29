using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The fix-verify loop reconciled into the 3-run escalation model: each iteration
/// is an escalation RUN that bumps tier (balanced→frontier, capped) and doubles the
/// turn budget (flat under the 10× boost), capped at MaxStageFailures runs, with a
/// labeled Run-Log entry per transition. Verified through a CapturingSubagentRunner
/// that records each stage-10 invocation's tier + turn budget.
/// </summary>
public sealed class RelayDriverVerifyFixEscalationTests
{
    // stage 5 red, stage 9 red (+ flaky retry), then N red fix-verify runs (gate +
    // flaky retry each) so the loop spends every run and flags.
    private static ScriptedTestRunner AllRed(int fixVerifyRuns)
    {
        var results = new List<TestRunResult> { new(1, "red"), new(1, "Failed TestX"), new(1, "Failed TestX") };
        for (var i = 0; i < fixVerifyRuns; i++)
        {
            results.Add(new TestRunResult(1, "Failed TestX")); // gate
            results.Add(new TestRunResult(1, "Failed TestX")); // flaky retry
        }
        return new ScriptedTestRunner(results.ToArray());
    }

    [Fact]
    public async Task RunVerifyFixLoop_EscalatesTierAndDoublesTurns_PerRun()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 1, maxStageFailures: 3);
        repo.WriteTask("fv-ladder", "# Fix-verify ladder\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, AllRed(3), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "fv-ladder");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        var stage10 = runner.Invocations.Where(i => i.Stage.Number == 10).ToList();
        Assert.Equal(3, stage10.Count);
        // Stage 10 default tier is balanced → balanced/frontier/frontier (capped).
        Assert.Equal(["balanced", "frontier", "frontier"], stage10.Select(i => i.Tier));
        // Turns double per run: 200/400/800.
        Assert.Equal([200, 400, 800], stage10.Select(i => i.MaxTurns));
    }

    [Fact]
    public async Task RunVerifyFixLoop_EmitsLabeledEscalationRunLogEntries()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 1, maxStageFailures: 3);
        repo.WriteTask("fv-log", "# Fix-verify log\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, AllRed(3), sink),
            RelayDriverOptions.NoGitCommit);

        await driver.RunTaskAsync(repo.Root, "fv-log");

        var escalations = sink.Events
            .Where(e => e is { EventName: "stage_escalated", StageNumber: 10 })
            .Select(e => e.Data!["message"])
            .ToList();
        Assert.Equal(2, escalations.Count); // one per transition into runs 2 and 3
        Assert.Equal(
            "Stage 10 Fix-verify escalated (run 2/3): tier balanced→frontier, max-turns 200→400",
            escalations[0]);
        Assert.Equal(
            "Stage 10 Fix-verify escalated (run 3/3): tier frontier→frontier, max-turns 400→800",
            escalations[1]);
    }

    [Fact]
    public async Task RunVerifyFixLoop_TenXBoost_HoldsTurnsFlat_ButTierStillEscalates()
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
              "maxStageFailures": 3,
              "boostTurnsTaskIds": ["fv-boost"]
            }
            """);
        repo.WriteTask("fv-boost", "# Fix-verify boost\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, AllRed(3), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        await driver.RunTaskAsync(repo.Root, "fv-boost");

        var stage10 = runner.Invocations.Where(i => i.Stage.Number == 10).ToList();
        Assert.Equal(3, stage10.Count);
        // Tier still escalates under the boost.
        Assert.Equal(["balanced", "frontier", "frontier"], stage10.Select(i => i.Tier));
        // Turns stay flat at 10× base (200 × 10 = 2000) — no per-run doubling.
        Assert.Equal([2000, 2000, 2000], stage10.Select(i => i.MaxTurns));
    }
}
