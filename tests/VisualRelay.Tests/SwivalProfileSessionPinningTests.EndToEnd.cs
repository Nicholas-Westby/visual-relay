using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class SwivalProfileSessionPinningTests
{
    // ──────────────────────────────────────────────────────────────
    // End-to-end tests through RelayDriver
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A task whose Implement stage edits swival.toml must NOT change the
    /// pinned profile content that subsequent stages launch with. The pinned
    /// content is frozen at run start and passed through StageInvocation
    /// unchanged regardless of tree edits.
    /// </summary>
    [Fact]
    public async Task RunTaskAsync_PinsProfile_AcrossStages()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("rename-profile", "# Rename profile aliases\n");

        // Pre-create a swival.toml so RelayDriver snapshots it at run start.
        const string originalProfile = "[profiles.balanced]\nmodel = \"balanced-kimi\"\n";
        var tomlPath = Path.Combine(repo.Root, "swival.toml");
        await File.WriteAllTextAsync(tomlPath, originalProfile);

        var sink = new InMemoryRelayEventSink();
        var editor = new ProfileEditingSubagentRunner(
            editAtStage: 6, // Implement — edits swival.toml in the tree
            editContent: "[profiles.balanced]\nmodel = \"balanced\"\n",
            eventSink: sink);
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "green"));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(editor, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "rename-profile");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Every stage invocation must carry the original pinned content,
        // even stages after the tree edit (stage 6).
        Assert.All(editor.PinnedContents, pc => Assert.Equal(originalProfile, pc));

        // At least one invocation after stage 6 must have been received
        // (proving the assertion covers the post-edit window).
        Assert.Contains(editor.PinnedContentsByStage, kv => kv.Key >= 7);
    }

    /// <summary>
    /// The working tree's edited swival.toml must survive to commit — the
    /// pin/restore cycle must not discard the task's legitimate file edits.
    /// </summary>
    [Fact]
    public async Task RunTaskAsync_PreservesEditedSwivalToml_InWorkingTree()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("rename-profile", "# Rename profile aliases\n");

        const string originalProfile = "[profiles.balanced]\nmodel = \"balanced-kimi\"\n";
        var tomlPath = Path.Combine(repo.Root, "swival.toml");
        await File.WriteAllTextAsync(tomlPath, originalProfile);

        const string editedProfile = "[profiles.balanced]\nmodel = \"balanced\"\n";
        var sink = new InMemoryRelayEventSink();
        var editor = new ProfileEditingSubagentRunner(
            editAtStage: 6,
            editContent: editedProfile,
            eventSink: sink);
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "green"));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(editor, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "rename-profile");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // After the full run, the tree file must contain the task's edit,
        // NOT the original pinned content.
        var treeContent = await File.ReadAllTextAsync(tomlPath);
        Assert.Equal(editedProfile, treeContent);
    }

    /// <summary>
    /// When a task edit changes swival.toml so it diverges from the pinned
    /// snapshot, a "swival_profile_divergence" info event must be emitted
    /// so the operator knows a backend/profile swap is pending.
    /// </summary>
    [Fact]
    public async Task RunTaskAsync_EmitsDivergenceEvent_WhenTaskEditsProfile()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("rename-profile", "# Rename profile aliases\n");

        const string originalProfile = "[profiles.balanced]\nmodel = \"balanced-kimi\"\n";
        var tomlPath = Path.Combine(repo.Root, "swival.toml");
        await File.WriteAllTextAsync(tomlPath, originalProfile);

        var sink = new InMemoryRelayEventSink();
        var editor = new ProfileEditingSubagentRunner(
            editAtStage: 6,
            editContent: "[profiles.balanced]\nmodel = \"balanced\"\n",
            eventSink: sink);
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "green"));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(editor, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "rename-profile");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // The divergence event must fire at least once — when the pinned
        // content no longer matches the tree (after stage 6's edit).
        var divergenceEvents = sink.Events
            .Where(e => e.EventName == "swival_profile_divergence")
            .ToList();
        Assert.NotEmpty(divergenceEvents);
        Assert.All(divergenceEvents, e => Assert.Equal("info", e.Level));
    }

    /// <summary>
    /// When no task edits touch swival.toml, no spurious divergence events
    /// should be emitted — the pinned content matches the tree throughout.
    /// </summary>
    [Fact]
    public async Task RunTaskAsync_NoDivergenceEvent_WhenProfileUnchanged()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("unrelated-task", "# Unrelated task\n");

        const string profile = "[profiles.balanced]\nmodel = \"balanced\"\n";
        var tomlPath = Path.Combine(repo.Root, "swival.toml");
        await File.WriteAllTextAsync(tomlPath, profile);

        // ScriptedSubagentRunner does NOT edit swival.toml — profile stays unchanged.
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "green"));
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "unrelated-task");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.DoesNotContain(sink.Events, e => e.EventName == "swival_profile_divergence");
    }
}

