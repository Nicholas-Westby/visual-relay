using System.Text;
using System.Text.Json;
using VisualRelay.Core.Tasks;
using VisualRelay.Core.Traces;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Commit-gate resume validation: when stages 1–10 are Done and only stage 11
    /// (Commit) failed, re-validate the gate test suite and the recorded tree hash.
    /// Extracted from RunTaskAsync to keep the main file under the 300-line guard.
    /// </summary>
    private async Task<(string previousSeal, string taskHash, int firstStageToRun)> ValidateCommitGateResumeAsync(
        string rootPath,
        string taskDirectory,
        RelayConfig config,
        StringBuilder ledger,
        List<string> seals,
        string previousSeal,
        string taskHash,
        int firstStageToRun,
        List<StageStatusEntry> statusEntries,
        CancellationToken cancellationToken)
    {
        if (_options.Resume && firstStageToRun == 11
            && statusEntries.Count >= 10
            && statusEntries.Take(10).All(e => e.Status == "Done"))
        {
            var manifestPath = Path.Combine(taskDirectory, "manifest.txt");
            var currentManifest = File.Exists(manifestPath)
                ? (await File.ReadAllLinesAsync(manifestPath, cancellationToken))
                    .Where(l => !string.IsNullOrWhiteSpace(l)).ToList()
                : new List<string>();

            // Re-run the gate test suite.
            bool gatePassed = false;
            try
            {
                var testResult = await _dependencies.TestRunner.RunAsync(
                    rootPath, config.TestCommand, cancellationToken);
                gatePassed = testResult is { TimedOut: false, ExitCode: 0 };
            }
            catch
            {
                // Test runner failure → treat as gate failure.
            }

            // Re-validate the recorded stage-10 tree hash against the current worktree.
            var recordedTreeHash = string.Empty;
            if (seals.Count >= 10)
            {
                try
                {
                    using var doc = JsonDocument.Parse(seals[9]);
                    if (doc.RootElement.TryGetProperty("treeHash", out var th))
                        recordedTreeHash = th.GetString() ?? string.Empty;
                }
                catch { /* malformed seal — treat as mismatch */ }
            }
            var currentHash = WorkingTreeHash(rootPath, currentManifest);
            var hashMatches = !string.IsNullOrEmpty(recordedTreeHash)
                && string.Equals(currentHash, recordedTreeHash, StringComparison.Ordinal);

            if (!gatePassed || !hashMatches)
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
            }
        }

        return (previousSeal, taskHash, firstStageToRun);
    }

    /// <summary>
    /// Executes the final commit step (stage 11) including task retirement,
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

        var commitSha = "simulated";
        if (_options.CreateGitCommit)
        {
            var retirement = TaskCompletionArchive.RetireAsync(rootPath, config, taskId, task);

            var proofFiles = new List<string>();
            if (config.CommitProofArtifacts)
            {
                proofFiles.AddRange(new[]
                {
                    Path.Combine(".relay", taskId, "ledger.md"),
                    Path.Combine(".relay", taskId, $"{taskId}.seals"),
                    Path.Combine(".relay", taskId, "manifest.txt"),
                    Path.Combine(".relay", taskId, "status.json"),
                });

                // ── Per-stage .input.json and .report.json artifacts ──
                if (Directory.Exists(taskDirectory))
                {
                    var inputFiles = Directory.EnumerateFiles(taskDirectory, "stage*-attempt*.input.json");
                    var reportFiles = Directory.EnumerateFiles(taskDirectory, "stage*-attempt*.report.json");
                    var allArtifacts = inputFiles.Concat(reportFiles);

                    // Group by stage number and pick all files from the highest attempt.
                    var latestByStage = allArtifacts
                        .GroupBy(f => RelayAttempt.StageNumber(Path.GetFileName(f)) ?? 0)
                        .Where(g => g.Key > 0)
                        .SelectMany(g =>
                        {
                            var maxAttempt = g.Max(f => RelayAttempt.AttemptNumber(Path.GetFileName(f)));
                            return g.Where(f => RelayAttempt.AttemptNumber(Path.GetFileName(f)) == maxAttempt);
                        });

                    foreach (var fullPath in latestByStage)
                    {
                        proofFiles.Add(Path.Combine(".relay", taskId, Path.GetFileName(fullPath)));
                    }
                }
            }
            if (retirement?.Additions is { Count: > 0 } additions)
                proofFiles.AddRange(additions);

            var chain = BuildCommitChain(commitMessages, taskId);
            var commit = await GitCommitter.CommitAsync(rootPath, taskId, taskHash, chain, manifest, proofFiles, activeLockNonce, preRunUntracked, config.TasksDir, cancellationToken, _dependencies.GitInvoker, runBaseSha);
            if (!commit.Success)
            {
                retirement?.Rollback?.Invoke();
                return await FlagAsync(rootPath, runId, taskId, taskDirectory, 11, commit.Error ?? "git commit failed", null, statusEntries, cancellationToken);
            }

            commitSha = commit.CommitSha ?? "unknown";

            if (preRunUntracked is not null)
            {
                var missed = await GitCommitter.FindUncommittedAuthoredFilesAsync(
                    rootPath, preRunUntracked, config.TasksDir, cancellationToken, _dependencies.GitInvoker);
                if (missed.Count > 0)
                {
                    retirement?.Rollback?.Invoke();
                    return await FlagAsync(rootPath, runId, taskId, taskDirectory, 11,
                        $"sealed commit is missing authored files: {string.Join(", ", missed.Order(StringComparer.Ordinal).Select(f => $"`{f}`"))}",
                        null, statusEntries, cancellationToken);
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
                    11,
                    Data: new Dictionary<string, string> { ["path"] = retirement.DestinationPath }), cancellationToken);
            }
        }
        MarkStatus(statusEntries, 11, "Done");
        await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
        return new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Committed, taskHash, commitSha, null);
    }
}
