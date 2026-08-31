using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// An in-memory <see cref="IProviderTransport"/> that hands back one canned
/// exchange. It stands in for the live transport underneath
/// <see cref="RecordingTransport"/> so the recorder can be tested without a
/// socket, and it captures the request it was handed so a test can assert what
/// the decorator forwarded.
/// </summary>
/// <param name="statusCode">The status both paths return.</param>
/// <param name="headers">The response headers both paths return.</param>
/// <param name="body">The buffered body <see cref="SendAsync"/> returns.</param>
/// <param name="chunks">The chunks <see cref="StreamAsync"/> emits, in order.</param>
internal sealed class StubProviderTransport(
    int statusCode,
    IReadOnlyDictionary<string, string> headers,
    string body,
    IReadOnlyList<byte[]>? chunks = null) : IProviderTransport
{
    /// <summary>The last request this transport was handed, or null.</summary>
    public ProviderRequest? LastRequest { get; private set; }

    /// <inheritdoc />
    public Task<ProviderResponse> SendAsync(
        ProviderRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(new ProviderResponse(statusCode, headers, body));
    }

    /// <inheritdoc />
    public Task<ProviderStreamResponse> StreamAsync(
        ProviderRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(new ProviderStreamResponse(
            statusCode, headers, token => CassetteChunks.Emit(chunks ?? [], token)));
    }
}
