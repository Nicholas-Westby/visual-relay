using VisualRelay.Domain;

namespace VisualRelay.Core.Queue;

public sealed class DrainCircuitBreaker
{
    private const int CommitRejectThreshold = 2;
    private const int ConsecutiveFlagThreshold = 3;
    private const string HaltMarker = "DRAIN-HALTED";
    private int _consecutiveCommitRejects;
    private int _consecutiveFlags;

    private string? HaltMessage { get; set; }

    public static void ClearHaltMarker(string rootPath)
    {
        File.Delete(Path.Combine(rootPath, ".relay", HaltMarker));
    }

    /// <summary>
    /// Reads the halt marker's reason — the file's trimmed content — or null when
    /// the root is unset, the marker is absent, or it cannot be read. Lets an
    /// observer report a halted drain without knowing where the marker lives.
    /// </summary>
    public static string? ReadHaltReason(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        try
        {
            var path = Path.Combine(rootPath, ".relay", HaltMarker);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    public bool ShouldHalt(string rootPath, RelayTaskOutcome outcome)
    {
        HaltMessage = null;
        // Ahead of every threshold: the flagged task's work exists only in the working
        // tree, and the next task would run over it.
        if (outcome is { Status: RelayTaskOutcomeStatus.Flagged, WorkUncaptured: true })
        {
            HaltMessage = $"the work of flagged task {outcome.TaskId} could not be saved, and the drain stopped "
                + "so the next task does not overwrite it: the working tree holds the only copy. Resolve the git "
                + $"error that flagged_work_capture_failed names in .relay/{outcome.TaskId}/run.log, then resume.";
            WriteMarker(rootPath, outcome, HaltMessage);
            return true;
        }

        if (outcome.Status == RelayTaskOutcomeStatus.Flagged &&
            outcome.Reason?.StartsWith("commit rejected:", StringComparison.OrdinalIgnoreCase) == true)
        {
            _consecutiveCommitRejects++;
            _consecutiveFlags++;

            if (_consecutiveCommitRejects >= CommitRejectThreshold)
            {
                HaltMessage = "commit gate rejected consecutive tasks";
                WriteMarker(rootPath, outcome, HaltMessage);
                return true;
            }

            if (_consecutiveFlags >= ConsecutiveFlagThreshold)
            {
                HaltMessage = $"drain halted after {_consecutiveFlags} consecutive flagged tasks";
                WriteMarker(rootPath, outcome, HaltMessage);
                return true;
            }

            return false;
        }

        if (outcome.Status == RelayTaskOutcomeStatus.Flagged)
        {
            _consecutiveCommitRejects = 0;
            _consecutiveFlags++;

            if (_consecutiveFlags >= ConsecutiveFlagThreshold)
            {
                HaltMessage = $"drain halted after {_consecutiveFlags} consecutive flagged tasks";
                WriteMarker(rootPath, outcome, HaltMessage);
                return true;
            }

            HaltMessage = $"task {outcome.TaskId} needs review";
            return false;
        }

        // Committed (or Failed) — reset both counters.
        _consecutiveCommitRejects = 0;
        _consecutiveFlags = 0;
        return false;
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
