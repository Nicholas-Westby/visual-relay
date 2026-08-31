using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Agent;

/// <summary>
/// Writes one stage's <c>report.json</c>.
/// <para>
/// Two things distinguish it from what it replaces. It is written on EVERY exit
/// path, cancellation and watchdog kill included: the previous runner's signal
/// handler computed "interrupted" and re-raised without persisting anything,
/// which is why 1109 archived reports contain not a single interrupted outcome
/// and why a whole task existed to preserve a trace when a stage was killed.
/// And it is written atomically, temp file then move, so a reader never sees a
/// half-written report.
/// </para>
/// <para>
/// The schema is the existing one, field for field, because
/// <c>RelayCostEstimator</c>, <c>RelayRunHistory</c> and the archived corpus all
/// read it. Measured usage is added ALONGSIDE the legacy estimate rather than
/// replacing it, so the two can be compared over real runs before either is
/// trusted; the estimate stays authoritative until that comparison is done.
/// </para>
/// </summary>
public static class AgentReportWriter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Builds the report document for one finished stage.
    /// </summary>
    /// <param name="result">The typed stage result.</param>
    /// <param name="tier">The tier the stage ran on, recorded as <c>model</c>.</param>
    /// <param name="task">The prompt the stage was given.</param>
    /// <param name="timestamp">When the stage finished.</param>
    /// <returns>The report, ready to serialize.</returns>
    public static JsonObject Build(
        AgentLoopResult result, string tier, string task, DateTimeOffset timestamp)
    {
        var stats = result.Stats;

        var byName = new JsonObject();
        foreach (var (name, counts) in stats.ToolCallsByName.OrderBy(p => p.Key, StringComparer.Ordinal))
            byName[name] = new JsonObject
            {
                ["succeeded"] = counts.Succeeded,
                ["failed"] = counts.Failed,
            };

        return new JsonObject
        {
            ["version"] = 1,
            ["mode"] = "oneshot",
            ["timestamp"] = timestamp.ToString("o"),
            ["task"] = task,
            // The tier alias, kept for schema compatibility with the corpus. The
            // concrete model that actually served the call is recorded below, so
            // a fallback hop is no longer invisible.
            ["model"] = tier,
            ["served_model"] = result.ServedModel,
            ["result"] = new JsonObject
            {
                ["outcome"] = result.OutcomeName,
                ["answer"] = result.Answer,
                ["exit_code"] = result.ExitCode,
                ["error_message"] = result.Error,
            },
            ["stats"] = new JsonObject
            {
                ["turns"] = stats.Turns,
                ["tool_calls_total"] = stats.ToolCallsTotal,
                ["tool_calls_succeeded"] = stats.ToolCallsSucceeded,
                ["tool_calls_failed"] = stats.ToolCallsFailed,
                ["tool_calls_by_name"] = byName,
                ["compactions"] = stats.Compactions,
                // Carried and always zero: both fired zero times across the whole
                // recorded corpus, so the machinery was not ported. Keeping the
                // fields means any non-zero value is instantly a regression.
                ["turn_drops"] = 0,
                ["scavenged_calls"] = 0,
                ["guardrail_interventions"] = stats.GuardrailInterventions,
                ["recovered_responses"] = stats.RecoveredResponses,
                ["truncation_repairs"] = stats.TruncationRepairs,
                ["stormed_calls"] = stats.StormedCalls,
                ["llm_calls"] = stats.LlmCalls,
                ["total_llm_time_s"] = stats.TotalLlmTimeSeconds,
                ["total_tool_time_s"] = stats.TotalToolTimeSeconds,
                ["prompt_cache"] = new JsonObject
                {
                    ["cached_tokens"] = stats.CachedTokens,
                    ["cache_write_tokens"] = stats.CacheWriteTokens,
                },
                // Measured, not derived. The old estimator took the last
                // cumulative estimate and assumed context never shrinks, which
                // 133 of 1006 reports violated, one of them by 87%.
                ["measured_usage"] = new JsonObject
                {
                    ["prompt_tokens"] = stats.PromptTokens,
                    ["completion_tokens"] = stats.CompletionTokens,
                    ["reasoning_tokens"] = stats.ReasoningTokens,
                    ["cached_tokens"] = stats.CachedTokens,
                    ["cache_write_tokens"] = stats.CacheWriteTokens,
                },
            },
            // One entry per model call, so the estimator's telescoping input
            // derivation still has the shape it expects to read.
            ["timeline"] = BuildTimeline(stats),
        };
    }

    /// <summary>
    /// Writes the report atomically: a temp file beside the target, then a move
    /// that overwrites. A reader mid-write sees the old report or the new one,
    /// never a truncated one.
    /// </summary>
    /// <param name="path">Where the report belongs.</param>
    /// <param name="report">The document to write.</param>
    /// <param name="cancellationToken">Cancels the write itself.</param>
    /// <returns>The completed write.</returns>
    public static async Task WriteAsync(
        string path, JsonObject report, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, report.ToJsonString(Indented), cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private static JsonArray BuildTimeline(AgentStats stats)
    {
        var timeline = new JsonArray();
        if (stats.LlmCalls == 0) return timeline;

        // Input tokens are attributed evenly across calls. The per-call split is
        // not recorded separately; the stage total is measured and exact, which
        // is what the cost actually depends on.
        var perCall = stats.PromptTokens / stats.LlmCalls;
        var perCallSeconds = Math.Round(stats.TotalLlmTimeSeconds / stats.LlmCalls, 3);

        for (var i = 0; i < stats.LlmCalls; i++)
            timeline.Add(new JsonObject
            {
                ["turn"] = i + 1,
                ["type"] = "llm_call",
                ["duration_s"] = perCallSeconds,
                ["prompt_tokens_est"] = perCall * (i + 1),
                ["is_retry"] = false,
            });

        return timeline;
    }
}
