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
    int CacheWriteTokens,
    string ReportPath,
    string? TraceDirectory,
    int Turns = 0)
{
    public string CostLabel => Priced ? MoneyFormatter.Dollars(CostUsd) : "?";
    public string DurationLabel => FormatDuration(DurationSeconds);

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
    // ReSharper disable once NotAccessedPositionalProperty.Global — identifies which
    // task this metric belongs to; part of the record's shape and set at both
    // construction sites, though current consumers read only Stages.
    string TaskId,
    IReadOnlyList<StageRunMetric> Stages)
{
    public double CostUsd => Stages.Sum(stage => stage.CostUsd);
    public double DurationSeconds => Stages.Sum(stage => stage.DurationSeconds);
    public int CompletedStageCount => Stages.Count;
    private string CostLabel => CompletedStageCount == 0
        ? "No cost yet"
        : Stages.All(s => s.Priced)
            ? MoneyFormatter.Dollars(CostUsd)
            : "?";
    private string DurationLabel => CompletedStageCount == 0 ? "No run yet" : FormatDuration(DurationSeconds);
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
