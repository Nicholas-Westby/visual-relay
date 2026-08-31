using VisualRelay.Core.Llm;

namespace VisualRelay.Core.Agent;

/// <summary>
/// Everything one run of the turn loop needs to know that is not the
/// conversation itself.
/// </summary>
/// <param name="Model">The concrete model to request.</param>
/// <param name="Endpoint">The provider's chat-completions endpoint.</param>
/// <param name="Headers">Request headers, including authorization.</param>
/// <param name="Capabilities">
/// The serving provider's measured capabilities, so a parameter it rejects is
/// never sent.
/// </param>
/// <param name="MaxTurns">
/// The turn budget. Running out is an <see cref="AgentLoopOutcome.Exhausted"/>
/// result, which the driver reads directly rather than inferring from exit 2.
/// </param>
/// <param name="StageBudget">
/// Wall clock for the whole stage. This is also the only ceiling on any single
/// tool invocation: there is no hidden per-call cap.
/// </param>
/// <param name="Resilience">Storm, guardrail and compaction tuning.</param>
/// <param name="RetryBudget">
/// How many times one turn may be retried after a retryable provider failure.
/// Exponential backoff with jitter is the only schedule available: none of the
/// four providers documents <c>Retry-After</c> and none was ever observed
/// sending it.
/// </param>
/// <param name="ContextWindow">
/// The model's context window in tokens, which the catalog already knows for
/// every model. Compaction triggers against it using the provider's own
/// measured <c>prompt_tokens</c>, so there is nothing to learn adaptively.
/// </param>
public sealed record AgentLoopOptions(
    string Model,
    Uri Endpoint,
    IReadOnlyDictionary<string, string> Headers,
    ProviderCapabilities Capabilities,
    int MaxTurns = 40,
    TimeSpan? StageBudget = null,
    AgentResilienceOptions? Resilience = null,
    int RetryBudget = 3,
    int ContextWindow = 128_000,
    TimeSpan? RetryBackoffBase = null)
{
    /// <summary>The resilience tuning, defaulted.</summary>
    public AgentResilienceOptions ResilienceOptions => Resilience ?? AgentResilienceOptions.Default;

    /// <summary>The stage wall clock, defaulted to an hour.</summary>
    public TimeSpan Budget => StageBudget ?? TimeSpan.FromHours(1);

    /// <summary>
    /// The first backoff step, doubling per attempt. Settable so a test can
    /// exercise the retry ladder without waiting real seconds for it.
    /// </summary>
    public TimeSpan BackoffBase => RetryBackoffBase ?? TimeSpan.FromSeconds(1);
}
