namespace VisualRelay.Core.Llm;

/// <summary>
/// The one implementation that opens a socket. Its <see cref="HttpMessageHandler"/>
/// is constructor-injected so the fast suite can install a handler that throws on
/// any connect attempt, and so a per-provider handler can carry its own pooling
/// policy without a second composition root appearing elsewhere.
/// <para>
/// This is the only file allowed to construct an <see cref="HttpClient"/>; the
/// no-new-HttpClient guard allowlists it by name. Everything else takes an
/// <see cref="IProviderTransport"/>.
/// </para>
/// </summary>
public sealed class LiveProviderTransport : IProviderTransport, IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    /// <summary>Creates a transport over the supplied handler.</summary>
    /// <param name="handler">
    /// The message handler to send through. Owned by this instance unless
    /// <paramref name="disposeHandler"/> is false.
    /// </param>
    /// <param name="disposeHandler">Whether disposing this disposes the handler.</param>
    public LiveProviderTransport(HttpMessageHandler handler, bool disposeHandler = true)
    {
        // Timeout.InfiniteTimeSpan: HttpClient's single timeout cannot express the
        // four separate budgets this project needs (connect, time-to-first-byte,
        // inter-chunk idle, total wall clock), and its one knob would abort a
        // healthy slow stream. The budgets are enforced by the caller's tokens.
        _client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _ownsClient = disposeHandler;
        Handler = handler;
    }

    /// <summary>The handler this transport sends through.</summary>
    private HttpMessageHandler Handler { get; }

    /// <summary>
    /// Builds a transport with the pooling policy the providers need.
    /// <para>
    /// A short <c>PooledConnectionLifetime</c> replaces the <c>Connection: close</c>
    /// header the old config sent, which was inert twice over: all four endpoints
    /// negotiate HTTP/2, where <c>Connection</c> is a prohibited header, and in
    /// .NET setting it sends the header while the socket is reused anyway. Only
    /// the pooling knobs actually force a fresh connection, and recycling also
    /// clears stale DNS against these load balancers.
    /// </para>
    /// <para>
    /// The connect timeout is the only budget the handler owns; the other three
    /// belong to the caller, because one HTTP timeout cannot express them and
    /// would abort a healthy slow stream.
    /// </para>
    /// </summary>
    /// <param name="connectTimeout">TCP connect budget.</param>
    /// <returns>A transport over a freshly configured handler.</returns>
    public static LiveProviderTransport CreateDefault(TimeSpan? connectTimeout = null) =>
        new(new SocketsHttpHandler
        {
            ConnectTimeout = connectTimeout ?? ProviderTimeouts.Default.Connect,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });

    /// <inheritdoc />
    public async Task<ProviderResponse> SendAsync(
        ProviderRequest request, CancellationToken cancellationToken = default)
    {
        using var message = BuildMessage(request);
        using var response = await _client
            .SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new ProviderResponse((int)response.StatusCode, ReadHeaders(response), body);
    }

    /// <inheritdoc />
    public async Task<ProviderStreamResponse> StreamAsync(
        ProviderRequest request, CancellationToken cancellationToken = default)
    {
        using var message = BuildMessage(request);
        // ResponseHeadersRead so the status is available before the body arrives:
        // a byte-0 hang must be attributable, and an error status must short
        // circuit before the SSE parser sees anything.
        var response = await _client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        return new ProviderStreamResponse(
            (int)response.StatusCode,
            ReadHeaders(response),
            ct => ReadChunksAsync(response, ct),
            () =>
            {
                response.Dispose();
                return ValueTask.CompletedTask;
            });
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        HttpResponseMessage response,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) yield break;
            // Copy: the caller may hold the chunk past the next read.
            yield return buffer.AsMemory(0, read).ToArray();
        }
    }

    private static HttpRequestMessage BuildMessage(ProviderRequest request)
    {
        var message = new HttpRequestMessage(request.Method, request.Uri)
        {
            Content = new StringContent(request.Body, System.Text.Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in request.Headers)
            if (!message.Headers.TryAddWithoutValidation(name, value))
                message.Content.Headers.TryAddWithoutValidation(name, value);
        return message;
    }

    private static IReadOnlyDictionary<string, string> ReadHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers)
            headers[name] = string.Join(", ", values);
        foreach (var (name, values) in response.Content.Headers)
            headers[name] = string.Join(", ", values);
        return headers;
    }

    /// <summary>Disposes the client, and the handler when this instance owns it.</summary>
    public void Dispose()
    {
        _client.Dispose();
        if (_ownsClient) Handler.Dispose();
    }
}
