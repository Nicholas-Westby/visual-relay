using System.Diagnostics;
using System.Text;
using VisualRelay.Core.Costs;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Review-pair retry escalation helper and shared types, split from
/// <c>RelayDriver.ReviewPair.cs</c> to keep that file under the 300-line guard.
/// </summary>
public sealed partial class RelayDriver
{
    /// <summary>
    /// Builds an enriched flag reason for a review-pair stage that failed.
    /// When <paramref name="result"/> carries a <see cref="KillSignature"/>, returns a
    /// descriptive string with the kill reason, last signal, elapsed wall time, and a
    /// relative path to the autopsy artifact. Otherwise it names the runner's own error
    /// — the contract-parser message saying WHAT was wrong, which every per-stage flag
    /// outside this pair already carries — falling back to the generic line without one.
    /// </summary>
    private static string BuildReviewFlagReason(string stageName, StageRunResult result, string rootPath)
    {
        if (result.Kill is null)
            return string.IsNullOrWhiteSpace(result.Error)
                ? $"{stageName} returned an invalid result"
                : $"{stageName} returned an invalid result: {result.Error}";

        var kill = result.Kill;
        var mins = (int)result.Elapsed.TotalMinutes;
        var secs = result.Elapsed.Seconds;
        var reason = $"{stageName} stall-killed ({kill.Reason}, lastSignal={kill.LastSignal}) after {mins}m {secs:D2}s";
        if (kill.AutopsyPath is not null)
        {
            try
            {
                var relative = Path.GetRelativePath(rootPath, kill.AutopsyPath);
                reason += $" — see {relative}";
            }
            catch
            {
                // Best-effort relativization; skip path on failure.
            }
        }
        return reason;
    }

    private sealed record TriageResult(string VisualReview, string Reason);

    private sealed record StageRunResult(
        string Body, string? Check, double CostUsd, bool CostUnknown,
        TimeSpan Elapsed, double? TestDurationSeconds,
        KillSignature? Kill = null,
        // What the runner said went wrong, when anything did. Carried so the pair's
        // flag reason can name it instead of the generic "invalid result".
        string? Error = null);

    private sealed record RenderOutput(IReadOnlyList<string> PngPaths, string? ErrorOutput);

    /// <summary>
    /// Called when a review-pair stage (7 Review or 8 Visual-review) produced a
    /// red result due to a watchdog kill. Runs ONE escalated retry attempt
    /// (tier bump + scaled budget) and returns the outcome. The original
    /// kill signature is preserved so the caller can build an enriched
    /// flag reason.
    /// </summary>
    private async Task<StageRunResult> RunReviewPairStageWithRetryAsync(
        string rootPath, string runId, string taskId, string taskDirectory,
        RelayConfig config, RelayStageDefinition stage, RelayTaskInput input,
        StringBuilder ledger, IReadOnlyList<string> manifest, KillSignature? originalKill,
        CancellationToken cancellationToken)
    {
        var boosted = config.BoostTurnsTaskIds?.Contains(taskId, StringComparer.Ordinal) == true;
        var baseTurns = boosted ? SaturatingBoost(config.MaxTurns) : config.MaxTurns;
        var baseCeilingMs = boosted ? SaturatingBoost(config.SubagentTimeoutMilliseconds) : config.SubagentTimeoutMilliseconds;

        // attempt 2: one escalation rung
        const int run = 2;
        var fromTier = stage.Tier;
        var toTier = StageEscalation.NextTier(fromTier);
        var toTurns = StageEscalation.TurnsForRun(baseTurns, run, boosted);
        var toCeilingMs = StageEscalation.Scale(baseCeilingMs, StageEscalation.RunMultiplier(run, boosted));

        // No-repeat exhaustion: never re-run identical config. Preserve the
        // original kill signature so the caller can still build an enriched
        // flag reason (otherwise it drops to the generic string).
        if (toTier == fromTier && toTurns == baseTurns)
            return new StageRunResult(string.Empty, "red", 0d, true, TimeSpan.Zero, null, Kill: originalKill);

        await PublishStageEscalatedAsync(rootPath, runId, taskId, stage, run, /*maxRuns*/ 2,
            fromTier, toTier, baseTurns, toTurns, cancellationToken);

        var invocation = BuildInvocation(rootPath, runId, taskId, taskDirectory,
            config, stage, input, ledger, manifest);
        invocation = invocation with
        {
            Tier = toTier,
            MaxTurns = toTurns,
            AbsoluteCeilingMs = toCeilingMs
        };

        return await RunStageAsync(invocation, stage, taskDirectory, cancellationToken);
    }

