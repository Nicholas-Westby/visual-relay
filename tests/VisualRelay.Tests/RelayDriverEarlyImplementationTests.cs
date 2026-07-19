using VisualRelay.Core.Execution;
using VisualRelay.Domain;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

public sealed class RelayDriverEarlyImplementationTests
{
    [Fact]
    public async Task Implement_DownshiftedToCheap_WhenStage3FrontLoaded()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("front-loaded", "# Front-loaded\n");
        var sim = InitGitRepo(repo);

        var capturer = new CapturingSubagentRunner();
        capturer.SeedHappyPath("src/status.cs", "tests/status.test");

        // Wrap so the stage-3 front-load double writes src/status.cs, but all
        // stage responses come from the scripted runner (via the capturer).
        var runner = new Stage3FrontLoadCapturingRunner(new Stage3FrontLoadRunner(), capturer);

        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(
                new TestRunResult(1, "red"),
                new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(), sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "front-loaded");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Stage 5's WorktreeFilter reverts the stage-3 front-loaded production
        // edit back to HEAD, so the implementation is no longer in the working
        // tree at stage 6.  Stage 6 therefore uses the normal "balanced" tier
        // and Implement prompt (not the down-shifted ConfirmImplementation).
        var stage6Invocation = capturer.Invocations.Single(i => i.Stage.Number == 6);
        Assert.Equal("balanced", stage6Invocation.Tier);
        Assert.DoesNotContain("do NOT re-narrate", stage6Invocation.Stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);

        // Stage 7 (Review) must still run on frontier.
        var stage7Invocation = capturer.Invocations.Single(i => i.Stage.Number == 7);
        Assert.Equal("frontier", stage7Invocation.Tier);
    }

    [Fact]
    public async Task Implement_StaysBalanced_WhenNoFrontLoad()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("clean-change", "# Clean change\n");
        var sim = InitGitRepo(repo);

        var capturer = new CapturingSubagentRunner();
        capturer.SeedHappyPath("src/status.cs", "tests/status.test");

        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(capturer, new ScriptedTestRunner(
                new TestRunResult(1, "red"),
                new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(), sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "clean-change");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Stage 6 (Implement) should keep its default "balanced" tier.
        var stage6Invocation = capturer.Invocations.Single(i => i.Stage.Number == 6);
        Assert.Equal("balanced", stage6Invocation.Tier);
    }

    [Fact]
    public async Task Downshift_Disabled_KeepsBalanced()
    {
        using var repo = TestRepository.Create();
        // Write a config that explicitly disables the down-shift.
        repo.WriteConfigWithDownshift("dotnet test", [], downshiftOnEarlyImplementation: false);
        repo.WriteTask("disabled-front", "# Disabled\n");
        var sim = InitGitRepo(repo);

        var capturer = new CapturingSubagentRunner();
        capturer.SeedHappyPath("src/status.cs", "tests/status.test");

        var runner = new Stage3FrontLoadCapturingRunner(new Stage3FrontLoadRunner(), capturer);

        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(
                new TestRunResult(1, "red"),
                new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(), sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "disabled-front");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Even though the change was front-loaded, the config kill-switch keeps
        // Implement on "balanced".
        var stage6Invocation = capturer.Invocations.Single(i => i.Stage.Number == 6);
        Assert.Equal("balanced", stage6Invocation.Tier);
    }

    [Fact]
    public async Task Recording_Unchanged_WhenDownshifted()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("record-test", "# Record test\n");
        var sim = InitGitRepo(repo);

        var capturer = new CapturingSubagentRunner();
        capturer.SeedHappyPath("src/status.cs", "tests/status.test");

        var runner = new Stage3FrontLoadCapturingRunner(new Stage3FrontLoadRunner(), capturer);

        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(
                new TestRunResult(1, "red"),
                new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(), sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "record-test");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // The ledger heading and seal must record the canonical stage identity.
        var ledger = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "record-test", "ledger.md"));
        Assert.Contains("## Stage 6 - Implement", ledger, StringComparison.Ordinal);

        var seals = await File.ReadAllLinesAsync(
            Path.Combine(repo.Root, ".relay", "record-test", "record-test.seals"));
        Assert.Contains(seals, line => line.Contains("\"n\":6", StringComparison.Ordinal));
    }

    private static GitSimEngine InitGitRepo(TestRepository repo)
    {
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/status.cs", "old\n");
        sim.Commit(repo.Root, "chore: seed repo");
        return sim;
    }

    /// <summary>
    /// Delegates stage 3 to a front-load double (which writes impl files) and all
    /// other stages to a <see cref="CapturingSubagentRunner"/> so invocations are
    /// recorded for assertion.
    /// </summary>
    [Fact]
    public async Task EarlyImplementationProbe_UsesInjectedGitInvoker()
    {
        // Prove that the stage-4 early-implementation probe flows through the
        // INJECTED git invoker, not a private `new GitInvoker()` fallback.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("probe-inject", "# Probe inject test\n");
        var sim = InitGitRepo(repo);

        var recorder = new RecordingGitInvoker(sim);
        var capturer = new CapturingSubagentRunner();
        capturer.SeedHappyPath("src/status.cs", "tests/status.test");

        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(capturer, new ScriptedTestRunner(
                new TestRunResult(1, "red"),
                new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(), recorder),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "probe-inject");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // The stage-4 early-implementation probe must use the injected invoker.
        Assert.True(recorder.RecordedCall(["rev-parse", "--is-inside-work-tree"]),
            "rev-parse --is-inside-work-tree must flow through the injected invoker");
    }

    private sealed class Stage3FrontLoadCapturingRunner(
        ISubagentRunner frontLoader, CapturingSubagentRunner capturer) : ISubagentRunner
    {

        public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
        {
            if (invocation.Stage.Number == 3)
                return frontLoader.RunAsync(invocation, cancellationToken);

            return capturer.RunAsync(invocation, cancellationToken);
        }
    }
}
