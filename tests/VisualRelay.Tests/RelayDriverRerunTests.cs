using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverRerunTests
{
    [Fact]
    public async Task RunTaskAsync_AllocatesNextAttemptIndexOnEachReRun()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("re-run", "# Re-run\n");

        await RelayDriverTestHelpers.RunHappyPath(repo, "re-run");
        await RelayDriverTestHelpers.RunHappyPath(repo, "re-run");
        await RelayDriverTestHelpers.RunHappyPath(repo, "re-run");

        var taskDirectory = Path.Combine(repo.Root, ".relay", "re-run");
        Assert.True(File.Exists(Path.Combine(taskDirectory, "stage1-attempt1.report.json")));
        Assert.True(File.Exists(Path.Combine(taskDirectory, "stage1-attempt2.report.json")));
        Assert.True(File.Exists(Path.Combine(taskDirectory, "stage1-attempt3.report.json")));
        Assert.True(Directory.Exists(Path.Combine(taskDirectory, "stage1-attempt1")));
        Assert.True(Directory.Exists(Path.Combine(taskDirectory, "stage1-attempt2")));
        Assert.True(Directory.Exists(Path.Combine(taskDirectory, "stage1-attempt3")));
    }

    [Fact]
    public async Task RunTaskAsync_EmitsTurnsKeyWhenEstimateHasTurns()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("turns-task", "# Turns task\n");
        var runner = new TurnsReportingSubagentRunner(3);
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "green"));
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "turns-task");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var stage1Done = sink.Events.Single(e => e is { EventName: "stage_done", StageNumber: 1 });
        Assert.NotNull(stage1Done.Data);
        Assert.True(stage1Done.Data!.ContainsKey("turns"));
        Assert.Equal("3", stage1Done.Data["turns"]);
        var stage12Done = sink.Events.Single(e => e is { EventName: "stage_done", StageNumber: 12 });
        Assert.False(stage12Done.Data?.ContainsKey("turns"));
    }

    [Fact]
    public async Task RunTaskAsync_RetriesTaskThatWasAlreadyNeedsReview()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("retry-me", "# Retry\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay", "retry-me"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "retry-me", "NEEDS-REVIEW"), "old failure\n");
        var runner = new ScriptedSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);
        var outcome = await driver.RunTaskAsync(repo.Root, "retry-me");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "retry-me", "NEEDS-REVIEW")));
    }
}