    private async Task<StageRunResult> RunSingleStageAsync(
        string rootPath, string runId, string taskId, string taskDirectory,
        RelayConfig config, RelayStageDefinition stage, RelayTaskInput input,
        StringBuilder ledger, IReadOnlyList<string> manifest, CancellationToken cancellationToken)
    {
        var invocation = BuildInvocation(rootPath, runId, taskId, taskDirectory,
            config, stage, input, ledger, manifest);
        return await RunStageAsync(invocation, stage, taskDirectory, cancellationToken);
    }

    private async Task<StageRunResult> RunStageAsync(
        StageInvocation invocation, RelayStageDefinition stage, string taskDirectory,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await _dependencies.SubagentRunner.RunAsync(invocation, cancellationToken);
        stopwatch.Stop();
        var cost = EstimateStageCostCumulative(taskDirectory, stage.Number);
        var costUsd = cost?.CostUsd ?? 0d;
        var costUnknown = cost is null;

        if (!result.IsValid || string.IsNullOrWhiteSpace(result.Json))
        {
            return new StageRunResult(
                result.RawText,
                "red", costUsd, costUnknown, stopwatch.Elapsed, null,
                Kill: result.Kill, Error: result.Error);
        }

        return new StageRunResult(result.Json, null, costUsd, costUnknown, stopwatch.Elapsed, null);
    }

    private async Task<(string PreviousSeal, string TaskHash)> RecordPairStageAsync(
        string rootPath, string runId, string taskId, string taskDirectory,
        RelayStageDefinition stage, StageRunResult runResult,
        StringBuilder ledger, List<string> seals,
        List<StageStatusEntry> statusEntries, IReadOnlyList<string> manifest,
        string previousSeal, string taskHash, double sessionCostUsd,
        int unknownCostStageCount, CancellationToken cancellationToken)
    {
        // The stage report carries the concrete model, token counts, and turn count.
        // Hand-building the estimate without them left Review and Visual-review as the
        // only stages in status.json with no model and no turns, so their cards could
        // never show the model and per-model token attribution dropped them. Cost and
        // duration stay the pair path's own (it accrues the session total from
        // runResult), so run totals are unchanged.
        var reported = EstimateStageCostCumulative(taskDirectory, stage.Number);
        var cost = runResult.CostUnknown ? null
            : new RelayCostEstimate(
                reported?.Model ?? "", runResult.CostUsd, true,
                reported?.PromptTokens ?? 0, reported?.CachedTokens ?? 0,
                reported?.OutputTokens ?? 0, runResult.Elapsed.TotalSeconds,
                reported?.CacheWriteTokens ?? 0, reported?.Turns ?? 0);
        // Visual-review (8) may report "unassessable" — the subject never appeared in
        // the render. That is neither a clean pass nor a defect, so it gets its own
        // seal/status check plus a Run Log warning; without it the stage would read as
        // a green visual review in status.json and on the queue card.
        var check = runResult.Check;
        if (check is null && stage.Number == 8 && IsUnassessableVisualVerdict(runResult.Body))
        {
            check = "unassessable";
            await _dependencies.EventSink.PublishAsync(new RelayEvent(
                DateTimeOffset.UtcNow, "warn", "visual_unassessable", runId, rootPath, taskId,
                stage.Number, stage.Tier, Data: new Dictionary<string, string>
                {
                    ["reason"] = "the task's subject did not appear in the supplied renders"
                }), cancellationToken);
        }
        return await RecordStageAsync(rootPath, runId, taskId, taskDirectory,
            stage, runResult.Body, check, cost,
            runResult.Elapsed, ledger, seals, statusEntries, manifest,
            previousSeal, taskHash, sessionCostUsd, unknownCostStageCount,
            cancellationToken, runResult.TestDurationSeconds);
    }
}
