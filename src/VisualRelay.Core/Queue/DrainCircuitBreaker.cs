using VisualRelay.Domain;

namespace VisualRelay.Core.Queue;

public sealed class DrainCircuitBreaker
{
    private const int CommitRejectThreshold = 2;
    private int _consecutiveCommitRejects;

    public bool ShouldHalt(string rootPath, RelayTaskOutcome outcome)
    {
        if (outcome.Status == RelayTaskOutcomeStatus.Flagged &&
            outcome.Reason?.StartsWith("commit rejected:", StringComparison.OrdinalIgnoreCase) == true)
        {
            _consecutiveCommitRejects++;
        }
        else
        {
            _consecutiveCommitRejects = 0;
        }

        if (_consecutiveCommitRejects < CommitRejectThreshold)
        {
            return false;
        }

        WriteMarker(rootPath, outcome);
        return true;
    }

    private static void WriteMarker(string rootPath, RelayTaskOutcome outcome)
    {
        var relayDirectory = Path.Combine(rootPath, ".relay");
        Directory.CreateDirectory(relayDirectory);
        File.WriteAllText(
            Path.Combine(relayDirectory, "DRAIN-HALTED"),
            $"commit gate rejected consecutive tasks{Environment.NewLine}last task {outcome.TaskId}{Environment.NewLine}{outcome.Reason}{Environment.NewLine}");
    }
}
