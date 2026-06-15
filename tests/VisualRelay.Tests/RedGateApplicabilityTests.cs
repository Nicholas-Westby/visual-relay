using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RedGateApplicabilityTests
{
    /// <summary>
    /// A code-only change (e.g. a .axaml markup file) with no authored tests:
    /// WorktreeFilter reverts the production edit before the red-gate runs,
    /// so the gate sees a clean tree, finds nothing to stash, and the task
    /// commits (the plan explicitly stated no tests were needed).
    /// </summary>
    [Fact]
    public async Task AxamlOnlyChange_TriggersRedGate()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("tweak-markup", "# Tweak markup\n");
        // Create a git repo with a committed impl file.
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "Panel.axaml"), "old\n");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "chore: seed repo");
        // The production file is dirty before stage 5, simulating an agent
        // edit.  WorktreeFilter at stage 5 reverts it to HEAD because the
        // agent returned no testFiles — non-test edits are discarded.
        File.WriteAllText(Path.Combine(repo.Root, "src", "Panel.axaml"), "new\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedCodeOnly("src/Panel.axaml");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(
                runner,
                new ScriptedTestRunner(new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "tweak-markup");

        // The production edit was reverted by WorktreeFilter at stage 5,
        // so the red-gate sees a clean tree and the task commits. The plan
        // explicitly stated no tests were needed.
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
    }

    /// <summary>
    /// A change consisting entirely of non-code files (documentation, config)
    /// has no implementation to test. The red gate must be skipped and the
    /// task committed without requiring a failing test.
    /// </summary>
    [Fact]
    public async Task NonCodeOnlyChange_SkipsRedGateAndCommits()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("update-readme", "# Update README\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedNonCodeOnly("docs/README.md");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(
                runner,
                new ScriptedTestRunner(new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "update-readme");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
    }

    /// <summary>
    /// A test-only change (adding a regression test for already-correct behavior)
    /// must be allowed to commit without flagging. The red gate must be skipped
    /// when every manifest entry is a declared test file — there is no
    /// implementation code to strip.
    /// </summary>
    [Fact]
    public async Task TestOnlyChange_SkipsRedGateAndCommits()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("add-regression-test", "# Add regression test\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedTestOnly("tests/regression.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(
                runner,
                new ScriptedTestRunner(new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "add-regression-test");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
    }

    /// <summary>
    /// When the manifest includes an impl file that has no working-tree change
    /// (the fix is already committed), and the authored test passes green, the
    /// gate must accept the green regression coverage instead of flagging.
    /// StashedImplementation is false because there was nothing to strip.
    /// </summary>
    [Fact]
    public async Task AlreadyResolved_NoImplDelta_AcceptsGreenRegressionCoverage()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("already-resolved", "# Add regression test\n");
        // Impl file is already committed (fix already present) — no working-tree delta.
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "fix.ts"), "correct code\n");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "chore: seed repo");
        var runner = new AlreadyResolvedSubagentRunner("src/fix.ts", "tests/regression.test.ts");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(
                runner,
                new ScriptedTestRunner(new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "already-resolved");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var ledger = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "already-resolved", "ledger.md"));
        Assert.Contains("Already-resolved", ledger, StringComparison.Ordinal);
    }
}

internal sealed class AlreadyResolvedSubagentRunner : ISubagentRunner
{
    private readonly string _implFile;
    private readonly string _testFile;

    public AlreadyResolvedSubagentRunner(string implFile, string testFile)
    {
        _implFile = implFile;
        _testFile = testFile;
    }

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"none","excerpts":[],"repro":"none"}""",
            4 => $$"""{"plan":"add regression test","manifest":["{{_implFile}}","{{_testFile}}"]}""",
            5 => $$"""{"testFiles":["{{_testFile}}"],"rationale":"green regression coverage for pre-existing fix"}""",
            6 => """{"summary":"no implementation changes needed"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"no changes"}""",
            9 => """{"summary":"verified","commitMessages":["test: add regression coverage"]}""",
            10 => """{"summary":"no changes"}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}
