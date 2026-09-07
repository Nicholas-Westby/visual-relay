using System.Text;
using VisualRelay.Core.Costs;
using VisualRelay.Core.Traces;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// Stage-invocation construction and stage recording, split out of
// RelayDriver.VerifyFix.cs to keep that file under the size guard.
public sealed partial class RelayDriver
{
    private StageInvocation BuildInvocation(
        string rootPath,
        string runId,
        string taskId,
        string taskDirectory,
        RelayConfig config,
        RelayStageDefinition stage,
        RelayTaskInput input,
        StringBuilder ledger,
        IReadOnlyList<string> manifest,
        string? lastTestOutput = null,
        string? testCommand = null,
        string? fullTestCommand = null,
        string? verifyOutputPath = null)
    {
        var boosted = config.BoostTurnsTaskIds?.Contains(taskId, StringComparer.Ordinal) == true;
        var turns = boosted ? SaturatingBoost(config.MaxTurns) : config.MaxTurns;
        var ceilingMs = boosted ? SaturatingBoost(config.SubagentTimeoutMilliseconds) : config.SubagentTimeoutMilliseconds;
        var attempt = RelayAttempt.Next(taskDirectory, stage.Number);
        // Every stage whose prompt names the section gets it, from every call site — the
        // review pair and its retry build invocations here too and used to pass nothing,
        // so Review was told to use a heading its input never contained.
        var verifyCommand = testCommand;
        if (verifyCommand is null && StageUsesVerifyCommand(stage.Number))
            verifyCommand = BuildTargetedTestCommand(config, manifest);
        // Fix-verify (11) is handed the full gate on purpose and its own prompt says so;
        // the fallback notice would misdescribe a project that does have a {files} form.
        var fullSuiteIsTargeted = stage.Number != 11
            && verifyCommand is not null
            && string.Equals(verifyCommand, config.TestCommand, StringComparison.Ordinal);
        // Only Research (2) is told to read the repo's own instruction files — every
        // other stage keeps the StageInvocation default (null/empty) and BuildPrompt
        // omits the heading. rootPath here is already the right root for the check: the
        // planning worktree during stages 1-4, the repo root otherwise.
        var repositoryInstructionFiles = stage.Number == 2
            ? RepositoryInstructionFiles.Find(rootPath)
            : null;
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
            turns,
            LastTestOutput: lastTestOutput,
            TaskContext: input.Context,
            TestCommand: verifyCommand,
            FullTestCommand: fullTestCommand,
            AbsoluteCeilingMs: ceilingMs,
            VerifyOutputPath: verifyOutputPath,
            TasksDir: config.TasksDir,
            TestCommandIsFullSuite: fullSuiteIsTargeted,
            RepositoryInstructionFiles: repositoryInstructionFiles);
    }

    /// <summary>
    /// Whether stage <paramref name="stageNumber"/>'s system prompt tells the model to
    /// use the command in <c>## Verify command</c>. Verify (10) is excluded on purpose:
    /// it is told NOT to run the suite itself.
    /// </summary>
    private static bool StageUsesVerifyCommand(int stageNumber) =>
        stageNumber is 5 or 6 or 7 or 9 or 11;

    private StageInvocation BuildStageInvocation(
        string rootPath, string runId, string taskId, string taskDirectory,
        RelayConfig config, RelayStageDefinition stage, RelayTaskInput input,
        StringBuilder ledger, IReadOnlyList<string> manifest,
        bool implementationFrontLoaded, TestRunResult? verifyTestResult)
    {
        var effectiveStage = implementationFrontLoaded && stage.Number == 6
            ? stage with { Tier = "cheap", SystemPrompt = RelayStages.ConfirmImplementationSystemPrompt } : stage;
        // The targeted command is derived inside BuildInvocation from the live manifest,
        // so it is right on a resumed run too — a threaded copy was computed before the
        // resume restored the manifest and stayed the whole suite.
        return BuildInvocation(rootPath, runId, taskId, taskDirectory, config, effectiveStage, input, ledger, manifest,
            fullTestCommand: stage.Number is 6 or 9 ? config.TestCommand : null,
            lastTestOutput: verifyTestResult?.Output);
    }

    /// <summary>
    /// Records a stage's ledger entry, seal, artifacts, status, and stage_done event.
    /// Returns the updated <paramref name="previousSeal"/> and <paramref name="taskHash"/>.
    /// </summary>
    private async Task<(string PreviousSeal, string TaskHash)> RecordStageAsync(
        string rootPath,
        string runId,
        string taskId,
        string taskDirectory,
        RelayStageDefinition stage,
        string body,
        string? check,
        RelayCostEstimate? cost,
        TimeSpan elapsed,
        StringBuilder ledger,
        List<string> seals,
        List<StageStatusEntry> statusEntries,
        IReadOnlyList<string> manifest,
        string previousSeal,
        // ReSharper disable once UnusedParameter.Local — the running task hash is
        // recomputed as the new seal (returned in .TaskHash); the prior value is
        // intentionally not read here. Kept for call-site tuple symmetry across the
        // 4 record sites: (previousSeal, taskHash) = await RecordStageAsync(…).
        string taskHash,
        double sessionCostUsd,
        int unknownCostStageCount,
        CancellationToken cancellationToken,
        double? testDurationSeconds = null,
        bool skipStatusAndPublish = false)
    {
        AppendLedgerSection(ledger, stage, body);
        var treeHash = stage.Number >= 4 ? WorkingTreeHash(rootPath, manifest) : string.Empty;
        var artifactHash = Hashing.Sha256Hex(stage.Number.ToString(), stage.Name, body);
        var seal = Hashing.Sha256Hex(previousSeal, stage.Number.ToString(), DateTimeOffset.UtcNow.ToString("O"), artifactHash, treeHash, check ?? string.Empty);
        seals.Add(SerializeSeal(stage.Number, artifactHash, treeHash, seal, check));
        await WriteArtifactsAsync(taskDirectory, taskId, ledger.ToString(), seals, cancellationToken);
        var idx = stage.Number - 1;
        var alreadySkipped = idx >= 0 && idx < statusEntries.Count
            && "Skipped".Equals(statusEntries[idx].Status, StringComparison.OrdinalIgnoreCase);
        if (!alreadySkipped)
            MarkStatusDone(statusEntries, stage, elapsed, cost, check, testDurationSeconds);
        if (!skipStatusAndPublish)
        {
            await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
            var status = idx >= 0 && idx < statusEntries.Count ? statusEntries[idx].Status : null;
            await PublishStageDoneAsync(rootPath, runId, taskId, stage, elapsed, cost, sessionCostUsd, unknownCostStageCount, cancellationToken, testDurationSeconds, status: status);
        }
        return (seal, seal);
    }
}
