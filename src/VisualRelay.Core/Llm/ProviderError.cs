namespace VisualRelay.Core.Llm;

/// <summary>How a provider failure should be treated by the retry ladder.</summary>
public enum ProviderErrorKind
{
    /// <summary>Credentials rejected. Never retryable; the key is wrong or revoked.</summary>
    Auth,

    /// <summary>Asked to slow down. Retryable with exponential backoff and jitter.</summary>
    RateLimit,

    /// <summary>
    /// Out of money or out of quota. Arrives as a 429 on every provider measured,
    /// never as a 402, so it is indistinguishable from a rate limit by status
    /// alone. Retrying burns the budget on a request that cannot succeed.
    /// </summary>
    QuotaExhausted,

    /// <summary>
    /// The model is gone, renamed, or not served here. Not retryable on this
    /// model; the caller falls through to the next hop in the chain.
    /// </summary>
    ModelUnavailable,

    /// <summary>The request itself is malformed. Retrying it unchanged cannot help.</summary>
    BadRequest,

    /// <summary>A provider-side fault. Retryable.</summary>
    Server,

    /// <summary>An error the shape of which was not recognised.</summary>
    Unknown,
}

/// <summary>
/// A normalized provider failure. The four providers disagree four ways about
/// how to report one, so everything above this type sees a single shape.
/// </summary>
/// <param name="StatusCode">The HTTP status, which alone is never sufficient.</param>
/// <param name="Code">The provider's own error code, as a string, when it sent one.</param>
/// <param name="Message">Human-readable text, always populated.</param>
/// <param name="Kind">How the retry ladder should treat it.</param>
public sealed record ProviderError(int StatusCode, string? Code, string Message, ProviderErrorKind Kind)
{
    /// <summary>
    /// Whether retrying the same request against the same model could succeed.
    /// Quota exhaustion is deliberately excluded: it presents as a 429 but no
    /// amount of waiting fixes it.
    /// </summary>
    public bool IsRetryable => Kind is ProviderErrorKind.RateLimit or ProviderErrorKind.Server;
}
