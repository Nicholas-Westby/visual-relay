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
            IReadOnlyList<string> commitMessages = [];

            await _dependencies.EventSink.PublishAsync(new RelayEvent(
                DateTimeOffset.UtcNow,
                "info",
                "run_start",
                runId,
                rootPath,
                taskId,
                Data: new Dictionary<string, string> { ["base_url"] = ModelBackend.BaseUrl }), cancellationToken);

            foreach (var stage in RelayStages.All)
            {
                await PublishAsync("info", "stage_start", rootPath, runId, taskId, stage, cancellationToken);
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
                    if (cost is not null)
                    {
                        sessionCostUsd += cost.CostUsd;
                    }
                    else
                    {
                        unknownCostStageCount++;
                    }
                    if (!result.IsValid || string.IsNullOrWhiteSpace(result.Json))
                    {
                        return await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number, result.Error ?? "invalid subagent result", result.RawText, cancellationToken);
                    }

                    body = result.Json;
                    var json = JsonDocument.Parse(result.Json).RootElement.Clone();
                    if (stage.Number == 4)
                    {
                        manifest.Clear();
                        manifest.AddRange(ReadStringArray(json, "manifest").Distinct(StringComparer.Ordinal));
                        var bad = manifest.FirstOrDefault(e => IsPathUnderDirectory(rootPath, e, config.TasksDir));
                        if (bad is not null)
                            return await FlagAsync(rootPath, runId, taskId, taskDirectory, 4,
                                $"manifest may not include task files under \"{config.TasksDir}\" (found \"{bad}\")", null, cancellationToken);
                        await WriteManifestAsync(taskDirectory, manifest, cancellationToken);
                    }

                    if (stage.Number == 5)
                    {
                        var testFiles = ReadStringArray(json, "testFiles");
                        // The red gate applies when the manifest contains at least one
                        // implementation file (a code file not declared as a test file).
                        // Non-code files (.md, .txt, .json, .yaml, .yml, .toml, .csv, and
                        // files with no extension) never trigger the gate on their own.
                        // Unknown extensions default to code (fail-safe toward requiring a test).
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
                                return await FlagAsync(rootPath, runId, taskId, taskDirectory, 5, gateResult.Error, null, cancellationToken);
                            }

                            if (gateResult.RestoreResult == RedGateRestoreResult.Conflict)
                            {
                                return await FlagAsync(rootPath, runId, taskId, taskDirectory, 5, "red gate stash restore conflict", null, cancellationToken);
                            }

                            var testResult = gateResult.TestResult;
                            if (testResult.TimedOut)
                            {
                                return await FlagAsync(rootPath, runId, taskId, taskDirectory, 5,
                                    ErrorHintClassifier.WithHint(testResult.Output), null, cancellationToken);
                            }

                            check = testResult.ExitCode == 0 ? "green" : "red";
                            if (check != "red")
                            {
                                var reason = gateResult.StashedImplementation
                                    ? "author-tests passed after implementation files were stripped"
                                    : "author-tests did not go red";
                                return await FlagAsync(rootPath, runId, taskId, taskDirectory, 5, reason, null, cancellationToken);
                            }
                        }
                    }

                    if (stage.Number == 9)
                    {
                        var testResult = await _dependencies.TestRunner.RunAsync(rootPath, config.TestCommand, cancellationToken);
                        if (testResult.TimedOut)
                        {
                            return await FlagAsync(rootPath, runId, taskId, taskDirectory, 9,
                                ErrorHintClassifier.WithHint(testResult.Output), null, cancellationToken);
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
                            return await FlagAsync(rootPath, runId, taskId, taskDirectory, 9, "verify failed", testResult.Output, cancellationToken);
                        }
                    }
                }

                AppendLedgerSection(ledger, stage, body);
                var treeHash = stage.Number >= 4 ? WorkingTreeHash(rootPath, manifest) : string.Empty;
                var artifactHash = Hashing.Sha256Hex(stage.Number.ToString(), stage.Name, body);
                var seal = Hashing.Sha256Hex(previousSeal, stage.Number.ToString(), DateTimeOffset.UtcNow.ToString("O"), artifactHash, treeHash, check ?? string.Empty);
                previousSeal = seal;
                taskHash = seal;
                seals.Add(SerializeSeal(stage.Number, artifactHash, treeHash, seal, check));
                await WriteArtifactsAsync(taskDirectory, taskId, ledger.ToString(), seals, cancellationToken);
                await PublishStageDoneAsync(rootPath, runId, taskId, stage, stopwatch.Elapsed, cost, sessionCostUsd, unknownCostStageCount, cancellationToken);
            }

            var commitSha = "simulated";
            if (_options.CreateGitCommit)
            {
                var proofFiles = new[] { Path.Combine(".relay", taskId, "ledger.md"), Path.Combine(".relay", taskId, $"{taskId}.seals"), Path.Combine(".relay", taskId, "manifest.txt") };
                var chain = BuildCommitChain(commitMessages, taskId);
                var commit = await GitCommitter.CommitAsync(rootPath, taskId, taskHash, chain, manifest, proofFiles, activeLock.Nonce, cancellationToken);
                if (!commit.Success)
                {
                    return await FlagAsync(rootPath, runId, taskId, taskDirectory, 11, commit.Error ?? "git commit failed", null, cancellationToken);
                }

                commitSha = commit.CommitSha ?? "unknown";
                await TaskCompletionArchive.CompleteAsync(rootPath, config, taskId, task, _dependencies.EventSink, runId, cancellationToken);
            }

            return new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Committed, taskHash, commitSha, null);
        }
        catch (Exception ex)
        {
            return await FlagAsync(rootPath, runId, taskId, taskDirectory, 0, $"exception: {ex.Message}", ex.ToString(), cancellationToken);
        }
    }

    private StageInvocation BuildInvocation(
        string rootPath,
        string runId,
        string taskId,
        string taskDirectory,
        RelayConfig config,
        RelayStageDefinition stage,
        RelayTaskInput input,
        StringBuilder ledger,
        IReadOnlyList<string> manifest)
    {
        var attempt = RelayAttempt.Next(taskDirectory, stage.Number);
        return new StageInvocation(
            stage,
            stage.Tier,
            runId,
            rootPath,
            taskId,
            input.Markdown,
            ledger.ToString(),
            manifest,
            config.LogSources,
            Path.Combine(taskDirectory, $"stage{stage.Number}-attempt{attempt}"),
            Path.Combine(taskDirectory, $"stage{stage.Number}-attempt{attempt}.report.json"),
            config.MaxTurns,
            TaskContext: input.Context);
    }

    private async Task<RelayTaskOutcome> FlagAsync(
        string rootPath, string runId, string taskId, string taskDirectory,
        int stage, string reason, string? details, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(taskDirectory);
        var body = $"{reason}\nstage {stage}\n";
        if (!string.IsNullOrWhiteSpace(details))
            body += $"\n{details.Trim()}\n";

        await File.WriteAllTextAsync(Path.Combine(taskDirectory, "NEEDS-REVIEW"), body, cancellationToken);
        await _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "error", "flagged", runId, rootPath, taskId, stage,
            Data: new Dictionary<string, string> { ["reason"] = reason }), cancellationToken);
        return new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Flagged, null, null, reason);
    }

    private Task PublishAsync(string level, string eventName, string rootPath, string runId, string taskId, RelayStageDefinition stage, CancellationToken cancellationToken) =>
        _dependencies.EventSink.PublishAsync(new RelayEvent(DateTimeOffset.UtcNow, level, eventName, runId, rootPath, taskId, stage.Number, stage.Tier, Data: new Dictionary<string, string> { ["name"] = stage.Name }), cancellationToken);

    private Task PublishStageDoneAsync(
        string rootPath,
        string runId,
        string taskId,
        RelayStageDefinition stage,
        TimeSpan elapsed,
        RelayCostEstimate? cost,
        double sessionCostUsd,
        int unknownCostStageCount,
        CancellationToken cancellationToken)
    {
        var costLabel = cost is not null
            ? MoneyFormatter.Dollars(cost.CostUsd)
            : stage.Kind == "driver"
                ? MoneyFormatter.Dollars(0)
                : "?";
        var sessionLabel = unknownCostStageCount > 0
            ? MoneyFormatter.Dollars(sessionCostUsd) + "?"
            : MoneyFormatter.Dollars(sessionCostUsd);

        var data = new Dictionary<string, string>
        {
            ["name"] = stage.Name,
            ["time"] = FormatDuration(cost?.DurationSeconds > 0 ? cost.DurationSeconds : elapsed.TotalSeconds),
            ["cost"] = costLabel,
            ["sessionCost"] = sessionLabel
        };
        if (!string.IsNullOrWhiteSpace(cost?.Model))
        {
            data["model"] = cost.Model;
        }
        if (cost?.Turns > 0)
        {
            data["turns"] = cost.Turns.ToString();
        }

        return _dependencies.EventSink.PublishAsync(
            new RelayEvent(DateTimeOffset.UtcNow, "info", "stage_done", runId, rootPath, taskId, stage.Number, stage.Tier, Data: data),
            cancellationToken);
    }
}
