namespace VisualRelay.Core.Llm;

/// <summary>
/// The seam between the agent loop and the network. One implementation talks to
/// a real provider; the test implementations record and replay cassettes, so the
/// fast suite exercises the whole loop with zero network and zero spend.
/// <para>
/// Everything above this seam is deterministic and unit-testable; everything
/// below it is a socket. Nothing else in the codebase may construct an
/// <see cref="HttpClient"/>, which a guard enforces.
/// </para>
/// </summary>
public interface IProviderTransport
{
    /// <summary>Sends a request and buffers the whole response.</summary>
    /// <param name="request">The wire-level request.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The buffered response, whatever its status.</returns>
    Task<ProviderResponse> SendAsync(
        ProviderRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a request and returns once the response headers are in, so a
    /// non-2xx is visible before the first chunk is parsed.
    /// </summary>
    /// <param name="request">The wire-level request.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The streaming response; dispose it when done.</returns>
    Task<ProviderStreamResponse> StreamAsync(
        ProviderRequest request, CancellationToken cancellationToken = default);
}
