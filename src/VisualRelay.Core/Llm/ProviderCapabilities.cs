namespace VisualRelay.Core.Llm;

/// <summary>
/// Per-provider behaviour that cannot be inferred from the wire format, measured
/// against the live endpoints on 2026-08-31. Every value here was observed, not
/// read from documentation: none of the four documents any of it.
/// </summary>
/// <param name="CanDisableReasoning">
/// Whether the provider accepts a request to stop reasoning. GLM 5.3 Flash does
/// not: both <c>thinking: {"type":"disabled"}</c> and
/// <c>reasoning_effort: "minimal"</c> return 400 with code 1210 and the message
/// "This model always engages in thinking and cannot be disabled".
/// </param>
/// <param name="SupportsRequiredToolChoiceWhileThinking">
/// Whether <c>tool_choice: "required"</c> is accepted with reasoning on.
/// Moonshot returns 400 "tool_choice 'required' is incompatible with thinking
/// enabled"; DeepSeek accepts it. The spec attributed this constraint to
/// DeepSeek, but a direct probe showed DeepSeek accepting it with reasoning on,
/// so it is recorded here per provider rather than assumed.
/// </param>
/// <param name="ReasoningEffortValues">
/// The effort levels the provider accepts. Z.AI's own 400 names its set.
/// Empty means the provider takes no effort knob at all.
/// </param>
public sealed record ProviderCapabilities(
    bool CanDisableReasoning,
    bool SupportsRequiredToolChoiceWhileThinking,
    IReadOnlyList<string> ReasoningEffortValues)
{
    /// <summary>
    /// Whether tool choice must be coerced through the prompt instead of the
    /// parameter. <c>tool_choice: "required"</c> is not portable, so a provider
    /// that rejects it needs prompt-level coercion.
    /// </summary>
    public bool NeedsPromptLevelToolCoercion => !SupportsRequiredToolChoiceWhileThinking;
}

/// <summary>Measured capabilities for each of the four remaining providers.</summary>
public static class ProviderCapabilityCatalog
{
    private static readonly IReadOnlyDictionary<string, ProviderCapabilities> ByProvider =
        new Dictionary<string, ProviderCapabilities>(StringComparer.OrdinalIgnoreCase)
        {
            // reasoning_effort: "none" is accepted and clears reasoning_content.
            ["DeepSeek"] = new(
                CanDisableReasoning: true,
                SupportsRequiredToolChoiceWhileThinking: true,
                ReasoningEffortValues: ["none", "low", "medium", "high"]),

            // Always-thinking. Its own 400 names the accepted set.
            ["Z.AI"] = new(
                CanDisableReasoning: false,
                SupportsRequiredToolChoiceWhileThinking: true,
                ReasoningEffortValues: ["low", "high", "max"]),

            // Rejects required tool choice while thinking is on.
            ["Moonshot"] = new(
                CanDisableReasoning: false,
                SupportsRequiredToolChoiceWhileThinking: false,
                ReasoningEffortValues: []),

            // A router over many upstreams, so assume the weakest of each.
            ["Hugging Face"] = new(
                CanDisableReasoning: false,
                SupportsRequiredToolChoiceWhileThinking: false,
                ReasoningEffortValues: []),
        };

    /// <summary>
    /// Capabilities for a provider name as used by the backend catalog. An
    /// unknown provider gets the most conservative set, so a new route cannot
    /// silently inherit a permission nothing measured.
    /// </summary>
    /// <param name="providerName">The provider's display name.</param>
    /// <returns>The measured capabilities, or the conservative default.</returns>
    public static ProviderCapabilities For(string providerName) =>
        ByProvider.TryGetValue(providerName, out var capabilities)
            ? capabilities
            : new ProviderCapabilities(false, false, []);
}
