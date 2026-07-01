using System.Diagnostics;
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
        string? pinnedSwivalProfileContent = null,
        string? verifyOutputPath = null)
    {
        var boosted = config.BoostTurnsTaskIds?.Contains(taskId, StringComparer.Ordinal) == true;
        var turns = boosted ? SaturatingBoost(config.MaxTurns) : config.MaxTurns;
        var ceilingMs = boosted ? SaturatingBoost(config.SubagentTimeoutMilliseconds) : config.SubagentTimeoutMilliseconds;
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
            turns,
            LastTestOutput: lastTestOutput,
            TaskContext: input.Context,
            TestCommand: testCommand,
            FullTestCommand: fullTestCommand,
            PinnedSwivalProfileContent: pinnedSwivalProfileContent,
            AbsoluteCeilingMs: ceilingMs,
            VerifyOutputPath: verifyOutputPath,
            IsTurnBoosted: boosted);
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
        Stopwatch stopwatch,
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
        double? testDurationSeconds = null)
    {
        AppendLedgerSection(ledger, stage, body);
        var treeHash = stage.Number >= 4 ? WorkingTreeHash(rootPath, manifest) : string.Empty;
        var artifactHash = Hashing.Sha256Hex(stage.Number.ToString(), stage.Name, body);
        var seal = Hashing.Sha256Hex(previousSeal, stage.Number.ToString(), DateTimeOffset.UtcNow.ToString("O"), artifactHash, treeHash, check ?? string.Empty);
        seals.Add(SerializeSeal(stage.Number, artifactHash, treeHash, seal, check));
        await WriteArtifactsAsync(taskDirectory, taskId, ledger.ToString(), seals, cancellationToken);
        stopwatch.Stop();
        MarkStatusDone(statusEntries, stage, stopwatch.Elapsed, cost, check, testDurationSeconds);
        await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
        await PublishStageDoneAsync(rootPath, runId, taskId, stage, stopwatch.Elapsed, cost, sessionCostUsd, unknownCostStageCount, cancellationToken, testDurationSeconds);
        return (seal, seal);
    }
}
