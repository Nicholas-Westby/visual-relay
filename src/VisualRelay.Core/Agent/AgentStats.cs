namespace VisualRelay.Core.Agent;

/// <summary>Per-tool success and failure counts.</summary>
/// <param name="Succeeded">Calls that returned a result.</param>
/// <param name="Failed">Calls that returned an error.</param>
public sealed record ToolCallCounts(int Succeeded, int Failed);

/// <summary>
/// The counters one stage reports. The field names mirror the existing
/// <c>report.json</c> schema exactly, because <c>RelayCostEstimator</c>,
/// <c>RelayRunHistory</c> and 1109 archived reports all read them. Changing a
/// name here silently breaks the history, so the shape is fixed even where a
/// tidier one exists.
/// <para>
/// Two counters are carried and always zero: <c>turn_drops</c> and
/// <c>scavenged_calls</c> fired zero times across the entire recorded corpus,
/// so the machinery behind them was deliberately not ported. They stay in the
/// schema so old and new reports remain comparable, and so any non-zero value
/// is instantly recognisable as a regression.
/// </para>
/// </summary>
public sealed record AgentStats
{
    /// <summary>Model turns taken.</summary>
    public int Turns { get; init; }

    /// <summary>Tool calls attempted.</summary>
    public int ToolCallsTotal { get; init; }

    /// <summary>Tool calls that returned a result.</summary>
    public int ToolCallsSucceeded { get; init; }

    /// <summary>Tool calls that returned an error.</summary>
    public int ToolCallsFailed { get; init; }

    /// <summary>Per-tool breakdown, keyed by tool name.</summary>
    public IReadOnlyDictionary<string, ToolCallCounts> ToolCallsByName { get; init; } =
        new Dictionary<string, ToolCallCounts>(StringComparer.Ordinal);

    /// <summary>Times the context was compacted.</summary>
    public int Compactions { get; init; }

    /// <summary>Times the consecutive-error guardrail intervened.</summary>
    public int GuardrailInterventions { get; init; }

    /// <summary>Calls refused by the repeat-call storm breaker.</summary>
    public int StormedCalls { get; init; }

    /// <summary>Responses recovered after a retryable failure.</summary>
    public int RecoveredResponses { get; init; }

    /// <summary>Tool arguments repaired before dispatch.</summary>
    public int TruncationRepairs { get; init; }

    /// <summary>Model calls made, including retries.</summary>
    public int LlmCalls { get; init; }

    /// <summary>Seconds spent waiting on the model.</summary>
    public double TotalLlmTimeSeconds { get; init; }

    /// <summary>Seconds spent running tools.</summary>
    public double TotalToolTimeSeconds { get; init; }

    /// <summary>Input tokens served from the provider's cache.</summary>
    public int CachedTokens { get; init; }

    /// <summary>Input tokens written to the provider's cache.</summary>
    public int CacheWriteTokens { get; init; }

    /// <summary>
    /// Measured input tokens, summed across calls. Replaces the old derivation
    /// that took the last cumulative estimate and assumed context never shrinks,
    /// an assumption 133 of 1006 reports violated.
    /// </summary>
    public int PromptTokens { get; init; }

    /// <summary>
    /// Each call's measured input tokens, in call order — the real context the
    /// provider billed for, not a share of the stage total.
    /// <para>
    /// The report timeline needs these. Without them it fabricated a linear
    /// ramp, whose last entry came out equal to the SUM of every call's input;
    /// the cost estimator reads that last entry as the final context and bills
    /// it as fresh input.
    /// </para>
    /// </summary>
    public IReadOnlyList<int> PromptTokensPerCall { get; init; } = [];

    /// <summary>
    /// Measured output tokens, summed across calls. Replaces
    /// <c>answer.Length / 4</c> plus fifty per turn.
    /// </summary>
    public int CompletionTokens { get; init; }

    /// <summary>
    /// The reasoning share of <see cref="CompletionTokens"/>. Reported for
    /// visibility; already counted inside the completion total, never added to it.
    /// </summary>
    public int ReasoningTokens { get; init; }

    /// <summary>
    /// These counters plus another run's, for a stage that took more than one
    /// run of the loop to finish. Everything a stage is billed and timed on has
    /// to add up, or the ledger silently loses whatever the extra run cost.
    /// </summary>
    /// <param name="other">The later run's counters.</param>
    /// <returns>The combined counters, in call order.</returns>
    public AgentStats Plus(AgentStats other)
    {
        var byName = new Dictionary<string, ToolCallCounts>(ToolCallsByName, StringComparer.Ordinal);
        foreach (var (name, counts) in other.ToolCallsByName)
        {
            var existing = byName.GetValueOrDefault(name, new ToolCallCounts(0, 0));
            byName[name] = new ToolCallCounts(
                existing.Succeeded + counts.Succeeded, existing.Failed + counts.Failed);
        }

        return new AgentStats
        {
            Turns = Turns + other.Turns,
            ToolCallsTotal = ToolCallsTotal + other.ToolCallsTotal,
            ToolCallsSucceeded = ToolCallsSucceeded + other.ToolCallsSucceeded,
            ToolCallsFailed = ToolCallsFailed + other.ToolCallsFailed,
            ToolCallsByName = byName,
            Compactions = Compactions + other.Compactions,
            GuardrailInterventions = GuardrailInterventions + other.GuardrailInterventions,
            StormedCalls = StormedCalls + other.StormedCalls,
            RecoveredResponses = RecoveredResponses + other.RecoveredResponses,
            TruncationRepairs = TruncationRepairs + other.TruncationRepairs,
            LlmCalls = LlmCalls + other.LlmCalls,
            TotalLlmTimeSeconds = Math.Round(TotalLlmTimeSeconds + other.TotalLlmTimeSeconds, 3),
            TotalToolTimeSeconds = Math.Round(TotalToolTimeSeconds + other.TotalToolTimeSeconds, 3),
            CachedTokens = CachedTokens + other.CachedTokens,
            CacheWriteTokens = CacheWriteTokens + other.CacheWriteTokens,
            PromptTokens = PromptTokens + other.PromptTokens,
            PromptTokensPerCall = [.. PromptTokensPerCall, .. other.PromptTokensPerCall],
            CompletionTokens = CompletionTokens + other.CompletionTokens,
            ReasoningTokens = ReasoningTokens + other.ReasoningTokens,
        };
    }
}
