using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VisualRelay.Core.Costs;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Traces;
using VisualRelay.Domain;

namespace VisualRelay.Core.Tasks;

public static partial class RelayRunHistory
{
    public static TaskRunMetric ReadTaskMetric(string rootPath, string taskId)
    {
        var taskDirectory = Path.Combine(rootPath, ".relay", taskId);
        if (!Directory.Exists(taskDirectory))
        {
            return new TaskRunMetric(taskId, []);
        }

        var stages = Directory.EnumerateFiles(taskDirectory, "stage*-attempt*.report.json")
            .Select(ReadStageMetric)
            .Where(metric => metric is not null)
            .Cast<StageRunMetric>()
            .GroupBy(metric => metric.StageNumber)
            .Select(SquashAttempts)
            .OrderBy(metric => metric.StageNumber)
            .ToArray();
        return new TaskRunMetric(taskId, stages);
    }

    public static IReadOnlyList<RelayEvent> ReadTaskEvents(string rootPath, string taskId)
    {
        return ReadTaskMetric(rootPath, taskId).Stages
            .OrderByDescending(stage => stage.StageNumber)
            .Select(stage => new RelayEvent(
                stage.Timestamp,
                stage.Priced ? "info" : "warn",
                "stage_report",
                "history",
                rootPath,
                taskId,
                stage.StageNumber,
                stage.Tier,
                Data: BuildStageReportData(stage)))
            .ToArray();
    }

    private static Dictionary<string, string> BuildStageReportData(StageRunMetric stage)
    {
        var data = new Dictionary<string, string>
        {
            ["name"] = stage.StageName,
            ["model"] = stage.Model,
            ["time"] = stage.DurationLabel,
            // Squashed (summed-across-attempts) duration, mirroring the live
            // stage_done event so both carry the numeric value consistently.
            ["timeSeconds"] = stage.DurationSeconds.ToString(CultureInfo.InvariantCulture),
            ["cost"] = stage.CostLabel
        };
        if (stage.Turns > 0)
        {
            data["turns"] = stage.Turns.ToString();
        }
        return data;
    }

    public static async Task<IReadOnlyList<TraceEntry>> ReadTraceEntriesAsync(
        string rootPath,
        string taskId,
        int? stageNumber = null,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<TraceEntry>();
        foreach (var file in RelayTraceLocator.FindTraceFiles(rootPath, taskId, stageNumber))
        {
            var stage = RelayAttempt.StageNumber(file);
            var text = await File.ReadAllTextAsync(file, cancellationToken);
            entries.AddRange(RelayTraceParser.Parse(text).Select(entry => entry with { StageNumber = stage }));
        }

        return entries;
    }

    private static StageRunMetric? ReadStageMetric(string reportPath)
    {
        var match = ReportNameRegex().Match(Path.GetFileName(reportPath));
        if (!match.Success)
        {
            return null;
        }

        var stageNumber = int.Parse(match.Groups[1].Value);
        var stage = RelayStages.All.FirstOrDefault(item => item.Number == stageNumber);
        var estimate = RelayCostEstimator.EstimateReport(reportPath);
        var timestamp = ReadTimestamp(reportPath);
        var traceDirectory = Path.Combine(
            Path.GetDirectoryName(reportPath)!,
            Path.GetFileName(reportPath).Replace(".report.json", "", StringComparison.Ordinal));
        return new StageRunMetric(
            stageNumber,
            stage?.Name ?? $"Stage {stageNumber}",
            stage?.Tier ?? string.Empty,
            estimate.Model,
            timestamp,
            estimate.DurationSeconds,
            estimate.CostUsd,
            estimate.Priced,
            estimate.PromptTokens,
            estimate.CachedTokens,
            estimate.OutputTokens,
            estimate.CacheWriteTokens,
            reportPath,
            Directory.Exists(traceDirectory) ? traceDirectory : null,
            estimate.Turns);
    }

    /// <summary>
    /// Reads the per-stage status record written by the driver.
    /// This is the single source of truth for stage status (waiting/running/done/flagged).
    /// </summary>
    public static IReadOnlyList<StageStatusEntry> ReadStatusRecord(string rootPath, string taskId)
    {
        var taskDirectory = Path.Combine(rootPath, ".relay", taskId);
        return StageStatusRecord.Read(taskDirectory);
    }

    private static StageRunMetric SquashAttempts(IGrouping<int, StageRunMetric> attempts)
    {
        // Order by the parsed attempt index, not the report path string: an ordinal sort ranks
        // "attempt10" before "attempt2", which would pick a stale attempt as the latest outcome.
        var ordered = attempts.OrderBy(metric => RelayAttempt.AttemptNumber(Path.GetFileName(metric.ReportPath))).ToArray();
        var latest = ordered[^1];
        return latest with
        {
            DurationSeconds = ordered.Sum(metric => metric.DurationSeconds),
            CostUsd = ordered.Sum(metric => metric.CostUsd),
            PromptTokens = ordered.Sum(metric => metric.PromptTokens),
            CachedTokens = ordered.Sum(metric => metric.CachedTokens),
            OutputTokens = ordered.Sum(metric => metric.OutputTokens),
            CacheWriteTokens = ordered.Sum(metric => metric.CacheWriteTokens),
            Turns = ordered.Sum(metric => metric.Turns),
            Priced = ordered.All(metric => metric.Priced)
        };
    }

    private static DateTimeOffset ReadTimestamp(string reportPath)
    {
        try
        {
            using var stream = File.OpenRead(reportPath);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("timestamp", out var timestamp) &&
                DateTimeOffset.TryParse(timestamp.GetString(), out var parsed))
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
        }

        return File.GetLastWriteTimeUtc(reportPath);
    }

    [GeneratedRegex(@"^stage(\d+)-attempt\d+\.report\.json$")]
    private static partial Regex ReportNameRegex();
}
