using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Costs;
using VisualRelay.Core.Tasks;
using VisualRelay.Core.Traces;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver : IRelayTaskRunner
{
    private readonly RelayDriverDependencies _dependencies;
    private readonly RelayDriverOptions _options;

    public RelayDriver(RelayDriverDependencies dependencies, RelayDriverOptions? options = null)
    {
        _dependencies = dependencies;
        _options = options ?? RelayDriverOptions.Default;
    }

    public async Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken cancellationToken = default)
    {
        var runId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{taskId}";
        var taskDirectory = Path.Combine(rootPath, ".relay", taskId);
        var statusEntries = SeedStatusEntries();
        try
        {
            var config = await RelayConfigLoader.LoadAsync(rootPath, cancellationToken);
            await using var activeLock = await ActiveTaskLock.AcquireAsync(rootPath, taskId, cancellationToken);
            Directory.CreateDirectory(taskDirectory);
            File.Delete(Path.Combine(taskDirectory, "NEEDS-REVIEW"));

            var repository = new RelayTaskRepository(rootPath);
            var task = (await repository.ListAsync(includeNeedsReview: true, cancellationToken)).FirstOrDefault(x => x.Id == taskId);
            var input = task is null ? new RelayTaskInput(string.Empty, null) : await repository.ReadTaskInputAsync(task, cancellationToken);
            var ledger = new StringBuilder();
            var manifest = new List<string>();
            var seals = new List<string>();
            var previousSeal = string.Empty;
            var taskHash = string.Empty;
            var sessionCostUsd = 0d;
            var unknownCostStageCount = 0;
            var firstStageToRun = 1;
            if (_options.Resume)
                LoadResumeState(taskDirectory, taskId, ledger, manifest, seals,
                    ref previousSeal, ref taskHash, ref sessionCostUsd, ref unknownCostStageCount,
                    statusEntries, ref firstStageToRun);
            IReadOnlyList<string> commitMessages = [];
            await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);

            await _dependencies.EventSink.PublishAsync(new RelayEvent(
                DateTimeOffset.UtcNow,
                "info",
                "run_start",
                runId,
                rootPath,
                taskId,
                Data: new Dictionary<string, string> { ["base_url"] = ModelBackend.BaseUrl }), cancellationToken);

            // Snapshot untracked files before the first agent edit so the commit pass
            // can distinguish files authored during the run from pre-existing scratch.
            var preRunUntracked = _options.CreateGitCommit
                ? await GitCommitter.CaptureUntrackedSnapshotAsync(rootPath, cancellationToken)
                : null;

            var stage10Handled = false;

            foreach (var stage in RelayStages.All)
            {
                if (stage.Number < firstStageToRun)
                    continue;
                if (_options.LastStageToRun is { } last && stage.Number > last)
                    break;
                if (stage.Number == 10 && stage10Handled)
                    continue;
                await PublishAsync("info", "stage_start", rootPath, runId, taskId, stage, cancellationToken);
                MarkStatus(statusEntries, stage.Number, "Running");
                await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
                var stopwatch = Stopwatch.StartNew();
                string body;
                string? check = null;
                RelayCostEstimate? cost = null;

                if (stage.Kind == "driver")
                {
                    body = _options.CreateGitCommit ? "Committed by Visual Relay." : "Simulated commit by Visual Relay.";
                }
                else
                {
                    var invocation = BuildInvocation(rootPath, runId, taskId, taskDirectory, config, stage, input, ledger, manifest);
                    var result = await _dependencies.SubagentRunner.RunAsync(invocation, cancellationToken);
                    cost = TryEstimateCost(invocation.ReportFile);
                    if (cost is not null) sessionCostUsd += cost.CostUsd; else unknownCostStageCount++;
                    if (!result.IsValid || string.IsNullOrWhiteSpace(result.Json))
                    {
                        return await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number, result.Error ?? "invalid subagent result", result.RawText, statusEntries, cancellationToken);
                    }

                    body = result.Json;
                    var json = JsonDocument.Parse(result.Json).RootElement.Clone();
                    if (stage.Number == 4)
                    {
                        manifest.Clear();
                        var raw = ReadStringArray(json, "manifest").Distinct(StringComparer.Ordinal).ToList();
                        var dropped = new List<string>();
                        var clean = new List<string>();
                        foreach (var e in raw)
                        {
                            if (IsPathUnderDirectory(rootPath, e, config.TasksDir))
                                dropped.Add(e);
                            else
                                clean.Add(e);
                        }
                        manifest.AddRange(clean);
                        if (dropped.Count > 0)
                        {
                            var note = dropped.Count == 1
                                ? $"> **Note**: dropped 1 task-dir entry from manifest: `{dropped[0]}`"
                                : $"> **Note**: dropped {dropped.Count} task-dir entries from manifest: {string.Join(", ", dropped.Select(d => $"`{d}`"))}";
                            ledger.AppendLine(note);
                            ledger.AppendLine();
                        }
                        await WriteManifestAsync(taskDirectory, manifest, cancellationToken);
                    }

                    if (stage.Number == 5)
                    {
                        var testFiles = ReadStringArray(json, "testFiles");
                        var hasImpl = manifest.Any(f => !testFiles.Contains(f, StringComparer.Ordinal) && IsImpl(f));

                        if (hasImpl)
                        {
                            var command = config.TestFileCommand.Replace("{files}", string.Join(' ', testFiles), StringComparison.Ordinal);
                            var gateResult = await AuthorTestGate.RunAsync(
                                rootPath,
                                taskId,
                                runId,
                                manifest,
                                testFiles,
                                command,
                                _dependencies.TestRunner,
                                cancellationToken);
                            if (gateResult.Error is not null)
                            {
                                return await FlagAsync(rootPath, runId, taskId, taskDirectory, 5, gateResult.Error, null, statusEntries, cancellationToken);
                            }

                            if (gateResult.RestoreResult == RedGateRestoreResult.Conflict)
                            {
                                return await FlagAsync(rootPath, runId, taskId, taskDirectory, 5, "red gate stash restore conflict", null, statusEntries, cancellationToken);
                            }

                            var testResult = gateResult.TestResult;
                            if (testResult.TimedOut)
                            {
                                return await FlagAsync(rootPath, runId, taskId, taskDirectory, 5,
                                    ErrorHintClassifier.WithHint(testResult.Output), null, statusEntries, cancellationToken);
                            }

                            check = testResult.ExitCode == 0 ? "green" : "red";
                            if (check != "red")
                            {
                                if (gateResult.StashedImplementation)
                                {
                                    return await FlagAsync(rootPath, runId, taskId, taskDirectory, 5,
                                        "author-tests passed after implementation files were stripped", null, statusEntries, cancellationToken);
                                }

                                // Already-resolved: no implementation delta to strip;
                                // accept green regression coverage.
                                check = "green";
                                ledger.AppendLine("> **Already-resolved**: no implementation delta to strip; accepted green regression coverage.");
                                ledger.AppendLine();
                            }
                        }
                    }

                    if (stage.Number == 9)
                    {
                        var testResult = await _dependencies.TestRunner.RunAsync(rootPath, config.TestCommand, cancellationToken);
                        if (testResult.TimedOut)
                        {
                            return await FlagAsync(rootPath, runId, taskId, taskDirectory, 9,
                                ErrorHintClassifier.WithHint(testResult.Output), null, statusEntries, cancellationToken);
                        }

                        check = testResult.ExitCode == 0 ? "green" : "red";
                        commitMessages = ReadStringArray(json, "commitMessages");
                        if (commitMessages.Count == 0)
                        {
                            var legacy = ReadOptionalString(json, "commitMessage");
                            if (legacy is not null)
                            {
                                commitMessages = [legacy];
                            }
                        }

                        if (check != "green")
                        {
                            var newFailures = config.BaselineVerify
                                ? await GetNewFailuresAsync(rootPath, taskId, runId, _dependencies.TestRunner, config.TestCommand, testResult, cancellationToken)
                                : null;
                            if (!config.BaselineVerify || newFailures is not null)
                            {
                                if (config.MaxVerifyLoops <= 0)
                                {
                                    var reason = newFailures is null || newFailures == "verify failed" ? "verify failed" : $"new test failures: {newFailures}";
                                    return await FlagAsync(rootPath, runId, taskId, taskDirectory, 9, reason, testResult.Output, statusEntries, cancellationToken);
                                }

                                // Genuinely red — record stage 9, then enter fix-verify loop.
                                (previousSeal, taskHash) = await RecordStageAsync(rootPath, runId, taskId, taskDirectory, stage, body, check, cost,
                                    stopwatch, ledger, seals, statusEntries, manifest, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);

                                var (loopOutcome, prevSeal, tHash, costUsd, unknownCost) = await RunVerifyFixLoopAsync(
                                    rootPath, runId, taskId, taskDirectory, config, input, ledger, seals, statusEntries, manifest,
                                    previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, testResult.Output, cancellationToken);
                                if (loopOutcome is not null)
                                    return loopOutcome;
                                previousSeal = prevSeal; taskHash = tHash; sessionCostUsd = costUsd; unknownCostStageCount = unknownCost;
                                stage10Handled = true;
                            }
                            else
                            {
                                check = "green"; // baseline-excluded: all failures pre-existing
                            }
                        }
                    }
                }

                if (stage.Number != 9 || !stage10Handled)
                {
                    (previousSeal, taskHash) = await RecordStageAsync(rootPath, runId, taskId, taskDirectory, stage, body, check, cost,
                        stopwatch, ledger, seals, statusEntries, manifest, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);
                }
            }

            // Plan-only run: stages 1–4 (or up to LastStageToRun) completed;
            // return Planned without touching git. The artifacts on disk (ledger,
            // manifest.txt, status.json, seals) are ready for a later Resume run.
            if (_options.LastStageToRun is not null)
                return new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Planned, null, null, null);

            var commitSha = "simulated";
            if (_options.CreateGitCommit)
            {
                // Retire the task BEFORE committing so the rename (deletion of old
                // path, addition of DONE-/archived path) lands in the same commit.
                var retirement = TaskCompletionArchive.RetireAsync(rootPath, config, taskId, task);

                var proofFiles = new List<string>
                {
                    Path.Combine(".relay", taskId, "ledger.md"),
                    Path.Combine(".relay", taskId, $"{taskId}.seals"),
                    Path.Combine(".relay", taskId, "manifest.txt"),
                    Path.Combine(".relay", taskId, "status.json"),
                };
                if (retirement?.Additions is { Count: > 0 } additions)
                    proofFiles.AddRange(additions);

                var chain = BuildCommitChain(commitMessages, taskId);
                var commit = await GitCommitter.CommitAsync(rootPath, taskId, taskHash, chain, manifest, proofFiles, activeLock.Nonce, preRunUntracked, cancellationToken);
                if (!commit.Success)
                {
                    // Rollback: restore original paths so the task stays runnable.
                    retirement?.Rollback?.Invoke();
                    return await FlagAsync(rootPath, runId, taskId, taskDirectory, 11, commit.Error ?? "git commit failed", null, statusEntries, cancellationToken);
                }

                commitSha = commit.CommitSha ?? "unknown";

                // Publish task_done / task_archived after successful commit.
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
            // Belt-and-suspenders: mark stage 11 done (covers simulated path too).
            MarkStatus(statusEntries, 11, "Done");
            await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
            return new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Committed, taskHash, commitSha, null);
        }
        catch (Exception ex)
        {
            return await FlagAsync(rootPath, runId, taskId, taskDirectory, 0, $"exception: {ex.Message}", ex.ToString(), statusEntries, cancellationToken);
        }
    }
}
