using System.Text;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Detects re-added tasks whose prior run completed but the canonical .md
    /// content has since changed. Archives the stale state and resets for a
    /// fresh run when a mismatch is found. Returns true when the task was
    /// classified as re-added (and the state was archived).
    /// Always stamps <see cref="StageStatusEntry.TaskInputHash"/> on every
    /// entry so the hash is persisted for future re-add detection.
    /// </summary>
    private static bool DetectReAddAndArchive(
        string rootPath,
        string taskId,
        string taskDirectory,
        string runId,
        string currentMarkdown,
        string? taskMarkdownPath,
        StringBuilder ledger,
        List<string> manifest,
        List<string> seals,
        ref string previousSeal,
        ref string taskHash,
        ref double sessionCostUsd,
        ref int unknownCostStageCount,
        List<StageStatusEntry> statusEntries,
        ref int firstStageToRun)
    {
        var currentTaskInputHash = Hashing.Sha256Hex(currentMarkdown);
        var priorTaskInputHash = statusEntries
            .Select(e => e.TaskInputHash)
            .FirstOrDefault(h => h is not null);

        var mismatch = priorTaskInputHash is not null
            ? !string.Equals(currentTaskInputHash, priorTaskInputHash, StringComparison.Ordinal)
            : taskMarkdownPath is not null
              && File.Exists(taskMarkdownPath)
              && File.GetLastWriteTimeUtc(taskMarkdownPath)
                 > File.GetLastWriteTimeUtc(Path.Combine(taskDirectory, "status.json"));

        if (mismatch)
        {
            ArchivePriorRunState(rootPath, taskId, taskDirectory, runId,
                ledger, manifest, seals, ref previousSeal, ref taskHash,
                ref sessionCostUsd, ref unknownCostStageCount,
                statusEntries, ref firstStageToRun);
        }

        StampTaskInputHash(statusEntries, currentTaskInputHash);
        return mismatch;
    }

    private static void StampTaskInputHash(List<StageStatusEntry> entries, string hash)
    {
        for (int i = 0; i < entries.Count; i++)
            entries[i] = entries[i] with { TaskInputHash = hash };
    }

    /// <summary>
    /// Ensures every status entry carries the current task input hash.
    /// No-op when the markdown is empty.
    /// </summary>
    private static void EnsureTaskInputHash(List<StageStatusEntry> entries, string markdown)
    {
        if (markdown.Length > 0)
            StampTaskInputHash(entries, Hashing.Sha256Hex(markdown));
    }

    /// <summary>
    /// Archives a prior completed run's state directory when a re-added task is
    /// detected, then resets all in-memory state for a fresh run from stage 1.
    /// The old dir is moved to <c>.relay/&lt;id&gt;.run-&lt;runId&gt;/</c> (with
    /// collision-avoidance suffix if needed) for forensic preservation.
    /// </summary>
    private static void ArchivePriorRunState(
        string rootPath,
        string taskId,
        string taskDirectory,
        string runId,
        StringBuilder ledger,
        List<string> manifest,
        List<string> seals,
        ref string previousSeal,
        ref string taskHash,
        ref double sessionCostUsd,
        ref int unknownCostStageCount,
        List<StageStatusEntry> statusEntries,
        ref int firstStageToRun)
    {
        // Resolve a unique archive name so repeated re-adds within the same
        // runId second never collide.
        var relayDir = Path.Combine(rootPath, ".relay");
        var baseArchivePath = Path.Combine(relayDir, $"{taskId}.run-{runId}");
        var archivePath = baseArchivePath;
        var suffix = 2;
        while (Directory.Exists(archivePath))
        {
            archivePath = $"{baseArchivePath}-{suffix}";
            suffix++;
        }

        Directory.Move(taskDirectory, archivePath);
        Directory.CreateDirectory(taskDirectory);

        // Reset all in-memory state to fresh-run defaults.
        firstStageToRun = 1;
        ledger.Clear();
        manifest.Clear();
        seals.Clear();
        previousSeal = string.Empty;
        taskHash = string.Empty;
        sessionCostUsd = 0d;
        unknownCostStageCount = 0;
        statusEntries.Clear();
        statusEntries.AddRange(SeedStatusEntries());
    }
}
