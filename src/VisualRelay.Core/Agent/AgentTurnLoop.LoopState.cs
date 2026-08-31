using VisualRelay.Core.Llm;

namespace VisualRelay.Core.Agent;

public sealed partial class AgentTurnLoop
{
    /// <summary>
    /// Mutable bookkeeping for one run: the counters that become the report, and
    /// the resilience objects that carry state across turns.
    /// </summary>
    private sealed class LoopState(AgentLoopOptions options, DateTimeOffset startedAt)
    {
        private readonly Dictionary<string, (int Succeeded, int Failed)> _byTool =
            new(StringComparer.Ordinal);

        public RepeatCallStormBreaker Storm { get; } =
            new(options.ResilienceOptions);

        public ConsecutiveErrorGuardrail Guardrail { get; } =
            new(options.ResilienceOptions);

        public ContextCompactor Compactor { get; } =
            new(options.ContextWindow, options.ResilienceOptions);

        /// <summary>What the provider said the last call's input actually cost.</summary>
        public int LastPromptTokens { get; set; }

        public int Turn { get; set; }

        public int LlmCalls { get; set; }

        public int Retries { get; set; }

        public int GuardrailInterventions { get; set; }

        public int ArgumentRepairs { get; set; }

        public double LlmSeconds { get; set; }

        public double ToolSeconds { get; set; }

        public string? LastError { get; set; }

        public string? ServedModel { get; set; }

        private ProviderUsage Totals { get; set; } = new(0, 0);

        /// <summary>Deadline for the whole stage, from which tool budgets derive.</summary>
        public DateTimeOffset Deadline { get; } = startedAt + options.Budget;

        /// <summary>Adds one call's measured usage to the running totals.</summary>
        /// <param name="usage">The usage the provider reported.</param>
        public void AddUsage(ProviderUsage? usage)
        {
            if (usage is null) return;
            Totals = new ProviderUsage(
                Totals.PromptTokens + usage.PromptTokens,
                Totals.CompletionTokens + usage.CompletionTokens,
                Totals.CachedTokens + usage.CachedTokens,
                Totals.ReasoningTokens + usage.ReasoningTokens,
                Totals.CacheWriteTokens + usage.CacheWriteTokens);
        }

        /// <summary>Records one tool outcome against its name.</summary>
        /// <param name="tool">The tool that ran.</param>
        /// <param name="isError">Whether it failed.</param>
        public void RecordTool(string tool, bool isError)
        {
            var counts = _byTool.GetValueOrDefault(tool);
            _byTool[tool] = isError
                ? (counts.Succeeded, counts.Failed + 1)
                : (counts.Succeeded + 1, counts.Failed);
            Guardrail.Record(isError);
        }

        /// <summary>Assembles the final typed result.</summary>
        /// <param name="outcome">How the stage ended.</param>
        /// <param name="answer">The model's answer, if any.</param>
        /// <param name="error">Why it failed, if it did.</param>
        /// <param name="servedModel">The concrete model that answered.</param>
        /// <returns>The result the driver branches on.</returns>
        public AgentLoopResult Build(
            AgentLoopOutcome outcome, string answer, string? error = null, string? servedModel = null)
        {
            var stats = new AgentStats
            {
                Turns = Turn,
                ToolCallsTotal = _byTool.Values.Sum(c => c.Succeeded + c.Failed),
                ToolCallsSucceeded = _byTool.Values.Sum(c => c.Succeeded),
                ToolCallsFailed = _byTool.Values.Sum(c => c.Failed),
                ToolCallsByName = _byTool.ToDictionary(
                    kv => kv.Key,
                    kv => new ToolCallCounts(kv.Value.Succeeded, kv.Value.Failed),
                    StringComparer.Ordinal),
                Compactions = Compactor.Compactions,
                GuardrailInterventions = GuardrailInterventions,
                StormedCalls = Storm.SuppressedCount,
                RecoveredResponses = Retries,
                TruncationRepairs = ArgumentRepairs,
                LlmCalls = LlmCalls,
                TotalLlmTimeSeconds = Math.Round(LlmSeconds, 3),
                TotalToolTimeSeconds = Math.Round(ToolSeconds, 3),
                PromptTokens = Totals.PromptTokens,
                CompletionTokens = Totals.CompletionTokens,
                CachedTokens = Totals.CachedTokens,
                ReasoningTokens = Totals.ReasoningTokens,
                CacheWriteTokens = Totals.CacheWriteTokens,
            };

            return new AgentLoopResult(
                outcome, answer, stats, error ?? LastError, servedModel ?? ServedModel);
        }
    }
}
