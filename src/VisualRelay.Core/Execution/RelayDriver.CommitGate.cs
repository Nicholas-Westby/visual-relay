using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VisualRelay.Core.Tasks;
using VisualRelay.Core.Traces;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Commit-gate resume validation: when stages 1–11 are Done and only stage 12
    /// (Commit) failed, re-validate the gate test suite ISOLATED and the recorded
    /// tree hash. Uses the same <see cref="RunIsolatedVerifyAsync"/> as the normal
    /// pipeline stages 10/11 and emits a <c>verify_result</c> event so the resumed
    /// run's log is indistinguishable in shape from a normal run's.
    /// Extracted from RunTaskAsync to keep the main file under the 300-line guard.
    /// </summary>
    private async Task<(string previousSeal, string taskHash, int firstStageToRun, RelayTaskOutcome? flaggedOutcome)> ValidateCommitGateResumeAsync(
        string rootPath,
        string taskDirectory,
        RelayConfig config,
        StringBuilder ledger,
        List<string> seals,
        string previousSeal,
        string taskHash,
        int firstStageToRun,
        List<StageStatusEntry> statusEntries,
        string runId,
        string taskId,
        CancellationToken cancellationToken)
    {
        if (_options.Resume && firstStageToRun == 12
            && statusEntries.Count >= 11
            && statusEntries.Take(11).All(e => StageStatusIsComplete(e.Status)))
        {
            var manifestPath = Path.Combine(taskDirectory, "manifest.txt");
            var currentManifest = File.Exists(manifestPath)
                ? (await File.ReadAllLinesAsync(manifestPath, cancellationToken))
                    .Where(l => !string.IsNullOrWhiteSpace(l)).ToList()
                : new List<string>();

            // Run the isolated verify gate (same mechanism as stages 10/11).
            var stage12 = RelayStages.All[11];
            // Each resume's gate is its own attempt: attempt 1 every time overwrote the last gate's output.
            var attempt = RelayAttempt.Next(taskDirectory, 12);
            var (testResult, verifyMutations) = await RunIsolatedVerifyAsync(
                rootPath, config, stageNumber: 12, attempt, runId, taskId, cancellationToken);
            await EmitMutatedTreeAdvisoryAsync(rootPath, runId, taskId, stage12, verifyMutations, cancellationToken);

            bool gatePassed = testResult is { TimedOut: false, ExitCode: 0 };

            // Emit verify_result event + artifact so the resumed run's log is
            // indistinguishable from a normal pipeline run.
            var (_, _, _, reason) = await PublishVerifyResultAsync(
                rootPath, runId, taskId, taskDirectory, stage12, attempt, config,
                testResult, currentManifest, cancellationToken);

            // Re-validate the recorded stage-11 tree hash against the current worktree.
            var recordedTreeHash = string.Empty;
            if (seals.Count >= 11)
            {
                try
                {
                    using var doc = JsonDocument.Parse(seals[10]);
                    if (doc.RootElement.TryGetProperty("treeHash", out var th))
                        recordedTreeHash = th.GetString() ?? string.Empty;
                }
                catch { /* malformed seal — treat as mismatch */ }
            }
            var currentHash = WorkingTreeHash(rootPath, currentManifest);
            var hashMatches = !string.IsNullOrEmpty(recordedTreeHash)
                && string.Equals(currentHash, recordedTreeHash, StringComparison.Ordinal);

            if (!gatePassed)
            {
                // Test failed — flag with enriched reason from the verify output.
                var enrichedReason = string.IsNullOrWhiteSpace(reason)
                    ? "commit-gate verify failed" : reason;
                var flaggedOutcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, 12,
                    enrichedReason, testResult.Output, statusEntries, cancellationToken);
                await PublishStageDoneAsync(rootPath, runId, taskId, stage12, TimeSpan.Zero,
                    null, 0, 0, cancellationToken, status: "Flagged");
                return (previousSeal, taskHash, firstStageToRun, flaggedOutcome);
            }

            if (!hashMatches)
            {
                firstStageToRun = 5;

                var truncated = new List<string>();
                foreach (var seal in seals)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(seal);
                        if (doc.RootElement.TryGetProperty("n", out var n) && n.GetInt32() <= 4)
                            truncated.Add(seal);
                    }
                    catch { /* malformed seal — drop */ }
                }
                seals.Clear();
                seals.AddRange(truncated);

                if (seals.Count > 0)
                {
                    using var doc = JsonDocument.Parse(seals[^1]);
                    if (doc.RootElement.TryGetProperty("seal", out var sp))
                        taskHash = previousSeal = sp.GetString() ?? string.Empty;
                }
                else
                {
                    previousSeal = string.Empty;
                    taskHash = string.Empty;
                }

                for (int i = 0; i < statusEntries.Count; i++)
                {
                    if (statusEntries[i].Stage >= 5)
                        statusEntries[i] = statusEntries[i] with { Status = "Waiting", Error = null };
                }

                ledger.AppendLine("> **Resume fallback**: commit-gate re-validation failed");
                ledger.AppendLine($"> gatePassed={gatePassed} hashMatch={hashMatches}");
                ledger.AppendLine("> Restarting from stage 5 (Author-tests).");
                ledger.AppendLine();

                // The ledger lines were the only record: no event and no status text, so
                // an operator watching a resume saw it start again at stage 5 with
                // nothing anywhere saying why.
                await _dependencies.EventSink.PublishAsync(new RelayEvent(
                    DateTimeOffset.UtcNow, "warn", "resume_restart", runId, rootPath, taskId, 12,
                    Data: new Dictionary<string, string>
                    {
                        ["fromStage"] = "12",
                        ["toStage"] = "5",
                        ["reason"] = "working tree changed since verify",
                    }), cancellationToken);
            }
        }

        return (previousSeal, taskHash, firstStageToRun, null);
    }

    /// <summary>
    /// Executes the final commit step (stage 12) including task retirement,
    /// git commit, post-commit invariant checks, and event publication.
    /// Extracted from RunTaskAsync.
    /// </summary>
    private async Task<RelayTaskOutcome> ExecuteCommitStageAsync(
        string rootPath,
        string runId,
        string taskId,
        string taskDirectory,
        RelayConfig config,
        RelayTaskItem? task,
        IReadOnlyList<string> commitMessages,
        IReadOnlyList<string> manifest,
        string taskMarkdown,
        string taskHash,
        string activeLockNonce,
        IReadOnlySet<string>? preRunUntracked,
        string? runBaseSha,
        List<StageStatusEntry> statusEntries,
        CancellationToken cancellationToken)
    {
        // Plan-only run: return Planned without touching git.
        if (_options.LastStageToRun is not null)
            return new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Planned, null, null, null);

        var commitStopwatch = Stopwatch.StartNew();
        var commitSha = "simulated";
        var doneWritten = false;
        if (_options.CreateGitCommit)
        {
            // Refuse to retire/commit-as-done a code-expecting run that changed no
            // source or tests (only proof + spec rename). Runs BEFORE retirement so
            // the spec is not renamed and nothing is committed on a phantom.
            var codelessFlag = await CheckCodeProducedAsync(
                rootPath, runId, taskId, taskDirectory, config, manifest, taskMarkdown,
                runBaseSha, statusEntries, cancellationToken);
            if (codelessFlag is not null)
                return codelessFlag;

            var retirement = TaskCompletionArchive.RetireAsync(rootPath, config, taskId, task);

            // The only paths staged beyond the run's own changes: the archived task
            // file the retirement move just wrote. Nothing under .relay/ is ever
            // staged — the bookkeeping stays on disk (see GitCommitter).
            var retirementFiles = retirement?.Additions ?? [];

            var (chain, advisories) = BuildCommitChain(commitMessages, taskId);
            foreach (var advisory in advisories)
            {
                await _dependencies.EventSink.PublishAsync(new RelayEvent(
                    DateTimeOffset.UtcNow,
                    "warn",
                    "commit_msg_rejected",
                    runId,
                    rootPath,
                    taskId,
                    12,
                    Data: new Dictionary<string, string> { ["message"] = advisory }), cancellationToken);
            }

            // Write final "Done" status BEFORE the commit so status.json is
            // included in the sealed commit. Publish deferred to the bottom.
            MarkStatusDone(statusEntries, RelayStages.All[11], commitStopwatch.Elapsed, null, null);
            await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
            doneWritten = true;

            var commit = await GitCommitter.CommitAsync(rootPath, taskId, taskHash, chain, manifest, retirementFiles, activeLockNonce, preRunUntracked, config.TasksDir, _dependencies.GitInvoker, cancellationToken, runBaseSha, timeProvider: _dependencies.TimeProvider);
            if (!commit.Success)
            {
                retirement?.Rollback?.Invoke();
                var outcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, 12, commit.Error ?? "git commit failed", null, statusEntries, cancellationToken);
                await PublishStageDoneAsync(rootPath, runId, taskId, RelayStages.All[11], commitStopwatch.Elapsed, null, 0, 0, cancellationToken, status: "Flagged");
                return outcome;
            }

            commitSha = commit.CommitSha ?? "unknown";

            if (preRunUntracked is not null)
            {
                var missed = await GitCommitter.FindUncommittedAuthoredFilesAsync(
                    rootPath, preRunUntracked, config.TasksDir, _dependencies.GitInvoker, cancellationToken,
                    timeProvider: _dependencies.TimeProvider);
                if (missed.Count > 0)
                {
                    // Keep the seal: the commit landed. Do NOT rollback retirement —
                    // the sealed commit recorded the folder move; rolling back would
                    // duplicate the task definition (active + completed). The flag is
                    // an advisory pointing at the sealed commit.
                    var outcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, 12,
                        $"sealed commit is missing authored files: {string.Join(", ", missed.Order(StringComparer.Ordinal).Select(f => $"`{f}`"))}",
                        null, statusEntries, cancellationToken);
                    await PublishStageDoneAsync(rootPath, runId, taskId, RelayStages.All[11], commitStopwatch.Elapsed, null, 0, 0, cancellationToken, status: "Flagged");
                    return outcome;
                }
            }

            if (retirement is not null)
            {
                var eventName = config.ArchiveOnDone ? "task_archived" : "task_done";
                await _dependencies.EventSink.PublishAsync(new RelayEvent(
                    DateTimeOffset.UtcNow,
                    "info",
                    eventName,
                    runId,
                    rootPath,
                    taskId,
                    12,
                    Data: new Dictionary<string, string> { ["path"] = retirement.DestinationPath }), cancellationToken);
            }
        }
        if (!doneWritten)
        {
            MarkStatusDone(statusEntries, RelayStages.All[11], commitStopwatch.Elapsed, null, null);
            await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
        }
        await PublishStageDoneAsync(rootPath, runId, taskId, RelayStages.All[11], commitStopwatch.Elapsed,
            null, 0, 0, cancellationToken, status: "Done");
        FlaggedWorkStore.Delete(taskDirectory);
        return new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Committed, taskHash, commitSha, null);
    }
}
