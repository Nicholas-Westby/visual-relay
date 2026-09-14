using VisualRelay.Core.Execution;
using VisualRelay.Domain;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// The guard's baseline and the stage-5 red gate stash the checkout, run a command, and put the
/// stash back in a <c>finally</c>. A cancel reaches that <c>finally</c> with the run's token already
/// cancelled, and a git call handed a cancelled token is killed before it does anything. Found with
/// max-sixty/worktrunk on the Mac, where the baseline verify still stashed too: a cancel during it
/// left the task's whole change in a relay-redgate stash, the flagged-work capture saw a clean
/// tree, and the resumed run had nothing to commit. The baseline verify now runs in a snapshot of
/// the base, which a cancel must remove without touching the work.
/// </summary>
public sealed partial class RelayDriverCancelTests
{
    [Fact]
    public async Task RunTaskAsync_CancelledDuringTheBaselineVerify_CapturesTheWorkAndRemovesTheBaseSnapshot()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: true, enableFixVerify: false);
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),              // stage 5 author gate
            new TestRunResult(1, "Failed NewTest"),   // stage 10 verify, in its snapshot
            new TestRunResult(1, "Failed NewTest"));  // stage 10 retry

        // The base snapshot is the one tree holding the committed file while the checkout holds the change.
        var cancelling = await AssertCancelCapturesTheWorkAsync(repo, tests, (_, root) => Task.FromResult(
            root != repo.Root && ReadApp(root) == "committed\n" && ReadApp(repo.Root) == "half-finished\n"));

        Assert.False(Directory.Exists(cancelling.CancelledAt), "the base snapshot must be removed on a fresh token");
    }

    [Fact]
    public async Task RunTaskAsync_CancelledDuringTheGuardBaseline_CapturesTheWorkInsteadOfStrandingIt()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: true, enableFixVerify: false,
            guardCmd: ProbeGuardCmd);
        var tests = new RelayDriverRepoGuardTests.CommandDispatchTestRunner(
            (ProbeGuardCmd, new ViolationGuardRunner()),
            ("*", new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green"))));

        await AssertCancelCapturesTheWorkAsync(repo, tests, HoldsARedGateStashAsync);
    }

    [Fact]
    public async Task AuthorTestGate_CancelledWhileTheImplementationIsStripped_PutsItBack()
    {
        using var repo = TestRepository.Create();
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app.cs", "committed\n");
        sim.Commit(repo.Root, "chore: seed repo");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "src", "app.cs"), "half-finished\n",
            TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        var tests = new CancelWhenTestRunner(sim, cts, new ScriptedTestRunner(), HoldsARedGateStashAsync);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AuthorTestGate.RunAsync(
            repo.Root, "strip", "run-1", ["src/app.cs", "tests/app.tests.cs"], ["tests/app.tests.cs"],
            "dotnet test tests/app.tests.cs", tests, new CancellationHonoringGitInvoker(sim), cts.Token));

        Assert.NotNull(tests.CancelledAt);
        Assert.DoesNotContain("relay-redgate", (await sim.Git(repo.Root, "stash", "list")).Output);
        Assert.Equal("half-finished\n",
            await File.ReadAllTextAsync(Path.Combine(repo.Root, "src", "app.cs"), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Runs a committing task that writes <c>src/app.cs</c> at Implement, cancels it the moment
    /// <paramref name="cancelNow"/> says so, and checks the work is in the flagged-work bundle.
    /// </summary>
    private static async Task<CancelWhenTestRunner> AssertCancelCapturesTheWorkAsync(
        TestRepository repo, ITestRunner tests, Func<GitSimEngine, string, Task<bool>> cancelNow)
    {
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, ".gitignore", ".relay/\n");
        sim.Seed(repo.Root, "src/app.cs", "committed\n");
        sim.Commit(repo.Root, "chore: seed repo");
        repo.WriteTask("stashed", "# Cancel while the checkout is stashed\n");
        using var cts = new CancellationTokenSource();
        var subagent = new ScriptedSubagentRunner();
        subagent.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var cancelling = new CancelWhenTestRunner(sim, cts, tests, cancelNow);
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(
                new FileWritingSubagentRunner(subagent, 6, "src/app.cs", "half-finished\n"),
                cancelling, new InMemoryRelayEventSink(), new CancellationHonoringGitInvoker(sim)),
            new RelayDriverOptions(CreateGitCommit: true));

        var outcome = await driver.RunTaskAsync(repo.Root, "stashed", cts.Token);

        Assert.NotNull(cancelling.CancelledAt);
        Assert.Equal("cancelled by operator", outcome.Reason);
        Assert.DoesNotContain("relay-redgate", (await sim.Git(repo.Root, "stash", "list")).Output);
        var restore = await FlaggedWorkStore.RestoreAsync(
            repo.Root, "stashed", Path.Combine(repo.Root, ".relay", "stashed"), sim, CancellationToken.None);
        Assert.True(restore.IsSuccess, "the cancel must capture the task's change, not a stashed-away clean tree");
        Assert.Equal("half-finished\n",
            await File.ReadAllTextAsync(Path.Combine(repo.Root, "src", "app.cs"), TestContext.Current.CancellationToken));
        return cancelling;
    }

    private static async Task<bool> HoldsARedGateStashAsync(GitSimEngine sim, string root) =>
        (await sim.Git(root, "stash", "list")).Output.Contains("relay-redgate", StringComparison.Ordinal);

    private static string? ReadApp(string root)
    {
        var path = Path.Combine(root, "src", "app.cs");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>A guard that reports the same oversize file on every tree it is run against.</summary>
    private sealed class ViolationGuardRunner : ITestRunner
    {
        public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TestRunResult(1, "ERROR: src/app.cs is 305 lines (limit: 300)"));
    }

    /// <summary>Runs <paramref name="inner"/>, except that the operator cancels at the first run <paramref name="cancelNow"/> picks.</summary>
    private sealed class CancelWhenTestRunner(
        GitSimEngine sim, CancellationTokenSource cts, ITestRunner inner, Func<GitSimEngine, string, Task<bool>> cancelNow) : ITestRunner
    {
        /// <summary>The root the cancelled run was in, or null when nothing was cancelled.</summary>
        public string? CancelledAt { get; private set; }

        public async Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
        {
            if (CancelledAt is not null || !await cancelNow(sim, rootPath))
                return await inner.RunAsync(rootPath, command, cancellationToken);

            CancelledAt = rootPath;
            await cts.CancelAsync();
            throw new OperationCanceledException(cts.Token);
        }
    }

    /// <summary>Throws on a cancelled token the way the real invoker does, which kills git before it can act.</summary>
    private sealed class CancellationHonoringGitInvoker(IGitInvoker inner) : IGitInvoker
    {
        public Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
            string rootPath, IEnumerable<string> arguments, CancellationToken cancellationToken,
            TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken killToken = default, Action<string>? onActivity = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return inner.RunAsync(rootPath, arguments, cancellationToken, timeout, environment, killToken, onActivity);
        }
    }
}
