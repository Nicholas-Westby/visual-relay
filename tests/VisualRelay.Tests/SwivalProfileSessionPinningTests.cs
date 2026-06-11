using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class SwivalProfileSessionPinningTests
{
    // ──────────────────────────────────────────────────────────────
    // SwivalProfileSession.PrepareWithPinnedContentAsync unit tests
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// When a swival.toml already exists in the working tree and pinned content
    /// differs, PrepareWithPinnedContentAsync must overwrite the tree file with
    /// the pinned content so the launched swival process sees the pinned version.
    /// </summary>
    [Fact]
    public async Task PrepareWithPinnedContentAsync_WritesPinnedContent_WhenTreeFileExists()
    {
        using var repo = TestRepository.Create();
        var tomlPath = Path.Combine(repo.Root, "swival.toml");
        const string original = "[profiles.balanced]\nmodel = \"balanced-kimi\"\n";
        await File.WriteAllTextAsync(tomlPath, original);

        const string pinned = "[profiles.balanced]\nmodel = \"balanced\"\n";
        await using var session = await SwivalProfileSession.PrepareWithPinnedContentAsync(
            repo.Root, pinned, "test-run", "test-task", eventSink: null, CancellationToken.None);

        var onDisk = await File.ReadAllTextAsync(tomlPath);
        Assert.Equal(pinned, onDisk);
    }

    /// <summary>
    /// On DisposeAsync, when the task did NOT edit swival.toml during the
    /// session (the file on disk still matches the pinned content), the
    /// session must restore the working tree's original content.
    /// </summary>
    [Fact]
    public async Task PrepareWithPinnedContentAsync_RestoresOriginalContent_OnDispose()
    {
        using var repo = TestRepository.Create();
        var tomlPath = Path.Combine(repo.Root, "swival.toml");
        const string original = "# pre-existing profile\nmodel = \"original\"\n";
        await File.WriteAllTextAsync(tomlPath, original);

        var session = await SwivalProfileSession.PrepareWithPinnedContentAsync(
            repo.Root, "model = \"pinned\"\n", "test-run", "test-task", eventSink: null, CancellationToken.None);
        await session.DisposeAsync();

        var onDisk = await File.ReadAllTextAsync(tomlPath);
        Assert.Equal(original, onDisk);
    }

    /// <summary>
    /// When no swival.toml exists in the working tree, PrepareWithPinnedContentAsync
    /// must write the pinned content (which may be DefaultToml) so swival has a
    /// profile to read.
    /// </summary>
    [Fact]
    public async Task PrepareWithPinnedContentAsync_WritesPinnedContent_WhenNoTreeFile()
    {
        using var repo = TestRepository.Create();
        var tomlPath = Path.Combine(repo.Root, "swival.toml");
        Assert.False(File.Exists(tomlPath));

        var pinnedContent = SwivalProfileSession.DefaultToml;
        await using var session = await SwivalProfileSession.PrepareWithPinnedContentAsync(
            repo.Root, pinnedContent, "test-run", "test-task", eventSink: null, CancellationToken.None);

        Assert.True(File.Exists(tomlPath));
        Assert.Equal(pinnedContent, await File.ReadAllTextAsync(tomlPath));
    }

    /// <summary>
    /// When no original tree file existed (the pinned content was DefaultToml),
    /// and the session did not edit the file, DisposeAsync must delete the
    /// file — matching the existing contract where _created==true leads to
    /// deletion.
    /// </summary>
    [Fact]
    public async Task PrepareWithPinnedContentAsync_DeletesFileOnDispose_WhenNoOriginalTreeFile()
    {
        using var repo = TestRepository.Create();
        var tomlPath = Path.Combine(repo.Root, "swival.toml");

        var session = await SwivalProfileSession.PrepareWithPinnedContentAsync(
            repo.Root, SwivalProfileSession.DefaultToml, "test-run", "test-task", eventSink: null, CancellationToken.None);
        await session.DisposeAsync();

        Assert.False(File.Exists(tomlPath));
    }

    /// <summary>
    /// When pinned content differs from the working tree's current swival.toml,
    /// an info-level "swival_profile_divergence" event must be published so the
    /// operator knows a backend/profile swap is pending at the drive boundary.
    /// The event must carry the run and task identifiers for correlation.
    /// </summary>
    [Fact]
    public async Task PrepareWithPinnedContentAsync_EmitsDivergenceEvent_WhenPinnedDiffersFromTree()
    {
        using var repo = TestRepository.Create();
        var tomlPath = Path.Combine(repo.Root, "swival.toml");
        const string original = "[profiles.balanced]\nmodel = \"balanced-kimi\"\n";
        await File.WriteAllTextAsync(tomlPath, original);

        var sink = new InMemoryRelayEventSink();
        const string pinned = "[profiles.balanced]\nmodel = \"balanced\"\n";
        await using var session = await SwivalProfileSession.PrepareWithPinnedContentAsync(
            repo.Root, pinned, "my-run-id", "my-task-id", sink, CancellationToken.None);

        var divergenceEvent = sink.Events.SingleOrDefault(e => e.EventName == "swival_profile_divergence");
        Assert.NotNull(divergenceEvent);
        Assert.Equal("info", divergenceEvent.Level);
        Assert.Equal("my-run-id", divergenceEvent.RunId);
        Assert.Equal("my-task-id", divergenceEvent.TaskId);
    }

    /// <summary>
    /// When pinned content matches the working tree's swival.toml byte-for-byte,
    /// no divergence event should be emitted (no false alarms).
    /// </summary>
    [Fact]
    public async Task PrepareWithPinnedContentAsync_NoDivergenceEvent_WhenPinnedMatchesTree()
    {
        using var repo = TestRepository.Create();
        var tomlPath = Path.Combine(repo.Root, "swival.toml");
        const string content = "[profiles.cheap]\nmodel = \"cheap\"\n";
        await File.WriteAllTextAsync(tomlPath, content);

        var sink = new InMemoryRelayEventSink();
        await using var session = await SwivalProfileSession.PrepareWithPinnedContentAsync(
            repo.Root, content, "test-run", "test-task", sink, CancellationToken.None);

        Assert.DoesNotContain(sink.Events, e => e.EventName == "swival_profile_divergence");
    }

    /// <summary>
    /// When no tree file exists and DefaultToml is the pinned content, no
    /// divergence event should fire — there is nothing to diverge from.
    /// </summary>
    [Fact]
    public async Task PrepareWithPinnedContentAsync_NoDivergenceEvent_WhenNoTreeFile()
    {
        using var repo = TestRepository.Create();
        var tomlPath = Path.Combine(repo.Root, "swival.toml");
        Assert.False(File.Exists(tomlPath));

        var sink = new InMemoryRelayEventSink();
        await using var session = await SwivalProfileSession.PrepareWithPinnedContentAsync(
            repo.Root, SwivalProfileSession.DefaultToml, "test-run", "test-task", sink, CancellationToken.None);

        Assert.DoesNotContain(sink.Events, e => e.EventName == "swival_profile_divergence");
    }

    /// <summary>
    /// When the tree file exists but is empty, and pinned content is non-empty,
    /// a divergence event must fire — the two are not byte-identical.
    /// </summary>
    [Fact]
    public async Task PrepareWithPinnedContentAsync_EmitsDivergenceEvent_WhenTreeFileIsEmpty()
    {
        using var repo = TestRepository.Create();
        var tomlPath = Path.Combine(repo.Root, "swival.toml");
        await File.WriteAllTextAsync(tomlPath, string.Empty);

        var sink = new InMemoryRelayEventSink();
        await using var session = await SwivalProfileSession.PrepareWithPinnedContentAsync(
            repo.Root, SwivalProfileSession.DefaultToml, "test-run", "test-task", sink, CancellationToken.None);

        var divergenceEvent = sink.Events.SingleOrDefault(e => e.EventName == "swival_profile_divergence");
        Assert.NotNull(divergenceEvent);
        Assert.Equal("info", divergenceEvent.Level);
    }

    /// <summary>
    /// When the task edits swival.toml during the session (the file on disk
    /// differs from the pinned content at dispose), the edit must survive —
    /// the session must NOT restore the original tree content.
    /// </summary>
    [Fact]
    public async Task PrepareWithPinnedContentAsync_PreservesTaskEdit_WhenFileDiffersFromPinnedAtDispose()
    {
        using var repo = TestRepository.Create();
        var tomlPath = Path.Combine(repo.Root, "swival.toml");
        const string original = "[profiles.balanced]\nmodel = \"balanced-kimi\"\n";
        await File.WriteAllTextAsync(tomlPath, original);

        const string pinned = "[profiles.balanced]\nmodel = \"balanced-kimi\"\n";
        const string taskEdit = "[profiles.balanced]\nmodel = \"balanced\"\n";

        // pinned matches original → no divergence event at prepare time.
        var session = await SwivalProfileSession.PrepareWithPinnedContentAsync(
            repo.Root, pinned, "test-run", "test-task", eventSink: null, CancellationToken.None);

        // Simulate swival editing the file during the session.
        await File.WriteAllTextAsync(tomlPath, taskEdit);

        await session.DisposeAsync();

        // DisposeAsync must detect the edit and leave it, NOT restore original.
        var onDisk = await File.ReadAllTextAsync(tomlPath);
        Assert.Equal(taskEdit, onDisk);
    }
}
