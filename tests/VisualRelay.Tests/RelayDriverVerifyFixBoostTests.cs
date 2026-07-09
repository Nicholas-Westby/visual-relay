using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverVerifyFixBoostTests
{
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
            RelayDriverTestHelpers.DepsFor(repo, runner, tests, new InMemoryRelayEventSink()),
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
            RelayDriverTestHelpers.DepsFor(repo, runner, tests, new InMemoryRelayEventSink()),
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
}
