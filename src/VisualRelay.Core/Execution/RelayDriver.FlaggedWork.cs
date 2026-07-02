using System.Text;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// On resume, restores the flagged working tree from a flagged-work bundle
    /// before re-entering the flagged stage. Only for mid-pipeline stages (5–10).
    /// Returns the (possibly adjusted) firstStageToRun and a non-null flagged
    /// outcome when the restore fails unresolvably.
    /// </summary>
    private async Task<(int firstStageToRun, RelayTaskOutcome? flaggedOutcome)> RestoreFlaggedWorkIfNeededAsync(
        string rootPath,
        string taskId,
        string taskDirectory,
        int firstStageToRun,
        StringBuilder ledger,
        List<StageStatusEntry> statusEntries,
        CancellationToken ct)
    {
        if (!_options.Resume || firstStageToRun is < 5 or > 10)
            return (firstStageToRun, null);

        // Only attempt restore when a bundle exists — resume proceeds normally
        // for older tasks that flagged before snapshots were introduced.
        var bundlePath = Path.Combine(taskDirectory, FlaggedWorkStore.BundleFileName);
        if (!File.Exists(bundlePath))
            return (firstStageToRun, null);

        var restoreResult = await FlaggedWorkStore.RestoreAsync(
            rootPath, taskId, taskDirectory, _dependencies.GitInvoker, ct);

        if (restoreResult.IsUnrestorable)
        {
            FlaggedWorkStore.Delete(taskDirectory);
            ledger.AppendLine("> **Resume**: flagged-work bundle not restorable — starting fresh from stage 1.");
            ledger.AppendLine();
            return (1, null);
        }

        if (restoreResult.HasConflicts)
        {
            return await ResolveConflictsAsync(
                rootPath, taskId, taskDirectory, firstStageToRun,
                restoreResult.ConflictedFiles, ledger, statusEntries, ct);
        }

        // Clean apply — proceed to the flagged stage.
        return (firstStageToRun, null);
    }

    private async Task<(int firstStageToRun, RelayTaskOutcome? flaggedOutcome)> ResolveConflictsAsync(
        string rootPath,
        string taskId,
        string taskDirectory,
        int firstStageToRun,
        IReadOnlyList<string> conflictedFiles,
        StringBuilder ledger,
        List<StageStatusEntry> statusEntries,
        CancellationToken ct)
    {
        const int maxAttempts = 3;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var conflictList = string.Join("\n", conflictedFiles.Select(f => $"- `{f}`"));
            var systemPrompt = $$"""
You are resolving merge conflicts from resuming a flagged task. Conflict markers
(<<<<<<< / ======= / >>>>>>>) are present in the listed files. The task is being
re-applied onto a newer HEAD, and the conflicts come from overlapping upstream changes.

Resolve every conflict in every listed file so the intended feature is preserved
and adapted to the upstream changes. Do NOT commit; just edit the files in place.

Conflicted files:
{{conflictList}}

Task ledger (for context on the intended changes):
{{ledger}}
""";

            var invocation = new StageInvocation(
                Stage: new RelayStageDefinition(firstStageToRun, "Resolve conflicts", "cheap", "agent", "", "",
                    systemPrompt, "{}"),
                Tier: "cheap",
                RunId: $"resume-{taskId}",
                TargetRoot: rootPath,
                TaskName: taskId,
                TaskInput: $"Resolve conflicts for task {taskId}",
                LedgerSoFar: ledger.ToString(),
                Manifest: conflictedFiles,
                LogSources: [],
                TraceDirectory: Path.Combine(taskDirectory, $"resolve-conflicts-attempt{attempt}"),
                ReportFile: Path.Combine(taskDirectory, $"resolve-conflicts-attempt{attempt}.report.json"),
                MaxTurns: 10);

            await _dependencies.SubagentRunner.RunAsync(invocation, ct);

            // Stage resolved files: after git cherry-pick --quit the index
            // retains unmerged entries; ls-files -u reads the index, so the
            // agent's working-tree edits must be staged first.
            await _dependencies.GitInvoker.RunAsync(rootPath, ["add", "-A"], ct);

            // Check for remaining conflicts.
            var unmergedResult = await _dependencies.GitInvoker.RunAsync(
                rootPath, ["ls-files", "-u"], ct);
            var hasConflicts = unmergedResult.ExitCode == 0
                && !string.IsNullOrWhiteSpace(unmergedResult.Output);

            if (!hasConflicts)
            {
                ledger.AppendLine($"> **Resume**: conflicts resolved (attempt {attempt}).");
                ledger.AppendLine();
                return (firstStageToRun, null);
            }

            ledger.AppendLine($"> **Resume**: conflicts remain after resolution attempt {attempt}.");
            ledger.AppendLine();
        }

        // Exhausted budget — flag.
        var outcome = await FlagAsync(rootPath, $"resume-{taskId}", taskId, taskDirectory,
            firstStageToRun, "resume conflict unresolved",
            $"Conflicts remain in {conflictedFiles.Count} file(s) after {maxAttempts} resolution attempts: {string.Join(", ", conflictedFiles)}",
            statusEntries, ct);
        return (0, outcome);
    }
}
