namespace VisualRelay.Domain;

public sealed record StageRunMetric(
    int StageNumber,
    string StageName,
    string Tier,
    string Model,
    DateTimeOffset Timestamp,
    double DurationSeconds,
    double CostUsd,
    bool Priced,
    int PromptTokens,
    int CachedTokens,
    int OutputTokens,
    string ReportPath,
    string? TraceDirectory)
{
    public string CostLabel => MoneyFormatter.Dollars(CostUsd);
    public string DurationLabel => FormatDuration(DurationSeconds);
    public string DetailLabel => $"{DurationLabel}  {CostLabel}";

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

public sealed record TaskRunMetric(
    string TaskId,
    IReadOnlyList<StageRunMetric> Stages)
{
    public double CostUsd => Stages.Sum(stage => stage.CostUsd);
    public double DurationSeconds => Stages.Sum(stage => stage.DurationSeconds);
    public int CompletedStageCount => Stages.Count;
    public string CostLabel => CompletedStageCount == 0 ? "No cost yet" : MoneyFormatter.Dollars(CostUsd);
    public string DurationLabel => CompletedStageCount == 0 ? "No run yet" : FormatDuration(DurationSeconds);
    public string SummaryLabel => CompletedStageCount == 0
        ? "No run history"
        : $"{CompletedStageCount} {(CompletedStageCount == 1 ? "stage" : "stages")}  {DurationLabel}  {CostLabel}";

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
