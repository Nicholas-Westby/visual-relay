using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The completion gate must reject a run that was expected to produce code but
/// left the tracked source/test tree unchanged — the "phantom completion" hole
/// where a task was retired to DONE- with only .relay proof + the spec rename.
/// </summary>
public sealed class RelayDriverCodeChangeGateTests
{
    [Fact]
    public async Task RunTaskAsync_CodeExpectingByChecklist_ProducesNoSourceChange_IsFlaggedNotDone()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("true", []);
        // A '## Deliverables' checklist marks this as code-expecting even though the
        // manifest carries no impl file. The agent writes nothing → phantom.
        repo.WriteTask("design-tokens",
            "# Centralize design tokens\n\n## Deliverables\n- Create the tokens module\n");
        SeedGit(repo.Root);

        var runner = new GateScenarioRunner("[]", "[]");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "design-tokens");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.Contains("no changes", outcome.Reason ?? "", StringComparison.OrdinalIgnoreCase);
        // Not retired to DONE-, not committed.
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "design-tokens.md")));
        Assert.False(Directory.Exists(Path.Combine(repo.Root, "llm-tasks", "completed")));
        Assert.Equal("1", TestGit.Run(repo.Root, "rev-list", "--count", "HEAD").Trim());
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "design-tokens", "NEEDS-REVIEW")));
    }

    [Fact]
    public async Task RunTaskAsync_CodeExpectingByManifestImpl_ProducesNoSourceChange_IsFlagged()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("true", []);
        // No checklist — code-expecting derives purely from the impl file in the
        // stage-4 manifest. The agent authors nothing, so nothing changes.
        repo.WriteTask("ghost-impl", "# Add a helper\n\nWire it up.\n");
        SeedGit(repo.Root);

        var runner = new GateScenarioRunner("[\"src/helper.cs\"]", "[]");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ghost-impl");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.Contains("no changes", outcome.Reason ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ghost-impl.md")));
        Assert.Equal("1", TestGit.Run(repo.Root, "rev-list", "--count", "HEAD").Trim());
    }

    [Fact]
    public async Task RunTaskAsync_CodeExpecting_WithRealSourceChange_Completes()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("true", []);
        repo.WriteTask("real-tokens",
            "# Centralize design tokens\n\n## Deliverables\n- Create the tokens module\n");
        SeedGit(repo.Root);

        // The agent authors a real, non-bookkeeping source file at Implement.
        var runner = new GateScenarioRunner("[\"src/tokens.cs\"]", "[]",
            writeAtStage6: inv =>
            {
                Directory.CreateDirectory(Path.Combine(inv.TargetRoot, "src"));
                File.WriteAllText(Path.Combine(inv.TargetRoot, "src", "tokens.cs"), "public static class Tokens {}");
            });
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "real-tokens");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        Assert.Equal("2", TestGit.Run(repo.Root, "rev-list", "--count", "HEAD").Trim());
        var names = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");
        Assert.Contains("src/tokens.cs", names);
    }

    [Fact]
    public async Task RunTaskAsync_CodeExpecting_OnlyInternalScratchChanged_IsFlagged()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("true", []);
        repo.WriteTask("scratch-only",
            "# Do the work\n\n## Deliverables\n- Create the tokens module\n");
        SeedGit(repo.Root);

        // The only thing the run leaves behind is VR-internal swival scratch — a
        // general target repo does not gitignore .swival/, so it shows up as
        // untracked. It must be treated as bookkeeping, not as produced code.
        var runner = new GateScenarioRunner("[]", "[]",
            writeAtStage6: inv =>
            {
                Directory.CreateDirectory(Path.Combine(inv.TargetRoot, ".swival"));
                File.WriteAllText(Path.Combine(inv.TargetRoot, ".swival", "cmd_output.txt"), "scratch");
            });
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "scratch-only");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.Contains("no changes", outcome.Reason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunTaskAsync_ObservationalTask_NoImplNoChecklist_CompletesWithoutFalsePositive()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("true", []);
        // No impl in the manifest and no deliverables/done-when checklist →
        // a legitimately read-only task that must still complete.
        repo.WriteTask("observe-only", "# Observe the codebase\n\nJust look around and report.\n");
        SeedGit(repo.Root);

        var runner = new GateScenarioRunner("[]", "[]");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "observe-only");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        // The read-only task is retired to DONE- exactly as before the gate existed.
        Assert.False(File.Exists(Path.Combine(repo.Root, "llm-tasks", "observe-only.md")));
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "completed", "DONE-observe-only.md")));
    }

    private static void SeedGit(string root)
    {
        TestGit.Run(root, "init");
        TestGit.Run(root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(root, "add", ".");
        TestGit.Run(root, "commit", "-m", "chore: seed repo");
    }
}

/// <summary>
/// Configurable pipeline runner: emits a fixed stage-4 <c>manifest</c> and stage-5
/// <c>testFiles</c> and optionally writes files at Implement (stage 6). Everything
/// else is a minimal green contract, so tests can dial in exactly which
/// code-expecting signal fires and whether a real change lands.
/// </summary>
internal sealed class GateScenarioRunner(
    string manifestJson,
    string testFilesJson,
    Action<StageInvocation>? writeAtStage6 = null) : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 6)
            writeAtStage6?.Invoke(invocation);

        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"none","excerpts":[],"repro":"none"}""",
            4 => $$"""{"plan":"create tokens module","manifest":{{manifestJson}}}""",
            5 => $$"""{"testFiles":{{testFilesJson}},"rationale":"red first"}""",
            6 => """{"summary":"implemented"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"fixed"}""",
            9 => """{"summary":"verified","commitMessages":["fix(core): centralize tokens"]}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}
