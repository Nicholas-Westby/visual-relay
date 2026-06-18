using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Runs the authoritative gate (via <see cref="RunTestCommandWithRetryAsync"/>)
    /// against an ISOLATED, FULL-FIDELITY snapshot of <paramref name="rootPath"/> =
    /// committed HEAD + ALL uncommitted changes (tracked mods AND untracked-not-
    /// ignored). The suite may write freely in the snapshot; the real repo is never
    /// polluted. Returns the gate result and the DELTA — the files the TEST RUN wrote
    /// (captured before vs after the suite ran), NOT the agent's own edits.
    /// If <paramref name="rootPath"/> is not a git repo (or worktree creation fails),
    /// falls back to running against <paramref name="rootPath"/> directly with an
    /// empty delta (preserves today's behavior for non-git test fixtures).
    /// </summary>
    private async Task<(TestRunResult Result, IReadOnlyList<string> Mutations)> RunIsolatedVerifyAsync(
        string rootPath, RelayConfig config, int stageNumber, int attempt,
        string runId, string taskId, CancellationToken cancellationToken)
    {
        string? worktreePath = null;
        var worktreeId = $"{taskId}-verify-s{stageNumber}-a{attempt}";
        try
        {
            worktreePath = await CreateVerifyWorktreeAsync(rootPath, worktreeId, runId, cancellationToken);
        }
        catch
        {
            worktreePath = null; // non-git fixture or transient git failure → no isolation
        }

        if (worktreePath is null)
        {
            var inPlace = await RunTestCommandWithRetryAsync(rootPath, config, cancellationToken, stageNumber, runId, taskId);
            return (inPlace, Array.Empty<string>());
        }

        try
        {
            // Dirty set IMMEDIATELY AFTER the overlay / BEFORE the suite runs.
            var before = await CaptureDirtySetAsync(worktreePath, cancellationToken);
            var result = await RunTestCommandWithRetryAsync(worktreePath, config, cancellationToken, stageNumber, runId, taskId);
            // Dirty set AFTER the suite ran — the DELTA is the suite's writes.
            var after = await CaptureDirtySetAsync(worktreePath, cancellationToken);
            var mutations = after.Where(p => !before.Contains(p))
                                 .OrderBy(p => p, StringComparer.Ordinal)
                                 .ToList();
            return (result, mutations);
        }
        finally
        {
            await CleanupVerifyWorktreeAsync(rootPath, worktreePath, cancellationToken);
        }
    }

    /// <summary>
    /// Creates a detached HEAD worktree (reusing <see cref="PlanningWorktree.CreateAsync"/>)
    /// then OVERLAYS the full uncommitted state of <paramref name="sourcePath"/> onto it:
    /// every tracked-modified and untracked-not-ignored file is copied across, so the
    /// snapshot mirrors exactly what the agent produced (Defect C). Throws if
    /// <paramref name="sourcePath"/> is not a git repo (caller catches → fallback).
    /// </summary>
    private async Task<string> CreateVerifyWorktreeAsync(
        string sourcePath, string worktreeId, string runId, CancellationToken cancellationToken)
    {
        var worktreePath = await PlanningWorktree.CreateAsync(
            sourcePath, worktreeId, runId, cancellationToken, _dependencies.GitInvoker);

        foreach (var relative in await EnumerateUncommittedAsync(sourcePath, cancellationToken))
        {
            var src = Path.Combine(sourcePath, relative);
            // Known limitation: a tracked file the agent DELETED in the working tree is
            // reported by `git diff --name-only` but no longer exists on disk, so it is
            // skipped here and the snapshot retains its HEAD copy. R0a concerns added/
            // modified files outside the manifest (covered by the overlay), not deletions.
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(worktreePath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }
        return worktreePath;
    }

    /// <summary>Tracked-modified + untracked-not-ignored repo-relative paths (NUL-safe).</summary>
    private async Task<IReadOnlyList<string>> EnumerateUncommittedAsync(string rootPath, CancellationToken cancellationToken)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var diff = await _dependencies.GitInvoker.RunAsync(rootPath, new[] { "diff", "--name-only", "-z" }, cancellationToken);
        foreach (var p in SplitNul(diff.Output)) set.Add(p);
        var untracked = await _dependencies.GitInvoker.RunAsync(rootPath, new[] { "ls-files", "--others", "--exclude-standard", "-z" }, cancellationToken);
        foreach (var p in SplitNul(untracked.Output)) set.Add(p);
        return set.ToList();
    }

    /// <summary>The worktree's current dirty set (tracked mods + untracked), NUL-safe.</summary>
    private async Task<HashSet<string>> CaptureDirtySetAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var diff = await _dependencies.GitInvoker.RunAsync(worktreePath, new[] { "diff", "--name-only", "-z" }, cancellationToken);
        foreach (var p in SplitNul(diff.Output)) set.Add(p);
        var untracked = await _dependencies.GitInvoker.RunAsync(worktreePath, new[] { "ls-files", "--others", "--exclude-standard", "-z" }, cancellationToken);
        foreach (var p in SplitNul(untracked.Output)) set.Add(p);
        return set;
    }

    private static IEnumerable<string> SplitNul(string? gitOutput) =>
        (gitOutput ?? string.Empty).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Best-effort teardown: git worktree remove, then a resilient dir delete.</summary>
    private async Task CleanupVerifyWorktreeAsync(string sourcePath, string worktreePath, CancellationToken cancellationToken)
    {
        await PlanningWorktree.RemoveAsync(sourcePath, worktreePath, cancellationToken, _dependencies.GitInvoker);
        try { if (Directory.Exists(worktreePath)) Directory.Delete(worktreePath, recursive: true); }
        catch { /* PRODUCTION fallback — never reference TestFileSystem here (Defect E). */ }
    }

    /// <summary>
    /// Emits a <c>verify_mutated_tree</c> warn advisory naming the DELTA files the test
    /// command wrote during verify. Emitted only when the delta is non-empty; the real
    /// <paramref name="rootPath"/> tree is always unaffected (the gate ran in the snapshot).
    /// </summary>
    // NOTE: isolation covers the authoritative test gates (stages 9/10) only — the bootstrap
    // check and commit gate still run in-place — so "the repo is unaffected" in the advisory
    // refers specifically to the test command's writes during those isolated gate runs.
    private async Task EmitMutatedTreeAdvisoryAsync(
        string rootPath, string runId, string taskId, RelayStageDefinition stage,
        IReadOnlyList<string> mutations, CancellationToken cancellationToken)
    {
        if (mutations.Count == 0) return;
        await _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "warn", "verify_mutated_tree", runId, rootPath, taskId,
            stage.Number, stage.Tier,
            Data: new Dictionary<string, string>
            {
                ["files"] = string.Join(' ', mutations),
                ["advice"] = "the test command wrote these files during verify; VR ran the gate in an "
                           + "isolated tree so the repo is unaffected — gitignore them or use a non-writing "
                           + "test command for idempotent verification"
            }), cancellationToken);
    }
}
