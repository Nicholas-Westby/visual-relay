using VisualRelay.Domain;

namespace VisualRelay.Core.Agent;

public sealed partial class FirstPartySubagentRunner
{
    /// <summary>
    /// Announces that a stage's contract only parsed after repair.
    /// <para>
    /// It is a warning rather than a note: the stage keeps its work, but a model
    /// that cannot emit valid JSON for its own contract is a defect, and the run
    /// log is where that becomes visible across a drain rather than one stage at
    /// a time.
    /// </para>
    /// </summary>
    /// <param name="invocation">The stage whose contract was repaired.</param>
    /// <param name="repairs">What had to be repaired, already deduplicated.</param>
    private void AnnounceRepairs(StageInvocation invocation, IReadOnlyList<string> repairs)
    {
        if (repairs.Count == 0 || _relayEvents is null) return;

        _relayEvents.PublishAsync(
            new RelayEvent(
                _timeProvider.GetUtcNow(), "warn", "contract_repaired",
                invocation.RunId, invocation.TargetRoot, invocation.TaskName,
                invocation.Stage.Number, invocation.Tier,
                Data: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["repairs"] = string.Join(", ", repairs),
                }),
            CancellationToken.None);
    }
}
