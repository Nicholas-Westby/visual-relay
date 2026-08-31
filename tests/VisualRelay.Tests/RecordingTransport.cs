using System.Runtime.CompilerServices;
using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// The record half of the seam: a decorator that forwards every exchange to a
/// real transport and files what came back as a cassette.
/// <para>
/// <b>It redacts on write.</b> The key already excludes credentials — the header
/// allowlist never admits one — and this writes the canonical request through
/// <see cref="CassetteHeaders.Redact"/> again on the way to disk. The committed
/// cassettes are therefore protected twice over, and the secret-scanning guard is
/// a backstop that catches a mistake rather than the only thing standing between
/// an <c>Authorization</c> value and the repository.
/// </para>
/// <para>
/// The streaming path records only once the caller has drained the stream: the
/// cassette is written after the last chunk, so an abandoned enumeration leaves
/// no half-recorded exchange behind.
/// </para>
/// </summary>
/// <param name="inner">The transport that actually performs the exchange.</param>
/// <param name="store">Where cassettes are filed.</param>
/// <param name="provider">The provider directory name.</param>
/// <param name="scenario">The scenario directory name.</param>
internal sealed class RecordingTransport(
    IProviderTransport inner,
    CassetteStore store,
    string provider,
    string scenario) : IProviderTransport
{
    /// <inheritdoc />
    public async Task<ProviderResponse> SendAsync(
        ProviderRequest request, CancellationToken cancellationToken = default)
    {
        var response = await inner.SendAsync(request, cancellationToken);

        store.Write(CassetteRecord.Create(
            provider, scenario, request,
            response.StatusCode, response.Headers, response.Body, chunks: null));

        return response;
    }

    /// <inheritdoc />
    public async Task<ProviderStreamResponse> StreamAsync(
        ProviderRequest request, CancellationToken cancellationToken = default)
    {
        var stream = await inner.StreamAsync(request, cancellationToken);

        return new ProviderStreamResponse(
            stream.StatusCode,
            stream.Headers,
            token => TeeAsync(stream, request, token),
            stream.DisposeAsync);
    }

    /// <summary>
    /// Passes each chunk through untouched while keeping a copy, then writes the
    /// cassette once the provider has finished.
    /// </summary>
    private async IAsyncEnumerable<ReadOnlyMemory<byte>> TeeAsync(
        ProviderStreamResponse stream,
        ProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chunks = new List<byte[]>();

        await foreach (var chunk in stream.ReadChunksAsync(cancellationToken))
        {
            chunks.Add(chunk.ToArray());
            yield return chunk;
        }

        store.Write(CassetteRecord.Create(
            provider, scenario, request,
            stream.StatusCode, stream.Headers, body: "", chunks));
    }
}
