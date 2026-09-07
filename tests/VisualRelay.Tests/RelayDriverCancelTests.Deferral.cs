using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// The two places a cancel must defer rather than tear: creating the isolated verify
/// worktree, and the gap between two stages where nothing is running to blame.
/// </summary>
public sealed partial class RelayDriverCancelTests
{
    [Fact]
    public async Task RunTaskAsync_CancelledAsTheVerifyWorktreeIsCreated_NeverRunsTheSuiteOnTheMainTree()
    {
        // Creating the verify snapshot must not be torn by a cancel: an aborted
        // `git worktree add` used to fall back to running the project's test command
        // against the real repository, which is the opposite of stopping.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("cancel-at-add", "# Cancel at add\n");
        using var cts = new CancellationTokenSource();
        var git = new CancelOnWorktreeAddGitInvoker(RelayDriverTestHelpers.InitTestRepo(repo), cts);
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var testRunner = new RecordingTestRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, testRunner, new InMemoryRelayEventSink(), git),
            RelayDriverOptions.NoGitCommit);

        await driver.RunTaskAsync(repo.Root, "cancel-at-add", cts.Token);

        Assert.Contains(git.Calls, c => c is ["worktree", "add", ..]);
        Assert.DoesNotContain(testRunner.Calls,
            c => c.RootPath == repo.Root && c.TokenWasCancelled);
    }

    [Fact]
    public async Task RunTaskAsync_CancelledBetweenStages_FlagsTheNextStageAndLeavesFinishedOnesDone()
    {
        // A cancel that lands between stages has no Running stage to blame. It must
        // claim the stage that had not started, not rewrite a finished one — a resume
        // reads the first unfinished entry, so blaming stage 1 would redo everything.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("between-stages", "# Between stages\n");
        using var cts = new CancellationTokenSource();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, new ScriptedSubagentRunner(),
                new ScriptedTestRunner(), new CancelOnStageDoneSink(cts, stage: 2)),
            RelayDriverOptions.NoGitCommit);

        await driver.RunTaskAsync(repo.Root, "between-stages", cts.Token);

        var taskDirectory = Path.Combine(repo.Root, ".relay", "between-stages");
        var status = StageStatusRecord.Read(taskDirectory);
        Assert.Equal("Done", status[0].Status);
        Assert.Equal("Done", status[1].Status);
        Assert.Equal("Flagged", status[2].Status);
        Assert.Equal("cancelled by operator", status[2].Error);
        var marker = await File.ReadAllTextAsync(Path.Combine(taskDirectory, "NEEDS-REVIEW"), CancellationToken.None);
        Assert.Contains("stage 3", marker, StringComparison.Ordinal);

        // The resume that follows must pick up at the cancelled stage, not stage 1.
        var resumeSink = new InMemoryRelayEventSink();
        var resumed = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, new ScriptedSubagentRunner(),
                new ScriptedTestRunner(), resumeSink),
            new RelayDriverOptions(CreateGitCommit: false, Resume: true));
        await resumed.RunTaskAsync(repo.Root, "between-stages", CancellationToken.None);

        var firstStage = resumeSink.Events.First(e => e.EventName == "stage_start").StageNumber;
        Assert.Equal(3, firstStage);
    }

    /// <summary>Cancels the run the moment git is asked to add a worktree.</summary>
    private sealed class CancelOnWorktreeAddGitInvoker(GitSimEngine inner, CancellationTokenSource cts) : IGitInvoker
    {
        public List<string[]> Calls { get; } = [];

        public Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
            string rootPath, IEnumerable<string> arguments, CancellationToken cancellationToken,
            TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken killToken = default, Action<string>? onActivity = null)
        {
            var args = arguments.ToArray();
            Calls.Add(args);
            if (args is ["worktree", "add", ..])
                cts.Cancel();
            // Real git dies on a cancelled token; the sim would happily carry on.
            cancellationToken.ThrowIfCancellationRequested();
            return inner.RunAsync(rootPath, args, cancellationToken, timeout, environment, killToken, onActivity);
        }
    }

    /// <summary>Records where each test command ran and whether its token was already cancelled.</summary>
    private sealed class RecordingTestRunner : ITestRunner
    {
        public List<(string RootPath, bool TokenWasCancelled)> Calls { get; } = [];

        public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
        {
            Calls.Add((rootPath, cancellationToken.IsCancellationRequested));
            return Task.FromResult(new TestRunResult(0, "green"));
        }
    }

    /// <summary>Cancels the run once a given stage has finished, so none is left running.</summary>
    private sealed class CancelOnStageDoneSink(CancellationTokenSource cts, int stage) : IRelayEventSink
    {
        public Task PublishAsync(RelayEvent relayEvent, CancellationToken cancellationToken = default)
        {
            if (relayEvent is { EventName: "stage_done" } && relayEvent.StageNumber == stage)
                cts.Cancel();
            return Task.CompletedTask;
        }
    }
}
