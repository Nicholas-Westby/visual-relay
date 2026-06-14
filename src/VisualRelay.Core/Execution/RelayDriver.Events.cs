using VisualRelay.Core.Costs;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
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

    private async Task<RelayTaskOutcome> FlagAsync(
        string rootPath, string runId, string taskId, string taskDirectory,
        int stageNumber, string reason, string? details,
        List<StageStatusEntry> statusEntries, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(taskDirectory);
            var body = $"{reason}\nstage {stageNumber}\n";
            if (!string.IsNullOrWhiteSpace(details))
                body += $"\n{details.Trim()}\n";

            var flaggedStage = stageNumber > 0 ? stageNumber : FindRunningStage(statusEntries);
            if (flaggedStage > 0)
            {
                foreach (var e in statusEntries.Where(e => e.Stage < flaggedStage && e.Status == "Running").ToList())
                    MarkStatus(statusEntries, e.Stage, "Done");
                MarkStatusFlagged(statusEntries, flaggedStage, reason);
                await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
            }

            await File.WriteAllTextAsync(Path.Combine(taskDirectory, "NEEDS-REVIEW"), body, cancellationToken);
            await _dependencies.EventSink.PublishAsync(new RelayEvent(
                DateTimeOffset.UtcNow, "error", "flagged", runId, rootPath, taskId, flaggedStage,
                Data: new Dictionary<string, string> { ["reason"] = reason }), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancellation must propagate — never swallow as a flag.
            throw;
        }
        catch
        {
            // Defence-in-depth: if directory/file ops fail (e.g. EMFILE),
            // still return a valid Flagged outcome carrying the original reason.
            // The NEEDS-REVIEW marker and event sink write are best-effort.
        }

        return new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Flagged, null, null, reason);
    }
}
