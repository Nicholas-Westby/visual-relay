using VisualRelay.Core.Logging;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Pins a frozen swival.toml at a target root for the lifetime of a subagent
/// launch, restoring the working tree's original content on dispose.
///
/// Concurrency: the review pair (stage 7) launches review, triage and
/// visual-review invocations that each pin the SAME root's swival.toml at once.
/// To keep the shared file coherent, pinned sessions for one (root, content) are
/// REF-COUNTED through a process-wide registry (see the PinnedRegistry partial):
/// only the first prepare writes the pin and captures the original, and only the
/// last dispose restores it — so concurrent siblings never tear each other's read
/// nor double-restore. The restore drains under a handoff barrier so a following
/// pin epoch captures the restored tree, not a peer's in-flight pin. All writes
/// are atomic (temp file + rename) so an external reader (swival) never observes
/// a 0-byte truncate window under any residual concurrency.
/// </summary>
internal sealed partial class SwivalProfileSession : IAsyncDisposable
{
    internal const string FileName = "swival.toml";
    private readonly string _path;
    private readonly bool _created;
    private readonly bool _pinnedMode;
    private readonly PinnedRoot? _pinnedRoot;
    private int _disposed;

    private SwivalProfileSession(string path, bool created)
    {
        _path = path;
        _created = created;
        _pinnedMode = false;
    }

    private SwivalProfileSession(string path, PinnedRoot pinnedRoot)
    {
        _path = path;
        _pinnedRoot = pinnedRoot;
        _pinnedMode = true;
    }

    public static async Task<SwivalProfileSession> PrepareAsync(
        string rootPath,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(rootPath, FileName);
        if (File.Exists(path))
        {
            return new SwivalProfileSession(path, created: false);
        }

        await AtomicWriteAsync(path, DefaultToml, cancellationToken);
        return new SwivalProfileSession(path, created: true);
    }

    /// <summary>
    /// Prepares a swival profile session with pinned content, saving the
    /// working tree's original swival.toml (if any) and overwriting it with
    /// <paramref name="pinnedContent"/> so the launched swival process sees
    /// the frozen profile. On <see cref="DisposeAsync"/>, if the file on disk
    /// still matches <paramref name="pinnedContent"/> (i.e. the session did
    /// not edit it), the original tree content is restored (or the file
    /// deleted if none existed). If the file differs — because the task
    /// edited it — the edit is left untouched so it survives to commit.
    /// When <paramref name="eventSink"/> is non-null and pinned content differs
    /// from the tree's current content, an info-level
    /// "swival_profile_divergence" event is emitted.
    ///
    /// Concurrent siblings pinning the same content on the same root share one
    /// ref-counted pin: only the first prepare reads/writes/diverges, the rest
    /// wait for that write and share it; only the last dispose restores.
    /// </summary>
    public static async Task<SwivalProfileSession> PrepareWithPinnedContentAsync(
        string rootPath,
        string pinnedContent,
        string runId,
        string taskId,
        IRelayEventSink? eventSink,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(rootPath, FileName);
        var (root, isFirst) = await AcquirePinnedRootAsync(path, pinnedContent, cancellationToken);

        if (!isFirst)
        {
            // A sibling already opened this pin epoch. Wait until its write
            // completes, then share the pin without re-reading or re-writing.
            try
            {
                await root.Ready.Task.WaitAsync(cancellationToken);
            }
            catch
            {
                await ReleasePinnedRootAsync(path, root);
                throw;
            }

            return new SwivalProfileSession(path, root);
        }

        // First prepare of this epoch: capture the original tree content, emit
        // any divergence, and write the pin — exactly once for all siblings.
        try
        {
            string? originalContent = File.Exists(path)
                ? await File.ReadAllTextAsync(path, cancellationToken)
                : null;
            root.OriginalContent = originalContent;

            if (eventSink is not null && originalContent is not null
                && !string.Equals(originalContent, pinnedContent, StringComparison.Ordinal))
            {
                await PublishDivergenceAsync(eventSink, rootPath, runId, taskId, cancellationToken);
            }

            await AtomicWriteAsync(path, pinnedContent, cancellationToken);
            root.Ready.TrySetResult();
        }
        catch (Exception ex)
        {
            // The pin was never established: fault waiters and drain the epoch so
            // a later prepare starts fresh instead of attaching to a dead pin.
            root.Ready.TrySetException(ex);
            await ReleasePinnedRootAsync(path, root);
            throw;
        }

        return new SwivalProfileSession(path, root);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_pinnedMode)
        {
            await ReleasePinnedRootAsync(_path, _pinnedRoot!);
        }
        else if (_created)
        {
            File.Delete(_path);
        }
    }
}
