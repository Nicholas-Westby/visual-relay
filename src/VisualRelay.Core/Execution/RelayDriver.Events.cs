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
}
