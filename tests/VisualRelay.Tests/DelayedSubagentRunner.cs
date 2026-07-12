using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Decorates an <see cref="ISubagentRunner"/> and injects an artificial delay
/// for specified stage numbers before delegating to the inner runner, so
/// timing-sensitive pair-orchestration tests can control which sibling finishes
/// first in the Review (7) / Visual-review (8) pair.
/// </summary>
internal sealed class DelayedSubagentRunner(
    ISubagentRunner inner, Dictionary<int, int> stageDelaysMs) : ISubagentRunner
{
    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (stageDelaysMs.TryGetValue(invocation.Stage.Number, out var delayMs))
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), TimeProvider.System, cancellationToken);
        return await inner.RunAsync(invocation, cancellationToken);
    }
}
