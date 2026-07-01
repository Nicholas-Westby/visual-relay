using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Completion gate (stage 11, before retirement): a task that was expected to
    /// produce code must not be retired to <c>DONE-</c> / committed if it left the
    /// tracked tree unchanged apart from proof and spec bookkeeping — the
    /// phantom-completion hole where a run staged only <c>.relay</c> proof plus the
    /// spec rename and reported success.
    /// Returns a Flagged outcome to short-circuit the commit stage, or <c>null</c>
    /// to let it proceed.
    /// </summary>
    private async Task<RelayTaskOutcome?> CheckCodeProducedAsync(
        string rootPath,
        string runId,
        string taskId,
        string taskDirectory,
        RelayConfig config,
        IReadOnlyList<string> manifest,
        string taskMarkdown,
        string? runBaseSha,
        List<StageStatusEntry> statusEntries,
        CancellationToken cancellationToken)
    {
        // No run-base to diff against (empty repo / HEAD unresolved) → cannot judge;
        // leave the pre-gate behaviour unchanged.
        if (string.IsNullOrEmpty(runBaseSha))
            return null;

        // "Expected to produce code": an implementation (non-test) file in the
        // stage-4 manifest, OR a Deliverables / Done-when checklist in the spec.
        // Neither → a legitimately non-code (observational) task; skip the gate so
        // read-only runs still complete.
        var expectsCode =
            manifest.Select(p => p.StartsWith('+') ? p[1..] : p)
                    .Any(f => IsImpl(f) && !IsTestFile(f))
            || PlanCompletenessGate.HasChecklist(taskMarkdown);
        if (!expectsCode)
            return null;

        if (await HasCodeChangeSinceAsync(rootPath, runBaseSha, config.TasksDir, cancellationToken))
            return null;

        return await FlagAsync(rootPath, runId, taskId, taskDirectory, 11,
            "task expected to modify source or tests but produced no changes",
            null, statusEntries, cancellationToken);
    }

    /// <summary>
    /// True when at least one non-bookkeeping path changed since
    /// <paramref name="runBaseSha"/>. Unions three sources: the working-tree diff
    /// vs the run-base (captures mid-run edits and self-commits reflected on disk),
    /// the committed delta run-base..HEAD (catches a file the agent self-committed
    /// then deleted from the working tree, which the commit stage still seals), and
    /// new untracked files. Excludes the VR-internal artifact dirs, the task/spec
    /// dir, and the top-level <c>VERSION</c> stamp, so proof + spec rename alone
    /// never satisfy the gate. Fails open (returns true) on any git error — the gate
    /// never blocks completion on an unclear tree.
    /// </summary>
    private async Task<bool> HasCodeChangeSinceAsync(
        string rootPath, string runBaseSha, string tasksDir, CancellationToken cancellationToken)
    {
        var gi = _dependencies.GitInvoker;
        var wtDiff = await GitLinesAsync(gi, rootPath, ["diff", "--name-only", runBaseSha], cancellationToken);
        var committed = await GitLinesAsync(gi, rootPath, ["diff", "--name-only", runBaseSha, "HEAD"], cancellationToken);
        var untracked = await GitLinesAsync(gi, rootPath, ["ls-files", "--others", "--exclude-standard"], cancellationToken);
        if (wtDiff is null || committed is null || untracked is null)
            return true; // fail open: never block completion on a git error

        foreach (var path in wtDiff.Concat(committed).Concat(untracked))
        {
            if (!IsBookkeepingPath(rootPath, path, tasksDir))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Runs a git command with non-ASCII paths kept literal (<c>core.quotePath=false</c>,
    /// so a quoted bookkeeping path can't dodge <see cref="IsBookkeepingPath"/> and its
    /// embedded quotes can't make Path.GetFullPath throw on Windows) and returns the
    /// output split into non-empty lines, or <c>null</c> on any non-zero exit.
    /// </summary>
    private async Task<IReadOnlyList<string>?> GitLinesAsync(
        IGitInvoker gi, string rootPath, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await gi.RunAsync(
            rootPath, ["-c", "core.quotePath=false", .. args], cancellationToken);
        return result.ExitCode != 0
            ? null
            : result.Output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
    }

    // VR-internal artifact dirs whose churn is pure bookkeeping and is never
    // auto-committed — the same set the commit stage excludes (GitCommitter /
    // WorktreeFilter / WorktreeResetter). Matching it keeps the gate aligned with
    // what actually lands in a commit on repos that don't gitignore these dirs.
    private static readonly string[] BookkeepingPrefixes = [".relay/", ".relay-scratch/", ".swival/"];

    // A path that a run may touch without producing code: VR-internal artifacts,
    // the task/spec dir, or the top-level VERSION stamp.
    private static bool IsBookkeepingPath(string rootPath, string path, string tasksDir)
    {
        var normalized = path.Replace('\\', '/').Trim();
        if (normalized.Length == 0 || string.Equals(normalized, "VERSION", StringComparison.Ordinal))
            return true;
        foreach (var prefix in BookkeepingPrefixes)
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        return IsPathUnderDirectory(rootPath, path, tasksDir);
    }
}
