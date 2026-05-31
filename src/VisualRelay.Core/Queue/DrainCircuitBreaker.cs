using VisualRelay.Domain;

namespace VisualRelay.Core.Queue;

public sealed class DrainCircuitBreaker
{
    private const int CommitRejectThreshold = 2;
    public const string HaltMarker = "DRAIN-HALTED";
    private int _consecutiveCommitRejects;

    public string? HaltMessage { get; private set; }

    public static void ClearHaltMarker(string rootPath)
    {
        File.Delete(Path.Combine(rootPath, ".relay", HaltMarker));
    }

    public bool ShouldHalt(string rootPath, RelayTaskOutcome outcome)
    {
        HaltMessage = null;
        if (outcome.Status == RelayTaskOutcomeStatus.Flagged &&
            outcome.Reason?.StartsWith("commit rejected:", StringComparison.OrdinalIgnoreCase) == true)
        {
            _consecutiveCommitRejects++;
        }
        else if (outcome.Status == RelayTaskOutcomeStatus.Flagged)
        {
            _consecutiveCommitRejects = 0;
            HaltMessage = $"task {outcome.TaskId} needs review";
            WriteMarker(rootPath, outcome, HaltMessage);
            return true;
        }
        else
        {
            _consecutiveCommitRejects = 0;
        }

        if (_consecutiveCommitRejects < CommitRejectThreshold)
        {
            return false;
        }

        HaltMessage = "commit gate rejected consecutive tasks";
        WriteMarker(rootPath, outcome, HaltMessage);
        return true;
    }

    private static void WriteMarker(string rootPath, RelayTaskOutcome outcome, string message)
    {
        var relayDirectory = Path.Combine(rootPath, ".relay");
        Directory.CreateDirectory(relayDirectory);
        File.WriteAllText(
            Path.Combine(relayDirectory, HaltMarker),
            $"{message}{Environment.NewLine}last task {outcome.TaskId}{Environment.NewLine}{outcome.Reason}{Environment.NewLine}");
    }
}
