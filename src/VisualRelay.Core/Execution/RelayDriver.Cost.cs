using VisualRelay.Core.Costs;
using VisualRelay.Core.Traces;

namespace VisualRelay.Core.Execution;

// Per-stage CUMULATIVE cost/turn estimation, split out of RelayDriver.Artifacts.cs
// to keep that file under the size guard.
public sealed partial class RelayDriver
{
    /// <summary>
    /// The CUMULATIVE cost estimate for a stage: every <c>stage{n}-attempt*.report.json</c>
    /// in <paramref name="taskDirectory"/>, summed the SAME way
    /// <see cref="VisualRelay.Core.Tasks.RelayRunHistory"/>'s SquashAttempts squashes the
    /// archived metric. A stage that escalates IN-PROCESS writes one report per escalation
    /// run, so reading only the start attempt under-counts; folding every attempt makes the
    /// single live <c>stage_done</c> (and the session-cost accrual it feeds) equal the
    /// archived squash by construction. Cost/duration/turns/token fields sum across attempts;
    /// <see cref="RelayCostEstimate.Model"/> is the latest attempt's; <see cref="RelayCostEstimate.Priced"/>
    /// is true only when EVERY attempt priced. Returns null when no attempt produced a
    /// parseable report — the stage then counts as unknown-cost, exactly as a single missing
    /// report did before.
    /// </summary>
    private static RelayCostEstimate? EstimateStageCostCumulative(string taskDirectory, int stageNumber)
    {
        if (!Directory.Exists(taskDirectory))
        {
            return null;
        }

        var attempts = Directory
            .EnumerateFiles(taskDirectory, $"stage{stageNumber}-attempt*.report.json")
            .Select(path => (path, estimate: TryEstimateCost(path)))
            .Where(entry => entry.estimate is not null)
            .OrderBy(entry => RelayAttempt.AttemptNumber(Path.GetFileName(entry.path)))
            .Select(entry => entry.estimate!)
            .ToArray();
        if (attempts.Length == 0)
        {
            return null;
        }

        return attempts[^1] with
        {
            CostUsd = attempts.Sum(a => a.CostUsd),
            DurationSeconds = attempts.Sum(a => a.DurationSeconds),
            PromptTokens = attempts.Sum(a => a.PromptTokens),
            CachedTokens = attempts.Sum(a => a.CachedTokens),
            OutputTokens = attempts.Sum(a => a.OutputTokens),
            CacheWriteTokens = attempts.Sum(a => a.CacheWriteTokens),
            Turns = attempts.Sum(a => a.Turns),
            Priced = attempts.All(a => a.Priced)
        };
    }
}
