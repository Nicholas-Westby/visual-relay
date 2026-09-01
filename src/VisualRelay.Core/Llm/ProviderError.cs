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
/// <param name="RetryAfter">
/// How long the provider asked us to wait, when it said so. None of the four
/// providers was ever observed sending <c>Retry-After</c>, so this is normally
/// null and the exponential schedule applies — but a provider that starts
/// sending one is telling us something the schedule cannot know, and guessing
/// over it is how a rate limit turns into a ban.
/// </param>
public sealed record ProviderError(
    int StatusCode, string? Code, string Message, ProviderErrorKind Kind,
    TimeSpan? RetryAfter = null)
{
    /// <summary>The header a provider uses to ask for a specific wait.</summary>
    public const string RetryAfterHeader = "Retry-After";

    /// <summary>
    /// Reads <c>Retry-After</c> from response headers. Accepts both forms the
    /// HTTP spec allows: delay-seconds, and an HTTP-date to wait until.
    /// </summary>
    /// <param name="headers">The response headers.</param>
    /// <param name="now">The clock, so an HTTP-date resolves against test time.</param>
    /// <returns>The requested wait, or <c>null</c> when absent or unparseable.</returns>
    public static TimeSpan? ReadRetryAfter(
        IReadOnlyDictionary<string, string>? headers, DateTimeOffset now)
    {
        if (headers is null) return null;

        var value = headers
            .FirstOrDefault(h => string.Equals(h.Key, RetryAfterHeader, StringComparison.OrdinalIgnoreCase))
            .Value;
        if (string.IsNullOrWhiteSpace(value)) return null;

        if (int.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            return seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;

        if (DateTimeOffset.TryParse(value.Trim(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var until))
        {
            var wait = until - now;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// Whether retrying the same request against the same model could succeed.
    /// Quota exhaustion is deliberately excluded: it presents as a 429 but no
    /// amount of waiting fixes it.
    /// </summary>
    public bool IsRetryable => Kind is ProviderErrorKind.RateLimit or ProviderErrorKind.Server;
}
