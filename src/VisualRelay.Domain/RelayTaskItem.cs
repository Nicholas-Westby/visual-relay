namespace VisualRelay.Domain;

public sealed record RelayTaskItem(
    string Id,
    string MarkdownPath,
    string TaskDirectory,
    bool IsNested,
    IReadOnlyList<string> SiblingPaths,
    string? ReviewReason = null,
    bool IsArchived = false,
    string? ArchiveBatch = null,
    double CostUsd = 0,
    double DurationSeconds = 0,
    int CompletedStageCount = 0,
    DateTimeOffset? CompletedAt = null)
{
    public bool NeedsReview => !string.IsNullOrWhiteSpace(ReviewReason);
    public string StateLabel => IsArchived ? "Completed" : NeedsReview ? "Needs review" : "Pending";
    private string CostLabel => CompletedStageCount == 0 ? "No cost yet" : MoneyFormatter.Dollars(CostUsd);
    private string DurationLabel => CompletedStageCount == 0 ? "No run yet" : FormatDuration(DurationSeconds);
    public string MetricsLine => CompletedStageCount == 0 ? "No run history" : $"{DurationLabel}  {CostLabel}";

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60)
        {
            return $"{Math.Max(0, seconds):0}s";
        }

        var minutes = Math.Floor(seconds / 60);
        var remainder = seconds % 60;
        return $"{minutes:0}m {remainder:00}s";
    }
}
