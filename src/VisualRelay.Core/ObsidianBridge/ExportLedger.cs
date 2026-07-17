using System.Text.Json;

namespace VisualRelay.Core.ObsidianBridge;

public enum SeedDecision { FullBackfill, SealOnly, Skip }

/// <summary>
/// Per-repo export ledger recording every task id whose summary has been
/// exported to the Obsidian vault. Stored at <c>&lt;RepoDir&gt;/.vr-export-ledger.json</c>
/// with atomic temp-file + rename writes so the file is never partial.
/// </summary>
public sealed class ExportLedger
{
    private readonly string _ledgerPath;

    public ExportLedger(string repoDir)
    {
        _ledgerPath = Path.Combine(repoDir, ".vr-export-ledger.json");
    }

    /// <summary>
    /// Returns true when <paramref name="taskId"/> has already been recorded.
    /// A missing or corrupt ledger returns false (safe default).
    /// </summary>
    public async Task<bool> ContainsAsync(string taskId)
    {
        var ids = await LoadIdsAsync();
        return ids.Contains(taskId);
    }

    /// <summary>
    /// Records <paramref name="taskId"/> in the ledger. Idempotent — a
    /// duplicate write is a no-op (the id is already present).
    /// </summary>
    public async Task RecordAsync(string taskId)
    {
        var ids = await LoadIdsAsync();
        if (!ids.Add(taskId)) return;
        await SaveAsync(ids);
    }

    /// <summary>
    /// Batch-records multiple ids in a single atomic write.
    /// An empty collection is a safe no-op.
    /// </summary>
    public async Task RecordBatchAsync(IReadOnlyCollection<string> taskIds)
    {
        if (taskIds.Count == 0) return;
        var ids = await LoadIdsAsync();
        var changed = false;
        foreach (var id in taskIds)
        {
            if (ids.Add(id))
                changed = true;
        }

        if (changed)
            await SaveAsync(ids);
    }

    /// <summary>
    /// First-scan seeding decision.
    /// </summary>
    /// <param name="completedTaskIds">All currently-completed task ids.</param>
    /// <param name="hasExistingNotes">
    /// True when the vault already contains <c>Completed/&lt;date&gt;/</c>
    /// subdirectories from a prior scan (even if the note files were deleted).
    /// </param>
    /// <returns>
    /// <see cref="SeedDecision.FullBackfill"/> — fresh vault; caller should
    /// back-fill every metric-having task and record each in the ledger.
    /// <see cref="SeedDecision.SealOnly"/> — pre-ledger vault; all ids are
    /// already recorded in the ledger (by this call) and the caller must write
    /// nothing.
    /// <see cref="SeedDecision.Skip"/> — a valid ledger already exists; the
    /// caller proceeds with the normal gated top-50 loop.
    /// </returns>
    public async Task<(SeedDecision Decision, IReadOnlyCollection<string> Ids)> TrySeedAsync(
        IReadOnlyCollection<string> completedTaskIds, bool hasExistingNotes)
    {
        var existing = await TryLoadIdsAsync();
        if (existing is not null)
            return (SeedDecision.Skip, Array.Empty<string>());

        // Ledger absent or corrupt.
        if (hasExistingNotes)
        {
            // Pre-ledger vault: seed ledger with all completed ids, write nothing.
            await RecordBatchAsync(completedTaskIds);
            return (SeedDecision.SealOnly, completedTaskIds);
        }

        // Fresh vault: create an empty ledger, then back-fill metric-having tasks.
        await SaveAsync(new HashSet<string>());
        return (SeedDecision.FullBackfill, completedTaskIds);
    }

    // ── persistence ──────────────────────────────────────────────────────

    private async Task<HashSet<string>> LoadIdsAsync()
    {
        return await TryLoadIdsAsync() ?? new HashSet<string>();
    }

    /// <summary>
    /// Loads the id set from disk. Returns null when the ledger is missing
    /// or corrupt (caller treats null as absent/empty).
    /// </summary>
    private async Task<HashSet<string>?> TryLoadIdsAsync()
    {
        if (!File.Exists(_ledgerPath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(_ledgerPath);
            using var doc = JsonDocument.Parse(json);
            var idsElement = doc.RootElement.GetProperty("ids");
            var set = new HashSet<string>();
            foreach (var element in idsElement.EnumerateArray())
                set.Add(element.GetString()!);
            return set;
        }
        catch
        {
            return null; // corrupt → absent
        }
    }

    private async Task SaveAsync(HashSet<string> ids)
    {
        var dir = Path.GetDirectoryName(_ledgerPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(new { ids = ids.ToArray() });
        var tempPath = _ledgerPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, _ledgerPath, overwrite: true);
    }
}
