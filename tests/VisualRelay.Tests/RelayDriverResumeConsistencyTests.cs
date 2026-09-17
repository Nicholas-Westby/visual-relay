using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// One i18next drain exercised "commit rejected by the project's hook, then resume"
/// and left oddities behind. None lost work; each was a small fault in the resume
/// path. These pin what the run now says about itself and what it leaves staged.
/// </summary>
public sealed class RelayDriverResumeConsistencyTests
{
    /// <summary>
    /// A green check has nothing to explain. The reason used to be picked from the
    /// exit code alone, so the commit-gate resume — which publishes without setup
    /// checks — reported <c>check=green reason=setup check failure</c> on every
    /// successful resume.
    /// </summary>
    [Fact]
    public async Task VerifyResult_GreenWithNoSetupChecks_PublishesAnEmptyReason()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false);
        repo.WriteTask("green-run", "# Green run\n");
        var sink = new InMemoryRelayEventSink();
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);

        await driver.RunTaskAsync(repo.Root, "green-run");

        var greens = sink.Events
            .Where(e => e.EventName == "verify_result" && e.Data?.GetValueOrDefault("check") == "green")
            .ToList();
        Assert.NotEmpty(greens);
        Assert.All(greens, e => Assert.Equal(string.Empty, e.Data!.GetValueOrDefault("reason")));
        Assert.DoesNotContain(greens, e => e.Data!.GetValueOrDefault("reason") == "setup check failure");
    }

    /// <summary>
    /// A resumed run's log did not say it was one, so a run that silently restarted at
    /// stage 5 read as a fresh run that had skipped four stages.
    /// </summary>
    [Fact]
    public async Task RunStart_OnAFreshRun_SaysItIsNotAResumeAndNamesTheFirstStage()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false);
        repo.WriteTask("fresh", "# Fresh\n");
        var sink = new InMemoryRelayEventSink();
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);

        await driver.RunTaskAsync(repo.Root, "fresh");

        var start = Assert.Single(sink.Events, e => e.EventName == "run_start");
        Assert.Equal("false", start.Data!["resume"]);
        Assert.Equal("1", start.Data["firstStage"]);
    }
}
