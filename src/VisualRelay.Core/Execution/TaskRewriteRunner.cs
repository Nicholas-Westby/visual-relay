using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Records the outcome of <see cref="TaskRewriteRunner.RunAsync"/>.
/// </summary>
public sealed record RewriteOutcome(bool Changed, string? Error);

/// <summary>
/// Orchestrates one isolated, sandboxed rewrite of a task spec using the
/// frontier model in a git worktree. On success, only the task's own folder
/// is copied back into the main tree; stray writes are discarded.
/// On cancellation or error, nothing is copied back and the worktree is
/// removed.
/// </summary>
public static class TaskRewriteRunner
{
    /// <summary>
    /// Runs a sandboxed, frontier-model rewrite of <paramref name="task"/>'s spec
    /// inside an ephemeral git worktree. Returns <see cref="RewriteOutcome"/>
    /// indicating whether the spec on disk actually changed.
    /// </summary>
    public static async Task<RewriteOutcome> RunAsync(
        string rootPath,
        RelayTaskItem task,
        RelayConfig config,
        ISubagentRunner runner,
        CancellationToken ct,
        IGitInvoker? git = null,
        IEnvironmentAccessor? environment = null)
    {
        var runId = "rewrite-" + DateTimeOffset.UtcNow.Ticks;
        var taskDir = task.TaskDirectory;
        var specPath = task.MarkdownPath;

        // The repo-relative path to the task's spec file.
        var specRepoRelativePath = Path.GetRelativePath(rootPath, specPath);

        // The repo-relative path to the task's folder (used for copy-back).
        var taskFolderRelativePath = Path.GetRelativePath(rootPath, taskDir);

        string worktreePath;
        string originalSpec;
        try
        {
            // Self-heal the VR-owned nono profile before launching the sandboxed
            // rewrite, mirroring RelayDriver.RunTaskAsync. The sandbox is always
            // on; on a fresh machine the profile does not exist yet, so without
            // this the nono --profile invocation inside the runner would fail. A
            // write failure throws — the run must not proceed unsandboxed/stale.
            await NonoProfileEnsurer.EnsureAsync(environment, ct);

            // Read the current spec before any worktree operations.
            originalSpec = await File.ReadAllTextAsync(specPath, ct);

            // Reclaim this task's leftover rewrite worktrees from a prior crash
            // (the finally-RemoveAsync is skipped on a hard kill). Scoped to this
            // task id so a concurrent rewrite of a DIFFERENT task — which shares
            // the rewrite namespace — is never deleted.
            await PlanningWorktree.PruneTaskLeftoversAsync(rootPath, task.Id, ct, git);

            worktreePath = await PlanningWorktree.CreateAsync(rootPath, task.Id, runId, ct, git, isRewrite: true);
            PlanningWorktree.CopyConfigIntoWorktree(rootPath, worktreePath);
        }
        catch (OperationCanceledException)
        {
            return new RewriteOutcome(false, "Cancelled before worktree creation.");
        }

        try
        {
            // Seed the latest spec (and folder contents) into the worktree so
            // the model rewrites current content, not stale HEAD.
            CopyTaskFolderIntoWorktree(rootPath, worktreePath, taskDir);

            var traceDir = Path.Combine(worktreePath, ".relay", task.Id, "rewrite");
            Directory.CreateDirectory(traceDir);
            var reportFile = Path.Combine(traceDir, "rewrite.log");

            var stageDef = new RelayStageDefinition(
                Number: 0,
                Name: "Rewrite",
                Tier: "frontier",
                Kind: "llm",
                Files: "all",
                Commands: "all",
                SystemPrompt: RewriteGuidance.SystemPrompt,
                OutputContract: """{ "summary": string }""");

            var taskInput = RewriteGuidance.BuildInput(originalSpec, specRepoRelativePath);

            var invocation = new StageInvocation(
                Stage: stageDef,
                Tier: "frontier",
                RunId: runId,
                TargetRoot: worktreePath,
                TaskName: task.Id,
                TaskInput: taskInput,
                LedgerSoFar: string.Empty,
                Manifest: [specRepoRelativePath],
                LogSources: [],
                TraceDirectory: traceDir,
                ReportFile: reportFile,
                MaxTurns: config.MaxTurns,
                AbsoluteCeilingMs: config.SubagentTimeoutMilliseconds);

            SubagentResult result;
            try
            {
                result = await runner.RunAsync(invocation, ct);
            }
            catch (OperationCanceledException)
            {
                return new RewriteOutcome(false, "Cancelled during rewrite.");
            }

            if (!result.IsValid)
            {
                // The worktree (and its .relay diagnostics) is deleted by the finally
                // below, which would orphan the (full output: …) breadcrumb in the
                // error. Preserve the rewrite diagnostics under the task's own .relay
                // in the main tree and repoint the breadcrumb so it still resolves.
                var error = PreserveDiagnosticsAndRepoint(
                    rootPath, worktreePath, task.Id,
                    result.Error ?? "Rewrite failed: model returned invalid result.");
                return new RewriteOutcome(false, error);
            }

            // On success: copy only the task folder back to the main tree.
            CopyTaskFolderIntoMainTree(rootPath, worktreePath, taskFolderRelativePath);

            // Re-read the spec to determine whether it actually changed.
            var newSpec = await File.ReadAllTextAsync(specPath, ct);
            var changed = !string.Equals(originalSpec, newSpec, StringComparison.Ordinal);

            return new RewriteOutcome(changed, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RewriteOutcome(false, ex.Message);
        }
        finally
        {
            await PlanningWorktree.RemoveAsync(rootPath, worktreePath, CancellationToken.None, git);
        }
    }

    /// <summary>
    /// Copies the task's folder from the main repo tree into the matching
    /// location in the worktree so the model sees current (possibly dirty) content.
    /// </summary>
    private static void CopyTaskFolderIntoWorktree(string rootPath, string worktreePath, string taskDir)
    {
        var taskRelative = Path.GetRelativePath(rootPath, taskDir);
        var dest = Path.Combine(worktreePath, taskRelative);
        CopyDirectoryRecursive(taskDir, dest);
    }

    /// <summary>
    /// Copies the task's folder from the worktree back into the main tree.
    /// Only the task folder — nothing else.
    /// </summary>
    private static void CopyTaskFolderIntoMainTree(string rootPath, string worktreePath, string taskFolderRelativePath)
    {
        var src = Path.Combine(worktreePath, taskFolderRelativePath);
        if (!Directory.Exists(src))
            return;

        var dest = Path.Combine(rootPath, taskFolderRelativePath);
        // Clean the destination first so deleted files are reflected.
        if (Directory.Exists(dest))
            Directory.Delete(dest, recursive: true);

        CopyDirectoryRecursive(src, dest);
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectoryRecursive(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }

    /// <summary>
    /// Copies the rewrite's diagnostics (the runner writes them under
    /// <c>{worktree}/.relay/{taskId}</c>: <c>rewrite/rewrite.log</c> and the
    /// <c>stage0-attempt*.killed-output.txt</c> the breadcrumb points at) into the
    /// matching <c>.relay/{taskId}</c> in the MAIN tree, then repoints any
    /// <c>(full output: …)</c> breadcrumb in <paramref name="error"/> from the
    /// (about-to-be-deleted) worktree to the preserved location. Best-effort: a copy
    /// failure must never mask the real error, so the original text is returned
    /// unchanged. Rewrite is stage 0, so these names never collide with a real run's
    /// stage 1-11 artifacts under the same folder.
    /// </summary>
    private static string PreserveDiagnosticsAndRepoint(
        string rootPath, string worktreePath, string taskId, string error)
    {
        try
        {
            var src = Path.Combine(worktreePath, ".relay", taskId);
            if (!Directory.Exists(src))
                return error;

            var dest = Path.Combine(rootPath, ".relay", taskId);
            CopyDirectoryRecursive(src, dest);

            // Repoint the breadcrumb: the runner baked the worktree path into the
            // error; the preserved copy lives at the same relative path under root.
            return error.Replace(src, dest, StringComparison.Ordinal);
        }
        catch
        {
            return error;
        }
    }
}
