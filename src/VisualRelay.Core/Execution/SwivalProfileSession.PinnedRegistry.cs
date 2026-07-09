using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

internal sealed partial class SwivalProfileSession
{
    // Process-wide ref-counted pins keyed by (absolute swival.toml path, pinned
    // content). Concurrent siblings pinning the same content on the same root
    // share ONE PinnedRoot: only the first prepare writes and captures the
    // original, only the last dispose restores. All access under RegistryGate.
    private static readonly Dictionary<(string Path, string Content), PinnedRoot> PinnedRoots = new();
    private static readonly object RegistryGate = new();

    private sealed class PinnedRoot(string pinnedContent)
    {
        public string PinnedContent { get; } = pinnedContent;
        public int RefCount;
        public string? OriginalContent;
        public bool Draining;

        // Completes when the first prepare has written the pin; siblings await it
        // so none launches swival before the frozen profile is on disk. Written
        // before completion, so any awaiter observes OriginalContent.
        public TaskCompletionSource Ready { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Completes when the last dispose has finished restoring and removed the
        // entry; an acquirer that finds a draining epoch awaits this then retries,
        // so the next epoch captures the restored tree rather than the pin.
        public TaskCompletionSource Drained { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Joins (or opens) the pin epoch for (path, content). Returns the shared state
    // and whether this caller is the first (and so owns the pin write). If an
    // epoch is mid-drain, waits for it to finish so the fresh epoch reads the
    // restored tree instead of the departing epoch's in-flight pin.
    private static async Task<(PinnedRoot Root, bool IsFirst)> AcquirePinnedRootAsync(
        string path, string pinnedContent, CancellationToken cancellationToken)
    {
        var key = (path, pinnedContent);
        while (true)
        {
            Task drainWait;
            lock (RegistryGate)
            {
                if (PinnedRoots.TryGetValue(key, out var existing))
                {
                    if (!existing.Draining)
                    {
                        existing.RefCount++;
                        return (existing, false);
                    }

                    drainWait = existing.Drained.Task;
                }
                else
                {
                    var root = new PinnedRoot(pinnedContent) { RefCount = 1 };
                    PinnedRoots[key] = root;
                    return (root, true);
                }
            }

            await drainWait.WaitAsync(cancellationToken);
        }
    }

    // Drops one hold on the epoch. The last holder drains it: restores the
    // original tree content (when the file still matches the pin), removes the
    // entry, then signals waiters. Also used by failed prepares — the restore
    // self-guards on the pin still being present, so a never-written pin restores
    // nothing.
    private static async Task ReleasePinnedRootAsync(string path, PinnedRoot root)
    {
        lock (RegistryGate)
        {
            if (--root.RefCount > 0)
                return;
            if (root.Draining)
                return;
            root.Draining = true;
        }

        try
        {
            string? currentContent = File.Exists(path)
                ? await File.ReadAllTextAsync(path)
                : null;

            if (currentContent is not null
                && string.Equals(currentContent, root.PinnedContent, StringComparison.Ordinal))
            {
                if (root.OriginalContent is not null)
                    await AtomicWriteAsync(path, root.OriginalContent, CancellationToken.None);
                else
                    File.Delete(path);
            }
            // else: file was edited during the session (or never pinned) — leave it.
        }
        finally
        {
            lock (RegistryGate)
            {
                if (PinnedRoots.TryGetValue((path, root.PinnedContent), out var current)
                    && ReferenceEquals(current, root))
                {
                    PinnedRoots.Remove((path, root.PinnedContent));
                }
            }

            root.Drained.TrySetResult();
        }
    }

    // Atomic write: stage into a sibling temp file then rename over the target so
    // no reader ever observes a truncated (0-byte) or partially written file.
    private static async Task AtomicWriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, content, cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // Best-effort cleanup — surface the original write failure.
            }

            throw;
        }
    }

    private static async Task PublishDivergenceAsync(
        IRelayEventSink eventSink, string rootPath, string runId, string taskId,
        CancellationToken cancellationToken)
    {
        await eventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow,
            "info",
            "swival_profile_divergence",
            RunId: runId,
            RootPath: rootPath,
            TaskId: taskId,
            Data: new Dictionary<string, string>
            {
                ["reason"] = "pinned swival profile differs from working-tree swival.toml — "
                           + "the run will use the pinned (frozen) content; "
                           + "a backend/profile swap is pending at the drive boundary"
            }), cancellationToken);
    }
}
