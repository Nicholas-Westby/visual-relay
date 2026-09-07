using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// A cancelled run must wind down, not tear: the driver records what the run had
/// reached (status, NEEDS-REVIEW, a <c>cancelled</c> event), does that recording on a
/// FRESH token — the run's own is already cancelled — and defers a cancel that lands
/// inside the sealing commit stage until the commit has finished.
/// </summary>
public sealed class RelayDriverCancelTests
{
    [Fact]
    public async Task RunTaskAsync_CancelledMidStage_WritesMarkerStatusAndCancelledEvent()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("cancel-me", "# Cancel me\n");
        using var cts = new CancellationTokenSource();
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, new CancellingSubagentRunner(cts, atStage: 2),
                new ScriptedTestRunner(), sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "cancel-me", cts.Token);

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.Equal("cancelled by operator", outcome.Reason);

        var taskDirectory = Path.Combine(repo.Root, ".relay", "cancel-me");
        var marker = await File.ReadAllTextAsync(Path.Combine(taskDirectory, "NEEDS-REVIEW"));
        Assert.Contains("cancelled by operator", marker, StringComparison.Ordinal);
        Assert.Contains("stage 2", marker, StringComparison.Ordinal);

        var status = StageStatusRecord.Read(taskDirectory);
        Assert.Equal("Flagged", status[1].Status);
        Assert.Equal("cancelled by operator", status[1].Error);

        var cancelled = Assert.Single(sink.Events, e => e.EventName == "cancelled");
        Assert.Equal("warn", cancelled.Level);
        Assert.Equal(2, cancelled.StageNumber);

        // The lock is a run's claim on the repo; a cancelled run must let go of it.
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".relay", "ACTIVE")));
    }

    [Fact]
    public async Task RunTaskAsync_StageReportsTheCancelInsteadOfThrowing_WindsDownAtThatStage()
    {
        // The turn loop absorbs a cancel and reports it as an invalid stage result. That
        // must not be recorded as an ordinary flag: the marker, the status entry and the
        // event all name the cancel, at the stage that was actually running.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("report-cancel", "# Report cancel\n");
        using var cts = new CancellationTokenSource();
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, new CancelReportingSubagentRunner(cts, atStage: 3),
                new ScriptedTestRunner(), sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "report-cancel", cts.Token);

        Assert.Equal("cancelled by operator", outcome.Reason);
        var taskDirectory = Path.Combine(repo.Root, ".relay", "report-cancel");
        var marker = await File.ReadAllTextAsync(Path.Combine(taskDirectory, "NEEDS-REVIEW"));
        Assert.Contains("stage 3", marker, StringComparison.Ordinal);

        var status = StageStatusRecord.Read(taskDirectory);
        Assert.Equal("Flagged", status[2].Status);
        Assert.Equal("cancelled by operator", status[2].Error);
        Assert.All(status, e => Assert.NotEqual("stage cancelled", e.Error));

        var cancelled = Assert.Single(sink.Events, e => e.EventName == "cancelled");
        Assert.Equal(3, cancelled.StageNumber);
        Assert.DoesNotContain(sink.Events, e => e.EventName == "flagged");
    }

    [Fact]
    public async Task RunTaskAsync_CancelledDuringTheCommitStage_StillCommits()
    {
        // Stage 12 retires the task and commits. A cancel that lands inside it must
        // take effect after it, leaving a committed task rather than a half-sealed one.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("seal-me", "# Seal me\n");
        var sim = RelayDriverTestHelpers.InitTestRepo(repo);
        using var cts = new CancellationTokenSource();
        var scripted = new ScriptedSubagentRunner();
        scripted.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var runner = new FileWritingSubagentRunner(scripted, 6, "src/app.cs", "implemented");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new CancelOnStageStartSink(cts, stage: 12), sim),
            new RelayDriverOptions(CreateGitCommit: true));

        var outcome = await driver.RunTaskAsync(repo.Root, "seal-me", cts.Token);

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "seal-me", "NEEDS-REVIEW")));
    }

    [Fact]
    public async Task RunTaskAsync_CancelledDuringVerify_RemovesTheVerifyWorktreeOnAFreshToken()
    {
        // The verify worktree is torn down in a finally that runs with the cancelled
        // token; passing that token on would skip `git worktree remove` and leave the
        // worktree registered.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("cancel-verify", "# Cancel verify\n");
        var git = new TokenRecordingGitInvoker(RelayDriverTestHelpers.InitTestRepo(repo));
        using var cts = new CancellationTokenSource();
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner,
                new CancelInsideWorktreeTestRunner(cts, repo.Root),
                new InMemoryRelayEventSink(), git),
            RelayDriverOptions.NoGitCommit);

        await driver.RunTaskAsync(repo.Root, "cancel-verify", cts.Token);

        var removals = git.Calls.Where(c => c.Arguments is ["worktree", "remove", ..]).ToList();
        Assert.NotEmpty(removals);
        Assert.All(removals, c => Assert.False(c.TokenWasCancelled));
    }

    /// <summary>Cancels the run from inside a stage, exactly as an operator's cancel does.</summary>
    private sealed class CancellingSubagentRunner(CancellationTokenSource cts, int atStage) : ISubagentRunner
    {
        private readonly ScriptedSubagentRunner _inner = new();

        public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
        {
            if (invocation.Stage.Number < atStage)
                return _inner.RunAsync(invocation, cancellationToken);
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }
    }

    /// <summary>Cancels the run and reports it as an invalid result, as the turn loop does.</summary>
    private sealed class CancelReportingSubagentRunner(CancellationTokenSource cts, int atStage) : ISubagentRunner
    {
        private readonly ScriptedSubagentRunner _inner = new();

        public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
        {
            if (invocation.Stage.Number < atStage)
                return _inner.RunAsync(invocation, cancellationToken);
            cts.Cancel();
            return Task.FromResult(new SubagentResult(string.Empty, null, IsValid: false, Error: "stage cancelled"));
        }
    }

    /// <summary>Cancels the run the moment a given stage announces itself.</summary>
    private sealed class CancelOnStageStartSink(CancellationTokenSource cts, int stage) : IRelayEventSink
    {
        public Task PublishAsync(RelayEvent relayEvent, CancellationToken cancellationToken = default)
        {
            if (relayEvent is { EventName: "stage_start" } && relayEvent.StageNumber == stage)
                cts.Cancel();
            return Task.CompletedTask;
        }
    }

    /// <summary>Cancels the run while the suite is running in the isolated verify worktree.</summary>
    private sealed class CancelInsideWorktreeTestRunner(CancellationTokenSource cts, string mainRoot) : ITestRunner
    {
        public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(rootPath, mainRoot, StringComparison.Ordinal))
                cts.Cancel();
            return Task.FromResult(new TestRunResult(0, "green"));
        }
    }

    /// <summary>Records the arguments and token state of every git call it forwards.</summary>
    private sealed class TokenRecordingGitInvoker(GitSimEngine inner) : IGitInvoker
    {
        public List<(string[] Arguments, bool TokenWasCancelled)> Calls { get; } = [];

        public Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
            string rootPath, IEnumerable<string> arguments, CancellationToken cancellationToken,
            TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken killToken = default, Action<string>? onActivity = null)
        {
            var args = arguments.ToArray();
            Calls.Add((args, cancellationToken.IsCancellationRequested));
            return inner.RunAsync(rootPath, args, cancellationToken, timeout, environment, killToken, onActivity);
        }
    }
}
