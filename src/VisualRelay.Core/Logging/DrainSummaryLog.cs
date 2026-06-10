namespace VisualRelay.Core.Logging;

/// <summary>
/// Appends high-level per-task drain milestones to a drain-level summary log
/// so there is one place to watch overall drain progress without grepping
/// per-task run.log files. Each line is a single milestone with a phase marker.
/// </summary>
public static class DrainSummaryLog
{
    private static readonly object Gate = new();

    /// <summary>
    /// Writes a single milestone line to the drain summary log.
    /// The path is <c>.relay/&lt;runId&gt;.log</c> under <paramref name="rootPath"/>.
    /// </summary>
    public static void Write(
        string rootPath,
        string runId,
        string taskId,
        string phase,  // "plan" or "execute"
        string milestone, // e.g. "start", "done(stage4)", "flagged", "committed"
        string? detail = null)
    {
        var dir = Path.Combine(rootPath, ".relay");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{runId}.log");

        var line = $"{DateTimeOffset.UtcNow:O} {phase,-7} {taskId} {milestone}";
        if (!string.IsNullOrWhiteSpace(detail))
            line += $" ({detail})";
        line += Environment.NewLine;

        lock (Gate)
        {
            File.AppendAllText(path, line);
        }
    }
}
