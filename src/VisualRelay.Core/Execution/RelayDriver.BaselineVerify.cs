using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    private static HashSet<string> ExtractFailureIds(string? output)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(output)) return ids;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (line.Trim().StartsWith("Failed ", StringComparison.Ordinal))
                ids.Add(line.Trim()["Failed ".Length..].Trim());
        return ids;
    }

    /// <summary>
    /// The failures in <paramref name="workingResult"/> that the run's base commit does not have:
    /// null when there are none, "verify failed" when the base could not be run or the red run
    /// named no failure. The base runs in its own snapshot, never in the checkout: stashing the
    /// checkout for the length of a suite left the task's work in the stash whenever the run died
    /// meanwhile, and ran the base at a different path from the snapshot it was compared with.
    /// </summary>
    private async Task<string?> GetNewFailuresAsync(
        string rootPath, string taskId, string runId, string? runBaseSha, string testCommand,
        TestRunResult workingResult, CancellationToken ct)
    {
        var baseline = await RunOnTheBaseAsync(rootPath, taskId, runId, runBaseSha, testCommand, ct);
        if (baseline is null || baseline.TimedOut) return "verify failed";
        var current = ExtractFailureIds(workingResult.Output);
        if (current.Count == 0 && workingResult.ExitCode != 0)
            return "verify failed";
        current.ExceptWith(ExtractFailureIds(baseline.Output));
        return current.Count == 0 ? null
            : string.Join(", ", current.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Runs <paramref name="testCommand"/> in a snapshot of <paramref name="baseSha"/> (HEAD when the
    /// run recorded no base) carrying the checkout's git-ignored overlay, as the verify snapshot does.
    /// Null when there is no commit or the snapshot could not be made.
    /// </summary>
    private async Task<TestRunResult?> RunOnTheBaseAsync(
        string rootPath, string taskId, string runId, string? baseSha, string testCommand, CancellationToken ct)
    {
        if (await ResolveBaseShaAsync(rootPath, baseSha, ct) is not { } commit)
            return null;

        var worktreeId = $"{taskId}-baseline";
        string? worktreePath = null;
        try
        {
            // Fresh token, like the teardown below: a torn `git worktree add` leaves no path to remove.
            worktreePath = await PlanningWorktree.CreateAsync(
                rootPath, worktreeId, runId, _dependencies.GitInvoker, CancellationToken.None,
                timeProvider: _dependencies.TimeProvider, commitish: commit);
            await _dependencies.EventSink.PublishAsync(new RelayEvent(
                DateTimeOffset.UtcNow, "info", "verify_snapshot_created", runId, rootPath, taskId, 10,
                Data: new Dictionary<string, string> { ["snapshot"] = worktreeId, ["worktree"] = worktreePath }), ct);
            var ignoredEntries = await OverlayIgnoredEntriesAsync(
                rootPath, worktreePath, worktreeId, runId, IgnoredOverlayCopyMaxBytes, cloneOverlay: true, ct);
            var searchPaths = await SnapshotSearchPathsAsync(
                rootPath, worktreePath, ignoredEntries, runId, taskId, stageNumber: 10, ct);
            return await RunInSnapshotAsync(worktreePath, testCommand, searchPaths, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (worktreePath is null)
        {
            // Not a git repository, or git refused the checkout: there is no base to compare with.
            await _dependencies.EventSink.PublishAsync(new RelayEvent(
                DateTimeOffset.UtcNow, "warn", "baseline_unavailable", runId, rootPath, taskId, 10,
                Data: new Dictionary<string, string> { ["reason"] = ex.Message }), ct);
            return null;
        }
        finally
        {
            if (worktreePath is not null)
                await CleanupVerifyWorktreeAsync(rootPath, worktreePath);
        }
    }
}
