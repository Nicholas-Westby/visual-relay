using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A resume re-enters the first stage the last run did not finish, but two stages cannot be
/// entered on their own. Fix-verify is a loop the Verify gate starts with the failing output, and
/// Visual-review runs as the second half of the review pair. The redirect that sent a stage-8
/// resume back to Review returned before the flagged work was restored, and nothing sent a
/// stage-11 resume back to Verify, so the resumed Fix-verify ran without a test run and the task
/// was committed unverified. Found reading the resume path before a LiteDB task cancelled in
/// Fix-verify on the Windows arm was resumed.
/// </summary>
public sealed partial class RelayDriverCancelTests
{
    [Fact]
    public async Task RunTaskAsync_ResumedAfterACancelInFixVerify_VerifiesAgainInsteadOfCommittingUnverified()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true, maxStageFailures: 1);
        repo.WriteTask("fix-verify-resume", "# Resume in Fix-verify\n");
        var sim = RelayDriverTestHelpers.InitTestRepo(repo);
        using var cts = new CancellationTokenSource();
        var firstRun = new RelayDriver(
            RelayDriverDependencies.ForTests(
                new FileWritingSubagentRunner(new CancellingSubagentRunner(cts, atStage: 11), 6, "src/app.cs", "half-finished\n"),
                new ScriptedTestRunner(
                    new TestRunResult(1, "red"),              // stage 5 author gate
                    new TestRunResult(1, "Failed AppTest"),   // stage 10 verify
                    new TestRunResult(1, "Failed AppTest")),  // stage 10 retry
                new InMemoryRelayEventSink(), sim),
            new RelayDriverOptions(CreateGitCommit: true));
        Assert.Equal("cancelled by operator", (await firstRun.RunTaskAsync(repo.Root, "fix-verify-resume", cts.Token)).Reason);

        var tests = new global::VisualRelay.Tests.RecordingTestRunner(new TestRunResult(1, "Failed AppTest"), new TestRunResult(1, "Failed AppTest"),
            new TestRunResult(1, "Failed AppTest"), new TestRunResult(1, "Failed AppTest"));
        var resumed = await new RelayDriver(
            RelayDriverDependencies.ForTests(new ScriptedSubagentRunner(), tests, new InMemoryRelayEventSink(), sim),
            new RelayDriverOptions(CreateGitCommit: true, Resume: true)).RunTaskAsync(repo.Root, "fix-verify-resume", TestContext.Current.CancellationToken);

        Assert.NotEmpty(tests.Calls);
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, resumed.Status);
    }

    [Fact]
    public async Task RunTaskAsync_ResumedAfterVisualReviewFlagged_ReviewsTheRestoredWork()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("review-resume", "# Resume in Visual-review\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, ".gitignore", ".relay/\n");
        sim.Seed(repo.Root, "src/app.cs", "committed\n");
        sim.Commit(repo.Root, "chore: seed repo");
        var firstRun = new RelayDriver(
            RelayDriverDependencies.ForTests(
                new FileWritingSubagentRunner(new FlagAtStageSubagentRunner(flagAtStage: 8), 6, "src/app.cs", "half-finished\n"),
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(), sim),
            new RelayDriverOptions(CreateGitCommit: true));
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, (await firstRun.RunTaskAsync(repo.Root, "review-resume")).Status);
        // The drain resets the checkout after a flagged task so the next task starts clean.
        await WorktreeResetter.ResetAsync(repo.Root, "review-resume", null, sim, TestContext.Current.CancellationToken);
        var appPath = Path.Combine(repo.Root, "src", "app.cs");
        Assert.Equal("committed\n", await File.ReadAllTextAsync(appPath, TestContext.Current.CancellationToken));

        var reviewer = new TreeReadingSubagentRunner(appPath, stage: 7);
        await new RelayDriver(
            RelayDriverDependencies.ForTests(reviewer, new ScriptedTestRunner(new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(), sim),
            new RelayDriverOptions(CreateGitCommit: true, Resume: true)).RunTaskAsync(repo.Root, "review-resume", TestContext.Current.CancellationToken);

        Assert.Equal("half-finished\n", reviewer.ContentSeen);
    }

    /// <summary>The scripted happy path, noting what a file held when the given stage started.</summary>
    private sealed class TreeReadingSubagentRunner(string path, int stage) : ISubagentRunner
    {
        private readonly ScriptedSubagentRunner _inner = new();

        public string? ContentSeen { get; private set; }

        public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
        {
            if (invocation.Stage.Number == stage)
                ContentSeen = File.Exists(path) ? File.ReadAllText(path) : null;
            return _inner.RunAsync(invocation, cancellationToken);
        }
    }
}
