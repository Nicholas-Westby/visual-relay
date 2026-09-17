using System.Globalization;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// The failures in <paramref name="workingResult"/> that the run's base commit does not have:
    /// null when there are none, "verify failed" when the base could not be run or the red run
    /// named no failure, and <see cref="RefusedBeforeTestsReason"/> when that red run was an
    /// operating-system refusal. The base runs in its own snapshot, never in the checkout: stashing the
    /// checkout for the length of a suite left the task's work in the stash whenever the run died
    /// meanwhile, and ran the base at a different path from the snapshot it was compared with.
    /// </summary>
    private async Task<string?> GetNewFailuresAsync(
        string rootPath, string taskId, string runId, string? runBaseSha, string testCommand,
        TestRunResult workingResult, string? verifyOutputPath, CancellationToken ct)
    {
        var current = TestFailureIds.Extract(workingResult.Output);
        // A red run that names no failing test leaves the base nothing to subtract, so it is not run.
        if (current.Count == 0 && workingResult.ExitCode != 0)
            return RefusedBeforeTestsReason(workingResult) ?? "verify failed";
        var baseline = await RunOnTheBaseAsync(rootPath, taskId, runId, runBaseSha, testCommand, ct);
        if (baseline is null || baseline.TimedOut) return "verify failed";
        var onTheBase = TestFailureIds.Extract(baseline.Output);
        var preExisting = current.Count(onTheBase.Contains);
        current.ExceptWith(onTheBase);
        var newFailures = string.Join(", ", current.Order(StringComparer.Ordinal));
        await PublishBaselineAsync(rootPath, runId, taskId, baseline, verifyOutputPath, newFailures, preExisting, ct);
        return current.Count == 0 ? null : newFailures;
    }

    /// <summary>
    /// Keeps the base run's output beside the verify output and says what was subtracted: a
    /// "new test failures" flag used to leave nothing to check the base's side against.
    /// </summary>
    private async Task PublishBaselineAsync(
        string rootPath, string runId, string taskId, TestRunResult baseline, string? verifyOutputPath,
        string newFailures, int preExisting, CancellationToken ct)
    {
        string? outputFile = null;
        if (verifyOutputPath is not null)
        {
            outputFile = verifyOutputPath.Replace(".verify-output.txt", ".baseline-output.txt", StringComparison.Ordinal);
            try
            {
                await File.WriteAllTextAsync(outputFile, baseline.Output, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outputFile = null;
            }
        }

        await _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "info", "verify_baseline", runId, rootPath, taskId, 10,
            Data: new Dictionary<string, string>
            {
                ["newFailures"] = newFailures,
                ["preExisting"] = preExisting.ToString(CultureInfo.InvariantCulture),
                ["outputFile"] = outputFile ?? string.Empty,
            }), ct);
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
            // Announced after the overlay, not before it: the event names the arm that made the
            // snapshot's copies, and only the overlay knows which one ran.
            var (ignoredEntries, overlayArm) = await OverlayIgnoredEntriesAsync(
                rootPath, worktreePath, worktreeId, runId, IgnoredOverlayCopyMaxBytes, cloneOverlay: true, ct);
            await _dependencies.EventSink.PublishAsync(new RelayEvent(
                DateTimeOffset.UtcNow, "info", "verify_snapshot_created", runId, rootPath, taskId, 10,
                Data: new Dictionary<string, string>
                {
                    ["snapshot"] = worktreeId,
                    ["worktree"] = worktreePath,
                    ["overlay"] = overlayArm,
                }), ct);
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
