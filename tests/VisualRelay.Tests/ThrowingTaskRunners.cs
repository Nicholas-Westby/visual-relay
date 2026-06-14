using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Throws <see cref="InvalidOperationException"/> on the first call to
/// <see cref="RunTaskAsync"/>, then returns <see cref="RelayTaskOutcomeStatus.Committed"/>
/// for all subsequent calls.  Simulates an unhandled exception from
/// <c>RunTaskAsync</c> (e.g. a secondary IOException inside <c>FlagAsync</c>).
/// </summary>
internal sealed class ThrowingThenCommittedTaskRunner : IRelayTaskRunner
{
    private int _callCount;
    public List<string> TasksRun { get; } = [];

    public Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken cancellationToken = default)
    {
        _callCount++;
        TasksRun.Add(taskId);
        if (_callCount == 1)
            throw new InvalidOperationException("Simulated FlagAsync failure");
        return Task.FromResult(new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Committed, "hash", "sha", null));
    }
}

/// <summary>
/// Throws <see cref="InvalidOperationException"/> on every call to
/// <see cref="RunTaskAsync"/>.  Used to verify the circuit breaker still
/// triggers after consecutive unhandled-exception flags.
/// </summary>
internal sealed class AlwaysThrowingTaskRunner : IRelayTaskRunner
{
    public List<string> TasksRun { get; } = [];

    public Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken cancellationToken = default)
    {
        TasksRun.Add(taskId);
        throw new InvalidOperationException("Simulated FlagAsync failure");
    }
}