// ──────────────────────────────────────────────────────────────
// Test doubles
// ──────────────────────────────────────────────────────────────

/// <summary>
/// Wraps a <see cref="ScriptedSubagentRunner"/> and, at a specified stage,
/// edits swival.toml in the working tree — simulating a task whose edits
/// touch the pipeline's own launch configuration. Records every
/// <see cref="StageInvocation.PinnedSwivalProfileContent"/> value seen so
/// tests can assert the pinned content remains frozen across stages.
/// When pinned content is present, calls
/// <see cref="SwivalProfileSession.PrepareWithPinnedContentAsync"/> to
/// simulate the real <see cref="SwivalSubagentRunner"/> profile lifecycle
/// (including divergence event emission and save/restore of tree content).
/// The simulated swival.toml edit happens INSIDE the session lifetime, so
/// <see cref="SwivalProfileSession.DisposeAsync"/> can distinguish task
/// edits (leave untouched) from no-op sessions (restore original).
/// </summary>
internal sealed class ProfileEditingSubagentRunner(
    int editAtStage, string editContent, IRelayEventSink? eventSink = null) : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();
    private readonly List<string?> _pinnedContents = [];
    private readonly Dictionary<int, string?> _pinnedContentsByStage = [];

    /// <summary>
    /// Every <see cref="StageInvocation.PinnedSwivalProfileContent"/> value
    /// received across all stages, in invocation order.
    /// </summary>
    public IReadOnlyList<string?> PinnedContents => _pinnedContents;

    /// <summary>
    /// <see cref="StageInvocation.PinnedSwivalProfileContent"/> keyed by
    /// stage number (latest invocation wins for retried stages).
    /// </summary>
    public IReadOnlyDictionary<int, string?> PinnedContentsByStage => _pinnedContentsByStage;

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        // Record the pinned content from the invocation before any edits.
        _pinnedContents.Add(invocation.PinnedSwivalProfileContent);
        _pinnedContentsByStage[invocation.Stage.Number] = invocation.PinnedSwivalProfileContent;

        // Simulate what SwivalSubagentRunner does: prepare the profile with
        // pinned content so the launched swival sees the frozen profile.
        // This also emits swival_profile_divergence events when the tree
        // has diverged from the pinned snapshot.
        if (invocation.PinnedSwivalProfileContent is not null)
        {
            await using var session = await SwivalProfileSession.PrepareWithPinnedContentAsync(
                invocation.TargetRoot, invocation.PinnedSwivalProfileContent,
                invocation.RunId, invocation.TaskName,
                eventSink, cancellationToken);

            // Simulate the task editing swival.toml while swival is running
            // (inside the session lifetime). DisposeAsync will detect the
            // edit and leave it untouched, so it survives to the next stage
            // (which will then see divergence between tree and pinned).
            if (invocation.Stage.Number == editAtStage)
            {
                var tomlPath = Path.Combine(invocation.TargetRoot, "swival.toml");
                await File.WriteAllTextAsync(tomlPath, editContent, cancellationToken);
            }
        }

        return await _inner.RunAsync(invocation, cancellationToken);
    }
}
