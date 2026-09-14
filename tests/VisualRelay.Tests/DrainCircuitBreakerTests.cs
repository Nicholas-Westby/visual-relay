using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A flagged task whose work could not be captured has left the only copy of that work
/// in the working tree. The next task in the drain would run over it, so the breaker
/// stops the drain at once, whatever its thresholds say.
/// </summary>
public sealed class DrainCircuitBreakerTests
{
    [Fact]
    public void AFlaggedTaskWhoseWorkWasNotCaptured_HaltsAtOnce_WithAMarkerThatSaysWhatToDo()
    {
        using var repo = TestRepository.Create();
        // The measured case: the first commit rejection, which on its own never halts.
        var outcome = new RelayTaskOutcome("alpha", RelayTaskOutcomeStatus.Flagged, null, null,
            "commit rejected: (git exit 128): Author identity unknown") { WorkUncaptured = true };

        Assert.True(new DrainCircuitBreaker().ShouldHalt(repo.Root, outcome));

        var reason = DrainCircuitBreaker.ReadHaltReason(repo.Root);
        Assert.NotNull(reason);
        Assert.Contains("alpha", reason, StringComparison.Ordinal);
        Assert.Contains("could not be saved", reason, StringComparison.Ordinal);
        Assert.Contains("so the next task does not overwrite it", reason, StringComparison.Ordinal);
        Assert.Contains("run.log", reason, StringComparison.Ordinal);
        Assert.Contains("resume", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlaggedTaskWhoseWorkWasCaptured_KeepsTheUsualThresholds()
    {
        using var repo = TestRepository.Create();
        var outcome = new RelayTaskOutcome("alpha", RelayTaskOutcomeStatus.Flagged, null, null,
            "commit rejected: (git exit 128): Author identity unknown");

        Assert.False(new DrainCircuitBreaker().ShouldHalt(repo.Root, outcome));
        Assert.Null(DrainCircuitBreaker.ReadHaltReason(repo.Root));
    }
}
