namespace VisualRelay.Core.Execution;

/// <summary>
/// Single owner for the NEEDS-REVIEW marker format. Every code path that writes
/// this marker must go through <see cref="WriteNeedsReviewMarkerAsync"/> so the
/// format is identical regardless of caller (driver flag, queue controller, …).
/// </summary>
public sealed partial class RelayDriver
{
    /// <summary>
    /// Writes the NEEDS-REVIEW marker in the canonical format. When
    /// <paramref name="stageNumber"/> is &gt; 0 a <c>stage N</c> line is
    /// included after the reason. An optional detail block is appended verbatim.
    /// </summary>
    internal static async Task WriteNeedsReviewMarkerAsync(
        string taskDirectory,
        string reason,
        int stageNumber,
        CancellationToken cancellationToken,
        string? details = null)
    {
        Directory.CreateDirectory(taskDirectory);
        var body = stageNumber > 0
            ? $"{reason}\nstage {stageNumber}\n"
            : reason + Environment.NewLine;

        if (!string.IsNullOrWhiteSpace(details))
            body += $"\n{details.Trim()}\n";

        await File.WriteAllTextAsync(
            Path.Combine(taskDirectory, "NEEDS-REVIEW"),
            body,
            cancellationToken);
    }
}
