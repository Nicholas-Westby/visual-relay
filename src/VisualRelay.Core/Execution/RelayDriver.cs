using System.Text;
using System.Text.Json;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed class RelayDriver : IRelayTaskRunner
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
        var config = await RelayConfigLoader.LoadAsync(rootPath, cancellationToken);
        await using var activeLock = await ActiveTaskLock.AcquireAsync(rootPath, taskId, cancellationToken);
        var runId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{taskId}";
        var taskDirectory = Path.Combine(rootPath, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);

        var repository = new RelayTaskRepository(rootPath);
        var task = (await repository.ListPendingAsync(cancellationToken)).FirstOrDefault(x => x.Id == taskId);
        var input = task is null ? new RelayTaskInput(string.Empty, null) : await repository.ReadTaskInputAsync(task, cancellationToken);
        var ledger = new StringBuilder();
        var manifest = new List<string>();
        var seals = new List<string>();
        var previousSeal = string.Empty;
        var taskHash = string.Empty;
        string? commitMessage = null;

        foreach (var stage in RelayStages.All)
        {
            await PublishAsync("info", "stage_start", rootPath, runId, taskId, stage, cancellationToken);
            string body;
            string? check = null;

            if (stage.Kind == "driver")
            {
                body = _options.CreateGitCommit ? "Committed by Visual Relay." : "Simulated commit by Visual Relay.";
            }
            else
            {
                var invocation = BuildInvocation(rootPath, taskId, taskDirectory, config, stage, input, ledger, manifest);
                var result = await _dependencies.SubagentRunner.RunAsync(invocation, cancellationToken);
                if (!result.IsValid || string.IsNullOrWhiteSpace(result.Json))
                {
                    return await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number, result.Error ?? "invalid subagent result", cancellationToken);
                }

                body = result.Json;
                var json = JsonDocument.Parse(result.Json).RootElement.Clone();
                if (stage.Number == 4)
                {
                    manifest.Clear();
                    manifest.AddRange(ReadStringArray(json, "manifest").Distinct(StringComparer.Ordinal));
                    await WriteManifestAsync(taskDirectory, manifest, cancellationToken);
                }

                if (stage.Number == 5)
                {
                    var testFiles = ReadStringArray(json, "testFiles");
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
                        return await FlagAsync(rootPath, runId, taskId, taskDirectory, 5, gateResult.Error, cancellationToken);
                    }

                    if (gateResult.RestoreResult == RedGateRestoreResult.Conflict)
                    {
                        return await FlagAsync(rootPath, runId, taskId, taskDirectory, 5, "red gate stash restore conflict", cancellationToken);
                    }

                    var testResult = gateResult.TestResult;
                    check = testResult.ExitCode == 0 ? "green" : "red";
                    if (check != "red")
                    {
                        var reason = gateResult.StashedImplementation
                            ? "author-tests passed after implementation files were stripped"
                            : "author-tests did not go red";
                        return await FlagAsync(rootPath, runId, taskId, taskDirectory, 5, reason, cancellationToken);
                    }
                }

                if (stage.Number == 9)
                {
                    var testResult = await _dependencies.TestRunner.RunAsync(rootPath, config.TestCommand, cancellationToken);
                    check = testResult.ExitCode == 0 ? "green" : "red";
                    commitMessage = ReadOptionalString(json, "commitMessage") ?? commitMessage;
                    if (check != "green")
                    {
                        return await FlagAsync(rootPath, runId, taskId, taskDirectory, 9, "verify failed", cancellationToken);
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
            await PublishAsync("info", "stage_done", rootPath, runId, taskId, stage, cancellationToken);
        }

        var commitSha = "simulated";
        if (_options.CreateGitCommit)
        {
            var proofFiles = new[]
            {
                Path.Combine(".relay", taskId, "ledger.md"),
                Path.Combine(".relay", taskId, $"{taskId}.seals"),
                Path.Combine(".relay", taskId, "manifest")
            };
            var subject = CommitMessageSanitizer.FromRawOrFallback(commitMessage, taskId);
            var commit = await GitCommitter.CommitAsync(rootPath, taskId, taskHash, subject, manifest, proofFiles, cancellationToken);
            if (!commit.Success)
            {
                return await FlagAsync(rootPath, runId, taskId, taskDirectory, 11, commit.Error ?? "git commit failed", cancellationToken);
            }

            commitSha = commit.CommitSha ?? "unknown";
        }

        return new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Committed, taskHash, commitSha, null);
    }

    private StageInvocation BuildInvocation(
        string rootPath,
        string taskId,
        string taskDirectory,
        RelayConfig config,
        RelayStageDefinition stage,
        RelayTaskInput input,
        StringBuilder ledger,
        IReadOnlyList<string> manifest)
    {
        return new StageInvocation(
            stage,
            stage.Tier,
            rootPath,
            taskId,
            input.Markdown,
            ledger.ToString(),
            manifest,
            config.LogSources,
            Path.Combine(taskDirectory, $"stage{stage.Number}-attempt1"),
            Path.Combine(taskDirectory, $"stage{stage.Number}-attempt1.report.json"),
            config.MaxTurns,
            TaskContext: input.Context);
    }

    private async Task<RelayTaskOutcome> FlagAsync(
        string rootPath,
        string runId,
        string taskId,
        string taskDirectory,
        int stage,
        string reason,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(taskDirectory);
        await File.WriteAllTextAsync(Path.Combine(taskDirectory, "NEEDS-REVIEW"), $"{reason}{Environment.NewLine}stage {stage}{Environment.NewLine}", cancellationToken);
        await _dependencies.EventSink.PublishAsync(new RelayEvent(DateTimeOffset.UtcNow, "error", "flagged", runId, rootPath, taskId, stage, Data: new Dictionary<string, string> { ["reason"] = reason }), cancellationToken);
        return new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Flagged, null, null, reason);
    }

    private static async Task WriteManifestAsync(string taskDirectory, IReadOnlyList<string> manifest, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            Path.Combine(taskDirectory, "manifest"),
            string.Join(Environment.NewLine, manifest) + Environment.NewLine,
            cancellationToken);
    }

    private static async Task WriteArtifactsAsync(
        string taskDirectory,
        string taskId,
        string ledger,
        IReadOnlyList<string> seals,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(taskDirectory, "ledger.md"), ledger, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(taskDirectory, $"{taskId}.seals"), string.Join(Environment.NewLine, seals) + Environment.NewLine, cancellationToken);
    }

    private static void AppendLedgerSection(StringBuilder ledger, RelayStageDefinition stage, string body)
    {
        ledger.AppendLine($"## Stage {stage.Number} - {stage.Name}");
        ledger.AppendLine();
        ledger.AppendLine(body);
        ledger.AppendLine();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement json, string propertyName)
    {
        if (!json.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Select(x => x.GetString() ?? string.Empty)
            .Where(x => x.Length > 0)
            .ToArray();
    }

    private static string? ReadOptionalString(JsonElement json, string propertyName) =>
        json.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string WorkingTreeHash(string rootPath, IReadOnlyList<string> manifest)
    {
        var parts = new List<string>();
        foreach (var relative in manifest.Order(StringComparer.Ordinal))
        {
            var fullPath = Path.Combine(rootPath, relative);
            parts.Add(relative);
            parts.Add(File.Exists(fullPath) ? File.ReadAllText(fullPath) : string.Empty);
        }

        return Hashing.Sha256Hex(parts.ToArray());
    }

    private static string SerializeSeal(int stageNumber, string artifactHash, string treeHash, string seal, string? check)
    {
        var payload = new Dictionary<string, object?>
        {
            ["kind"] = "stage",
            ["n"] = stageNumber,
            ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
            ["artifactHash"] = artifactHash,
            ["treeHash"] = treeHash,
            ["seal"] = seal
        };
        if (check is not null)
        {
            payload["check"] = check;
        }

        return JsonSerializer.Serialize(payload);
    }

    private Task PublishAsync(
        string level,
        string eventName,
        string rootPath,
        string runId,
        string taskId,
        RelayStageDefinition stage,
        CancellationToken cancellationToken) =>
        _dependencies.EventSink.PublishAsync(
            new RelayEvent(DateTimeOffset.UtcNow, level, eventName, runId, rootPath, taskId, stage.Number, stage.Tier),
            cancellationToken);
}
