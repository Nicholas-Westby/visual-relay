using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VisualRelay.Core.Costs;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    private readonly record struct ReviewEscalationResult(
        RelayTaskOutcome? Outcome,
        string Body,
        RelayCostEstimate? Cost,
        double SessionCostUsd,
        int UnknownCostStageCount);

    /// <summary>
    /// When <see cref="ReviewEscalationPolicy.ShouldEscalate"/> signals,
    /// re-runs stage 7 on the frontier tier and promotes the frontier
    /// result as the authoritative body.  Returns a flagged outcome on
    /// failure, or the updated stage state on success.
    /// </summary>
    private async Task<ReviewEscalationResult> TryEscalateReviewAsync(
        JsonElement reviewJson,
        IReadOnlyList<string> manifest,
        string rootPath,
        RelayConfig config,
        RelayStageDefinition stage,
        string currentBody,
        RelayCostEstimate? currentCost,
        double sessionCostUsd,
        int unknownCostStageCount,
        string runId,
        string taskId,
        string taskDirectory,
        RelayTaskInput input,
        StringBuilder ledger,
        string pinnedSwivalProfileContent,
        List<StageStatusEntry> statusEntries,
        CancellationToken cancellationToken)
    {
        if (!ReviewEscalationPolicy.ShouldEscalate(
                reviewJson, manifest, rootPath,
                config.ReviewEscalationManifestFileThreshold,
                config.ReviewEscalationManifestLineThreshold))
            return new(null, currentBody, currentCost, sessionCostUsd, unknownCostStageCount);

        await _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "info", "review_escalated", runId, rootPath, taskId,
            stage.Number, "frontier",
            Data: new Dictionary<string, string> { ["name"] = stage.Name }), cancellationToken);

        await _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "info", "stage_start", runId, rootPath, taskId,
            stage.Number, "frontier",
            Data: new Dictionary<string, string> { ["name"] = stage.Name }), cancellationToken);

        var escalatedInvocation = BuildInvocation(
            rootPath, runId, taskId, taskDirectory, config, stage, input,
            ledger, manifest,
            pinnedSwivalProfileContent: pinnedSwivalProfileContent)
            with
        { Tier = "frontier" };
        var escalatedStopwatch = Stopwatch.StartNew();
        var escalatedResult = await _dependencies.SubagentRunner.RunAsync(escalatedInvocation, cancellationToken);
        var escalatedCost = TryEstimateCost(escalatedInvocation.ReportFile);
        if (escalatedCost is not null) sessionCostUsd += escalatedCost.CostUsd; else unknownCostStageCount++;

        if (!escalatedResult.IsValid || string.IsNullOrWhiteSpace(escalatedResult.Json))
        {
            var outcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number,
                escalatedResult.Error ?? "invalid frontier review result",
                escalatedResult.RawText, statusEntries, cancellationToken);
            return new(outcome, currentBody, escalatedCost, sessionCostUsd, unknownCostStageCount);
        }

        escalatedStopwatch.Stop();
        var escalatedCostLabel = escalatedCost is not null
            ? MoneyFormatter.Dollars(escalatedCost.CostUsd)
            : "?";
        var escalatedSessionLabel = unknownCostStageCount > 0
            ? MoneyFormatter.Dollars(sessionCostUsd) + "?"
            : MoneyFormatter.Dollars(sessionCostUsd);
        var escalatedData = new Dictionary<string, string>
        {
            ["name"] = stage.Name,
            ["time"] = FormatDuration(escalatedCost?.DurationSeconds > 0
                ? escalatedCost.DurationSeconds
                : escalatedStopwatch.Elapsed.TotalSeconds),
            ["cost"] = escalatedCostLabel,
            ["sessionCost"] = escalatedSessionLabel
        };
        if (!string.IsNullOrWhiteSpace(escalatedCost?.Model))
            escalatedData["model"] = escalatedCost.Model;
        if (escalatedCost?.Turns > 0)
            escalatedData["turns"] = escalatedCost.Turns.ToString();

        await _dependencies.EventSink.PublishAsync(
            new RelayEvent(DateTimeOffset.UtcNow, "info", "stage_done", runId, rootPath, taskId,
                stage.Number, "frontier", Data: escalatedData), cancellationToken);

        return new(null, escalatedResult.Json, escalatedCost, sessionCostUsd, unknownCostStageCount);
    }
}
