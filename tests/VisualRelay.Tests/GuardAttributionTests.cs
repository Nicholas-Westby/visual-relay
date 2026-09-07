using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A red guard with green tests is only the environment's fault when the SAME guard
/// command also fails on a pristine checkout of the run base. When it passes there,
/// the change broke it — which is exactly what Fix-verify repairs — so the task
/// escalates as before instead of being flagged.
/// </summary>
public sealed class GuardAttributionTests
{
    private const string GuardCmd = "tools/guards/check-file-size.sh";

    /// <summary>
    /// The guard passes on the untouched base and fails on the change: Fix-verify runs,
    /// the agent gets the guard's own output, and the repaired task commits.
    /// </summary>
    [Fact]
    public async Task GuardGreenOnBase_RedOnChange_RepairsThroughFixVerifyAndCommits()
    {
        using var repo = SeededRepo(out var sim);
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true,
            guardCmd: GuardCmd);
        repo.WriteTask("new-oversize", "# Add an oversized file\n");
        var subagent = new CapturingSubagentRunner();
        subagent.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var sink = new InMemoryRelayEventSink();
        var runner = new GuardTreeAwareTestRunner(
            repo.Root, GuardCmd,
            workingTree: new ScriptedTestRunner(
                new TestRunResult(1, "ERROR: src/new-file.cs is 305 lines (limit: 300)"),
                new TestRunResult(0, "guard clean")),
            onBase: new TestRunResult(0, "guard clean"),
            other: new ScriptedTestRunner(
                new TestRunResult(1, "red"),            // stage 5 author gate
                new TestRunResult(0, "All tests pass"), // stage 10 suite
                new TestRunResult(0, "All tests pass"))); // fix-verify run 1 suite
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(subagent, runner, sink, sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "new-oversize");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var fixVerify = Assert.Single(subagent.Invocations, i => i.Stage.Number == 11);
        Assert.Contains("new-file.cs", fixVerify.LastTestOutput!, StringComparison.Ordinal);
        Assert.Single(runner.BaseProbes);
        Assert.Contains(sink.Events, e =>
            e is { Level: "info", EventName: "guard_attribution", Data: not null }
            && e.Data["verdict"] == "change");
    }

    /// <summary>
    /// The guard fails on the untouched base too: no edit could make it green, so the
    /// task is flagged as an environment failure and Fix-verify never runs.
    /// </summary>
    [Fact]
    public async Task GuardRedOnBaseToo_FlagsAsEnvironmentFailureWithoutFixVerify()
    {
        using var repo = SeededRepo(out var sim);
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true,
            guardCmd: GuardCmd);
        repo.WriteTask("broken-toolchain", "# The guard cannot run here\n");
        var subagent = new CapturingSubagentRunner();
        subagent.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var sink = new InMemoryRelayEventSink();
        var runner = new GuardTreeAwareTestRunner(
            repo.Root, GuardCmd,
            workingTree: new ScriptedTestRunner(
                new TestRunResult(1, "error: unable to write module cache")),
            onBase: new TestRunResult(1, "error: unable to write module cache"),
            other: new ScriptedTestRunner(
                new TestRunResult(1, "red"),             // stage 5 author gate
                new TestRunResult(0, "All tests pass"))); // stage 10 suite
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(subagent, runner, sink, sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "broken-toolchain");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.DoesNotContain(subagent.Invocations, i => i.Stage.Number == 11);
        Assert.Contains("environment failure", outcome.Reason!, StringComparison.Ordinal);
        Assert.Single(runner.BaseProbes);
        Assert.Contains(sink.Events, e =>
            e is { Level: "warn", EventName: "environment_failure" });
        Assert.Contains(sink.Events, e =>
            e is { Level: "info", EventName: "guard_attribution", Data: not null }
            && e.Data["verdict"] == "environment");
    }

    /// <summary>
    /// Attribution is asked twice — once at the stage-10 gate and again when the guard is
    /// still red inside the Fix-verify loop — but the base is checked out and probed only
    /// once, because the answer is cached per (repo, guard command, base commit).
    /// </summary>
    [Fact]
    public async Task PristineBaseGuard_ProbedOnce_AcrossStage10AndTheFixVerifyLoop()
    {
        using var repo = SeededRepo(out var sim);
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true,
            guardCmd: GuardCmd, maxStageFailures: 3);
        repo.WriteTask("two-rounds", "# Takes two rounds to repair\n");
        var subagent = new CapturingSubagentRunner();
        subagent.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var runner = new GuardTreeAwareTestRunner(
            repo.Root, GuardCmd,
            workingTree: new ScriptedTestRunner(
                new TestRunResult(1, "ERROR: src/first.cs is 305 lines (limit: 300)"),
                new TestRunResult(1, "ERROR: src/first.cs is 302 lines (limit: 300)"),
                new TestRunResult(0, "guard clean")),
            onBase: new TestRunResult(0, "guard clean"),
            other: new ScriptedTestRunner(
                new TestRunResult(1, "red"),            // stage 5 author gate
                new TestRunResult(0, "All tests pass"), // stage 10 suite
                new TestRunResult(0, "All tests pass"), // fix-verify run 1 suite
                new TestRunResult(0, "All tests pass"))); // fix-verify run 2 suite
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(subagent, runner, new InMemoryRelayEventSink(), sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "two-rounds");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.Equal(2, subagent.Invocations.Count(i => i.Stage.Number == 11));
        Assert.Single(runner.BaseProbes);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// A repo whose GitSim has one commit, so the attribution probe has a base commit to
    /// check out into a pristine worktree.
    /// </summary>
    private static TestRepository SeededRepo(out VisualRelay.GitSim.GitSim sim)
    {
        var repo = TestRepository.Create();
        sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, ".gitignore", ".relay/\n");
        sim.Seed(repo.Root, "src/app.cs", "old\n");
        sim.Commit(repo.Root, "chore: seed repo");
        return repo;
    }

    /// <summary>
    /// Answers the guard command according to WHICH tree it is asked about: the task's
    /// working tree (<paramref name="workingRoot"/>) draws from a scripted queue, while
    /// any other root — the pristine base checkout the attribution probe creates — gets
    /// <paramref name="onBase"/> and is recorded. Every other command falls through to
    /// <paramref name="other"/>.
    /// </summary>
    private sealed class GuardTreeAwareTestRunner(
        string workingRoot,
        string guardSentinel,
        ITestRunner workingTree,
        TestRunResult onBase,
        ITestRunner other) : ITestRunner
    {
        private readonly List<string> _baseProbes = [];

        /// <summary>Roots, other than the working tree, the guard was run against.</summary>
        public IReadOnlyList<string> BaseProbes => _baseProbes;

        public Task<TestRunResult> RunAsync(
            string rootPath, string command, CancellationToken cancellationToken = default)
        {
            if (!command.Contains(guardSentinel, StringComparison.Ordinal))
                return other.RunAsync(rootPath, command, cancellationToken);
            if (string.Equals(rootPath, workingRoot, StringComparison.Ordinal))
                return workingTree.RunAsync(rootPath, command, cancellationToken);
            _baseProbes.Add(rootPath);
            return Task.FromResult(onBase);
        }
    }
}
