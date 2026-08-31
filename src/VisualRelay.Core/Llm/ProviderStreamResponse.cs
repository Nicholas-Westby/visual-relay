namespace VisualRelay.Core.Llm;

/// <summary>
/// A streaming response from a provider: the status and headers arrive first so
/// a non-2xx (or a 200 carrying an error body) can be handled before any chunk
/// is parsed, then the raw byte chunks are pulled as they arrive.
/// <para>
/// Chunks are handed over exactly as received, with no attempt to align them to
/// event or character boundaries: a provider may split an event mid-UTF-8
/// codepoint, and re-joining that is the SSE parser's job, not the transport's.
/// </para>
/// </summary>
public sealed class ProviderStreamResponse : IAsyncDisposable
{
    private readonly Func<CancellationToken, IAsyncEnumerable<ReadOnlyMemory<byte>>> _chunks;
    private readonly Func<ValueTask>? _dispose;

    /// <summary>Creates a streaming response over a chunk source.</summary>
    /// <param name="statusCode">The HTTP status of the response.</param>
    /// <param name="headers">Response headers, lower-cased keys.</param>
    /// <param name="chunks">Produces the raw byte chunks for one enumeration.</param>
    /// <param name="dispose">Releases the underlying response, if any.</param>
    public ProviderStreamResponse(
        int statusCode,
        IReadOnlyDictionary<string, string> headers,
        Func<CancellationToken, IAsyncEnumerable<ReadOnlyMemory<byte>>> chunks,
        Func<ValueTask>? dispose = null)
    {
        StatusCode = statusCode;
        Headers = headers;
        _chunks = chunks;
        _dispose = dispose;
    }

    /// <summary>The HTTP status of the response.</summary>
    public int StatusCode { get; }

    /// <summary>Response headers, lower-cased keys.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Reads the raw byte chunks as the provider emits them.</summary>
    /// <param name="cancellationToken">Cancels the read mid-stream.</param>
    /// <returns>The chunk sequence; enumerate once.</returns>
    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        CancellationToken cancellationToken = default) => _chunks(cancellationToken);

    /// <summary>Releases the underlying HTTP response.</summary>
    public ValueTask DisposeAsync() => _dispose?.Invoke() ?? ValueTask.CompletedTask;
}
