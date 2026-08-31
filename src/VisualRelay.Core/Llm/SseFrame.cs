namespace VisualRelay.Core.Llm;

/// <summary>What a parsed server-sent-events frame represents.</summary>
public enum SseFrameKind
{
    /// <summary>A data-bearing event: the payload is model output to parse.</summary>
    Event,

    /// <summary>
    /// A comment line, which providers use as a keepalive. Surfaced rather than
    /// dropped because the two clocks disagree about it: it proves the connection
    /// is alive, so it must feed the inter-chunk idle budget, but it is not model
    /// output, so it must NOT reset the output-silence clock. Collapsing the two
    /// is how a wedged request survived to the absolute ceiling before.
    /// </summary>
    Keepalive,

    /// <summary>The <c>[DONE]</c> terminator. Its absence makes a stream a failure.</summary>
    Done,
}

/// <summary>One frame lifted off an SSE stream.</summary>
/// <param name="Kind">What the frame represents.</param>
/// <param name="Data">
/// The concatenated <c>data:</c> payload, or the comment text for a keepalive.
/// Empty for <see cref="SseFrameKind.Done"/>.
/// </param>
/// <param name="EventType">The <c>event:</c> field when the provider sent one.</param>
public sealed record SseFrame(SseFrameKind Kind, string Data, string? EventType = null);
