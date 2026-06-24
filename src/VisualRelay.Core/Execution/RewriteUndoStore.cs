namespace VisualRelay.Core.Execution;

/// <summary>
/// Snapshots a task's WHOLE folder before a "Rewrite with AI" copy-back so a
/// later "Revert" can restore the entire folder, not just the spec .md.
///
/// <para>The rewrite copy-back recreates the whole task folder, so the model can
/// add, modify, or delete attachment files. Restoring only the spec string would
/// be lossy for non-spec folder contents. This store captures the pre-rewrite
/// folder to a private temp directory (keyed by task id) and, on revert, deletes
/// the live folder and recopies the snapshot — making revert exact.</para>
///
/// <para>Snapshots are deleted whenever an undo is discarded (revert, a fresh
/// edit, a run start, or a re-capture) so they never leak.</para>
/// </summary>
public sealed class RewriteUndoStore
{
    private readonly Dictionary<string, string> _snapshots = new(StringComparer.Ordinal);

    /// <summary>Whether an undo snapshot exists for <paramref name="taskId"/>.</summary>
    public bool Has(string taskId) => _snapshots.ContainsKey(taskId);

    /// <summary>
    /// Snapshots the whole <paramref name="taskDirectory"/> to a private temp dir.
    /// Re-capturing replaces (and deletes) any prior snapshot for the same id.
    /// </summary>
    public void Capture(string taskId, string taskDirectory)
    {
        Discard(taskId);

        var snapshot = Path.Combine(
            Path.GetTempPath(), "visual-relay", "rewrite-undo", Guid.NewGuid().ToString("N"));
        CopyDirectoryRecursive(taskDirectory, snapshot);
        _snapshots[taskId] = snapshot;
    }

    /// <summary>
    /// Restores the whole task folder to its captured pre-rewrite state: the live
    /// <paramref name="taskDirectory"/> is deleted and recopied from the snapshot,
    /// so files the rewrite added/modified/deleted are all reverted. No-op when no
    /// snapshot exists. The snapshot is discarded afterwards.
    /// </summary>
    public async Task RestoreAsync(string taskId, string taskDirectory)
    {
        if (!_snapshots.TryGetValue(taskId, out var snapshot) || !Directory.Exists(snapshot))
        {
            Discard(taskId);
            return;
        }

        if (Directory.Exists(taskDirectory))
            Directory.Delete(taskDirectory, recursive: true);
        CopyDirectoryRecursive(snapshot, taskDirectory);

        // Yield so this stays awaitable/cancellable-friendly alongside callers
        // that await it; the copy itself is synchronous file I/O.
        await Task.CompletedTask;
        Discard(taskId);
    }

    /// <summary>Drops the undo for <paramref name="taskId"/> and deletes its snapshot.</summary>
    public void Discard(string taskId)
    {
        if (!_snapshots.Remove(taskId, out var snapshot))
            return;
        TryDeleteDirectory(snapshot);
    }

    /// <summary>
    /// Migrates the undo snapshot from <paramref name="oldId"/> to
    /// <paramref name="newId"/> when a task is renamed. No-op when no
    /// snapshot exists for <paramref name="oldId"/>.
    /// </summary>
    public void Rekey(string oldId, string newId)
    {
        if (_snapshots.Remove(oldId, out var snapshot))
        {
            _snapshots[newId] = snapshot;
        }
    }

    /// <summary>Discards every snapshot (e.g. on view-model teardown).</summary>
    public void DiscardAll()
    {
        foreach (var snapshot in _snapshots.Values)
            TryDeleteDirectory(snapshot);
        _snapshots.Clear();
    }

    /// <summary>Test-only accessor for the snapshot directory of a task id.</summary>
    internal string? SnapshotPathForTests(string taskId) =>
        _snapshots.GetValueOrDefault(taskId);

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort: a leaked temp snapshot beats throwing from cleanup.
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectoryRecursive(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }
}
