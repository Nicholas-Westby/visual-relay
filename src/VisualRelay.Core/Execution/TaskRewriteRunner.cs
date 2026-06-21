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
        IGitInvoker? git = null)
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
            // Read the current spec before any worktree operations.
            originalSpec = await File.ReadAllTextAsync(specPath, ct);

            worktreePath = await PlanningWorktree.CreateAsync(rootPath, task.Id, runId, ct, git);
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
                MaxTurns: config.MaxTurns);

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
                return new RewriteOutcome(false, result.Error ?? "Rewrite failed: model returned invalid result.");
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
}
