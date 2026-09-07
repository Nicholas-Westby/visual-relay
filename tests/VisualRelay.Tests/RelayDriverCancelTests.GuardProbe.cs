using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The guard-attribution probe checks the repository's guard command out into a
/// throwaway worktree. Like every other worktree the run creates, that one is torn
/// down in a <c>finally</c> — which is reached with the run's token already
/// cancelled, so the teardown must not be handed it.
/// </summary>
public sealed partial class RelayDriverCancelTests
{
    [Fact]
    public async Task RunTaskAsync_CancelledInsideTheGuardProbe_RemovesTheProbeWorktreeOnAFreshToken()
    {
        using var repo = TestRepository.Create();
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, ".gitignore", ".relay/\n");
        sim.Seed(repo.Root, "src/app.cs", "old\n");
        sim.Commit(repo.Root, "chore: seed repo");
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true,
            guardCmd: ProbeGuardCmd);
        repo.WriteTask("cancel-probe", "# Cancel inside the probe\n");
        var git = new TokenRecordingGitInvoker(sim);
        using var cts = new CancellationTokenSource();
        var subagent = new ScriptedSubagentRunner();
        subagent.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(subagent,
                new CancelInsideGuardProbeTestRunner(cts, repo.Root), new InMemoryRelayEventSink(), git),
            RelayDriverOptions.NoGitCommit);

        await driver.RunTaskAsync(repo.Root, "cancel-probe", cts.Token);

        var removals = git.Calls
            .Where(c => c.Arguments is ["worktree", "remove", ..]
                     && c.Arguments.Any(a => a.Contains("-guard-base", StringComparison.Ordinal)))
            .ToList();
        Assert.NotEmpty(removals);
        Assert.All(removals, c => Assert.False(c.TokenWasCancelled,
            "the probe worktree must be removed through git, not left registered by a cancelled token"));
    }

    private const string ProbeGuardCmd = "tools/guards/check-file-size.sh";

    /// <summary>
    /// Green suite, red guard — the precondition for attribution — and a cancel the
    /// moment the guard is run against the probe's pristine base checkout.
    /// </summary>
    private sealed class CancelInsideGuardProbeTestRunner(CancellationTokenSource cts, string mainRoot) : ITestRunner
    {
        private readonly ScriptedTestRunner _suite = new(
            new TestRunResult(1, "red"),               // stage 5 author gate
            new TestRunResult(0, "All tests pass"));   // stage 10 suite

        public Task<TestRunResult> RunAsync(
            string rootPath, string command, CancellationToken cancellationToken = default)
        {
            if (!command.Contains(ProbeGuardCmd, StringComparison.Ordinal))
                return _suite.RunAsync(rootPath, command, cancellationToken);

            if (string.Equals(rootPath, mainRoot, StringComparison.Ordinal))
                return Task.FromResult(new TestRunResult(1, "ERROR: src/app.cs is 305 lines (limit: 300)"));

            // Inside the probe's base checkout: the operator cancels mid-probe.
            cts.Cancel();
            return Task.FromResult(new TestRunResult(1, "ERROR: src/app.cs is 305 lines (limit: 300)"));
        }
    }
}
