namespace VisualRelay.Core.Llm;

/// <summary>Why a completion stopped, in terms the caller branches on.</summary>
public enum CompletionOutcome
{
    /// <summary>The model finished normally.</summary>
    Completed,

    /// <summary>
    /// The output ceiling was hit with no content produced. Distinct from an
    /// empty answer and separately retryable: on a reasoning model the whole
    /// budget can go to reasoning before a single content token is emitted, so
    /// the fix is a larger budget or lower effort, not an escalation.
    /// </summary>
    LengthWithoutContent,

    /// <summary>The provider returned an error, or a 200 carrying an error body.</summary>
    Failed,

    /// <summary>
    /// The stream ended without its <c>[DONE]</c> terminator. Whatever content
    /// arrived is preserved, but the turn is incomplete.
    /// </summary>
    Truncated,

    /// <summary>A budget expired: time to first byte, inter-chunk idle, or total.</summary>
    TimedOut,
}

/// <summary>One finished model turn.</summary>
/// <param name="Outcome">Why it stopped.</param>
/// <param name="Content">The assistant text, possibly empty.</param>
/// <param name="ReasoningContent">The reasoning trace, replayed on the next turn.</param>
/// <param name="ToolCalls">Tool calls the model asked for.</param>
/// <param name="FinishReason">The provider's own finish reason, verbatim.</param>
/// <param name="Usage">Measured usage, never estimated.</param>
/// <param name="ServedModel">
/// The concrete model the provider reported serving, which is what cost is
/// attributed to. A tier alias would hide a fallback hop entirely.
/// </param>
/// <param name="Error">The failure, when there was one.</param>
public sealed record ChatCompletion(
    CompletionOutcome Outcome,
    string Content,
    string? ReasoningContent,
    IReadOnlyList<ToolCall> ToolCalls,
    string? FinishReason,
    ProviderUsage? Usage,
    string? ServedModel,
    ProviderError? Error = null);
