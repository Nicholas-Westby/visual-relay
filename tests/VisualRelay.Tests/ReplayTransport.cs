using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// The replay half of the seam: serves recorded exchanges and nothing else.
/// <para>
/// <b>A miss throws.</b> This type holds no inner transport and no handler, so
/// falling through to the network is not a policy it declines to apply but a
/// thing it structurally cannot do — the same stance as GitSim's
/// <c>Unsupported()</c>, which refuses an unmodelled argv rather than shelling
/// out to real git. A silent fallback would spend money, need a key, and hide the
/// fact that the suite had drifted off its cassettes; a throw names the file that
/// is missing and the field that changed.
/// </para>
/// </summary>
/// <param name="store">Where cassettes are read from.</param>
/// <param name="provider">The provider directory name.</param>
/// <param name="scenario">The scenario directory name.</param>
internal sealed class ReplayTransport(CassetteStore store, string provider, string scenario)
    : IProviderTransport
{
    /// <inheritdoc />
    public Task<ProviderResponse> SendAsync(
        ProviderRequest request, CancellationToken cancellationToken = default)
    {
        var record = Lookup(request, nameof(SendAsync));
        if (record.Chunks is not null)
            throw new InvalidOperationException(
                $"ReplayTransport: cassette {record.Key}.json records a STREAMING exchange; "
                + "SendAsync cannot serve it. Call StreamAsync, or re-record this request "
                + "through the buffered path.");

        return Task.FromResult(
            new ProviderResponse(record.StatusCode, record.ResponseHeaders, record.Body));
    }

    /// <inheritdoc />
    public Task<ProviderStreamResponse> StreamAsync(
        ProviderRequest request, CancellationToken cancellationToken = default)
    {
        var record = Lookup(request, nameof(StreamAsync));
        var chunks = record.Chunks
            ?? throw new InvalidOperationException(
                $"ReplayTransport: cassette {record.Key}.json records a BUFFERED exchange; "
                + "StreamAsync cannot serve it. Call SendAsync, or re-record this request "
                + "through the streaming path.");

        return Task.FromResult(new ProviderStreamResponse(
            record.StatusCode, record.ResponseHeaders, token => CassetteChunks.Emit(chunks, token)));
    }

    private CassetteRecord Lookup(ProviderRequest request, string operation) =>
        store.TryRead(provider, scenario, CassetteKey.Compute(request))
        ?? throw new InvalidOperationException(
            CassetteMiss.Describe(store, provider, scenario, request, operation));
}
