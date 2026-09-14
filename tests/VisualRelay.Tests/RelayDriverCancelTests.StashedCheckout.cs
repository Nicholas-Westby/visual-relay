using VisualRelay.Core.Execution;
using VisualRelay.Domain;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// The baseline verify, the guard's baseline and the stage-5 red gate each stash the
/// checkout, run a command, and put the stash back in a <c>finally</c>. A cancel reaches
/// that <c>finally</c> with the run's token already cancelled, and a git call handed a
/// cancelled token is killed before it does anything. Found with max-sixty/worktrunk on the
/// Mac: a cancel during the baseline verify left the task's whole change in a relay-redgate
/// stash, the flagged-work capture saw a clean tree, and the resumed run had nothing to commit.
/// </summary>
public sealed partial class RelayDriverCancelTests
{
    [Fact]
    public async Task RunTaskAsync_CancelledDuringTheBaselineVerify_CapturesTheWorkInsteadOfStrandingIt()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: true, enableFixVerify: false);
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),              // stage 5 author gate
            new TestRunResult(1, "Failed NewTest"),   // stage 10 verify, in its snapshot
            new TestRunResult(1, "Failed NewTest"));  // stage 10 retry

        await AssertCancelWhileStashedCapturesTheWorkAsync(repo, tests);
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

        await AssertCancelWhileStashedCapturesTheWorkAsync(repo, tests);
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
        var tests = new CancelWhileStashedTestRunner(sim, cts, new ScriptedTestRunner());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AuthorTestGate.RunAsync(
            repo.Root, "strip", "run-1", ["src/app.cs", "tests/app.tests.cs"], ["tests/app.tests.cs"],
            "dotnet test tests/app.tests.cs", tests, new CancellationHonoringGitInvoker(sim), cts.Token));

        Assert.True(tests.CancelledWhileStashed);
        Assert.DoesNotContain("relay-redgate", (await sim.Git(repo.Root, "stash", "list")).Output);
        Assert.Equal("half-finished\n",
            await File.ReadAllTextAsync(Path.Combine(repo.Root, "src", "app.cs"), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Runs a committing task that writes <c>src/app.cs</c> at Implement, cancels it the moment a
    /// relay-redgate stash holds the checkout, and checks the work is in the flagged-work bundle.
    /// </summary>
    private static async Task AssertCancelWhileStashedCapturesTheWorkAsync(TestRepository repo, ITestRunner tests)
    {
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, ".gitignore", ".relay/\n");
        sim.Seed(repo.Root, "src/app.cs", "committed\n");
        sim.Commit(repo.Root, "chore: seed repo");
        repo.WriteTask("stashed", "# Cancel while the checkout is stashed\n");
        using var cts = new CancellationTokenSource();
        var subagent = new ScriptedSubagentRunner();
        subagent.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var cancelling = new CancelWhileStashedTestRunner(sim, cts, tests);
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(
                new FileWritingSubagentRunner(subagent, 6, "src/app.cs", "half-finished\n"),
                cancelling, new InMemoryRelayEventSink(), new CancellationHonoringGitInvoker(sim)),
            new RelayDriverOptions(CreateGitCommit: true));

        var outcome = await driver.RunTaskAsync(repo.Root, "stashed", cts.Token);

        Assert.True(cancelling.CancelledWhileStashed);
        Assert.Equal("cancelled by operator", outcome.Reason);
        Assert.DoesNotContain("relay-redgate", (await sim.Git(repo.Root, "stash", "list")).Output);
        var restore = await FlaggedWorkStore.RestoreAsync(
            repo.Root, "stashed", Path.Combine(repo.Root, ".relay", "stashed"), sim, CancellationToken.None);
        Assert.True(restore.IsSuccess, "the cancel must capture the task's change, not a stashed-away clean tree");
        Assert.Equal("half-finished\n",
            await File.ReadAllTextAsync(Path.Combine(repo.Root, "src", "app.cs"), TestContext.Current.CancellationToken));
    }

    /// <summary>A guard that reports the same oversize file on every tree it is run against.</summary>
    private sealed class ViolationGuardRunner : ITestRunner
    {
        public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TestRunResult(1, "ERROR: src/app.cs is 305 lines (limit: 300)"));
    }

    /// <summary>Runs <paramref name="inner"/>, except that the operator cancels while a relay-redgate stash holds the checkout.</summary>
    private sealed class CancelWhileStashedTestRunner(GitSimEngine sim, CancellationTokenSource cts, ITestRunner inner) : ITestRunner
    {
        public bool CancelledWhileStashed { get; private set; }

        public async Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
        {
            if (!(await sim.Git(rootPath, "stash", "list")).Output.Contains("relay-redgate", StringComparison.Ordinal))
                return await inner.RunAsync(rootPath, command, cancellationToken);

            CancelledWhileStashed = true;
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
