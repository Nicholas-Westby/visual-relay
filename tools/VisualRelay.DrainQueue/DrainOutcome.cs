using VisualRelay.Domain;

namespace VisualRelay.DrainQueue;

/// <summary>
/// Exit-code mapping, outcome-line formatting, and summary computation
/// for the drain-queue tool.  Deliberately kept as small pure functions
/// so every branch is trivially unit-testable.
/// </summary>
public static class DrainOutcome
{
    /// <summary>Summary counts after a drain completes.</summary>
    public sealed record Summary(int Committed, int Flagged, int Failed, int Planned);

    /// <summary>Message printed when the queue is empty.</summary>
    public const string NothingPendingMessage = "Nothing pending — queue is empty.";

    /// <summary>
    /// Computes the process exit code from the drain results.
    /// Returns 0 when every outcome is Committed (or the list is empty);
    /// returns 2 when any outcome is Flagged or Failed.
    /// </summary>
    public static int GetExitCode(IReadOnlyList<RelayTaskOutcome> outcomes)
    {
        foreach (var outcome in outcomes)
        {
            if (outcome.Status is RelayTaskOutcomeStatus.Flagged or RelayTaskOutcomeStatus.Failed)
                return 2;
        }

        return 0;
    }

    /// <summary>
    /// Formats a single per-task outcome line matching the RunTask format:
    /// <c>Status: taskId sha-or-reason</c>.
    /// </summary>
    public static string FormatOutcomeLine(RelayTaskOutcome outcome)
    {
        var status = outcome.Status.ToString();
        var detail = outcome.Status == RelayTaskOutcomeStatus.Committed
            ? outcome.CommitSha
            : outcome.Reason;

        if (detail is null)
            return $"{status}: {outcome.TaskId}";

        return $"{status}: {outcome.TaskId} {detail}";
    }

    /// <summary>
    /// Formats the final one-line summary:
    /// <c>Committed: N  Flagged: N  Failed: N  Planned: N</c>.
    /// </summary>
    public static string FormatSummary(Summary summary)
    {
        return $"Committed: {summary.Committed}  Flagged: {summary.Flagged}  Failed: {summary.Failed}  Planned: {summary.Planned}";
    }

    /// <summary>
    /// Computes a <see cref="Summary"/> from a list of outcomes.
    /// </summary>
    public static Summary ComputeSummary(IReadOnlyList<RelayTaskOutcome> outcomes)
    {
        var committed = 0;
        var flagged = 0;
        var failed = 0;
        var planned = 0;

        foreach (var outcome in outcomes)
        {
            switch (outcome.Status)
            {
                case RelayTaskOutcomeStatus.Committed:
                    committed++;
                    break;
                case RelayTaskOutcomeStatus.Flagged:
                    flagged++;
                    break;
                case RelayTaskOutcomeStatus.Failed:
                    failed++;
                    break;
                case RelayTaskOutcomeStatus.Planned:
                    planned++;
                    break;
            }
        }

        return new Summary(committed, flagged, failed, planned);
    }
}
