using VisualRelay.Core.Tasks;

namespace VisualRelay.Core.Execution;

/// <summary>What a reset did, so the operator is told rather than left guessing.</summary>
/// <param name="TreeReset">Whether the working tree was put back to the run base.</param>
/// <param name="Removed">The run's untracked files that were deleted.</param>
/// <param name="SnapshotMissing">True when no pre-run snapshot existed, so untracked
/// files were kept rather than deleted on an unknown baseline.</param>
/// <param name="Failure">What git said when the reset could not run, else null. The
/// archive still happens: a git failure is not a reason to leave the task flagged.</param>
public sealed record FlaggedTaskResetResult(
    bool TreeReset,
    IReadOnlyList<string> Removed,
    bool SnapshotMissing,
    string? Failure);

/// <summary>
/// Reset promises a flagged task a fresh start, and the working tree is where the
/// flagged run's edits live. A flagged single-task run leaves them there — only a
/// drain flag and a cancel reset the tree — so Reset was the operator's one tool for
/// clearing them and did not. It also archived the two files a resetter needs
/// (<c>pre-run-untracked.txt</c> and <c>run-base.txt</c>), after which nothing could
/// put the tree back at all.
/// <para>
/// Order matters: capture, then reset, then archive. The archive move takes those two
/// files with it, and the re-capture has to happen first because the operator may have
/// edited since the flag and the modal promises their work will not be lost.
/// </para>
/// </summary>
internal static class FlaggedTaskReset
{
    internal static async Task<FlaggedTaskResetResult> ResetAsync(
        string rootPath,
        string taskId,
        string? tasksDir,
        IGitInvoker git,
        CancellationToken cancellationToken)
    {
        var taskDirectory = Path.Combine(rootPath, ".relay", taskId);

        // The bundle is re-cut from the tree as it stands NOW, not as it stood at flag
        // time: "it won't be lost" is the promise the modal makes, and a failed capture
        // is not a reason to stop, exactly as at flag time.
        await FlaggedWorkStore.CaptureAsync(
            rootPath, taskId, taskDirectory, ReadFlaggedStage(taskDirectory), git,
            DateTimeOffset.UtcNow, cancellationToken);

        var treeReset = false;
        IReadOnlyList<string> removed = [];
        var snapshotMissing = false;
        string? failure = null;
        try
        {
            var result = await WorktreeResetter.ResetAsync(rootPath, taskId, tasksDir, git, cancellationToken);
            treeReset = true;
            removed = result.Removed;
            snapshotMissing = result.SnapshotMissing;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = ex.Message;
        }

        new RelayTaskRepository(rootPath).ResetTask(taskId);
        return new FlaggedTaskResetResult(treeReset, removed, snapshotMissing, failure);
    }

    /// <summary>The stage the marker names, or 0 when there is no marker to read.</summary>
    private static int ReadFlaggedStage(string taskDirectory)
    {
        var marker = Path.Combine(taskDirectory, "NEEDS-REVIEW");
        if (!File.Exists(marker))
            return 0;

        foreach (var line in File.ReadLines(marker))
        {
            var at = line.IndexOf("stage", StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                continue;
            var digits = new string(line[at..].SkipWhile(c => !char.IsAsciiDigit(c)).TakeWhile(char.IsAsciiDigit).ToArray());
            if (int.TryParse(digits, out var stage))
                return stage;
        }

        return 0;
    }
}
