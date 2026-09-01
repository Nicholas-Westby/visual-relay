using System.Net;
using System.Text;
using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// Covers <see cref="LiveProviderTransport"/> through a stub handler, so the one
/// class allowed to hold an <see cref="HttpClient"/> is exercised without a
/// socket. The transport this replaced shipped with no coverage at all.
/// </summary>
public sealed class LiveProviderTransportTests
{
    private static readonly ProviderRequest Request = new(
        HttpMethod.Post,
        new Uri("https://provider.test/v1/chat/completions"),
        new Dictionary<string, string> { ["Authorization"] = "Bearer secret", ["accept"] = "text/event-stream" },
        """{"model":"m"}""");

    /// <summary>A buffered send returns the status, headers and body verbatim.</summary>
    [Fact]
    public async Task SendAsync_ReturnsStatusHeadersAndBody()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, "{\"ok\":true}");
        using var transport = new LiveProviderTransport(handler, disposeHandler: false);

        var response = await transport.SendAsync(Request);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("{\"ok\":true}", response.Body);
        Assert.True(response.Headers.ContainsKey("Content-Type"));
    }

    /// <summary>Request headers reach the handler, including the auth header.</summary>
    [Fact]
    public async Task SendAsync_ForwardsRequestHeadersAndBody()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, "{}");
        using var transport = new LiveProviderTransport(handler, disposeHandler: false);

        await transport.SendAsync(Request);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(Request.Uri, handler.LastRequest.RequestUri);
        Assert.Equal("Bearer secret", handler.LastRequest.Headers.GetValues("Authorization").Single());
        Assert.Equal("""{"model":"m"}""", handler.LastBody);
    }

    /// <summary>An error status is returned rather than thrown, body intact.</summary>
    [Fact]
    public async Task SendAsync_DoesNotThrowOnAnErrorStatus()
    {
        using var handler = new StubHandler(HttpStatusCode.TooManyRequests, """{"error":"slow down"}""");
        using var transport = new LiveProviderTransport(handler, disposeHandler: false);

        var response = await transport.SendAsync(Request);

        Assert.Equal(429, response.StatusCode);
        Assert.Contains("slow down", response.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The streaming path exposes the status before any chunk, then yields the
    /// body bytes, so a non-2xx short circuits before parsing begins.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ExposesStatusThenChunks()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, "data: one\n\ndata: [DONE]\n\n");
        using var transport = new LiveProviderTransport(handler, disposeHandler: false);

        await using var response = await transport.StreamAsync(Request);

        Assert.Equal(200, response.StatusCode);

        var text = new StringBuilder();
        await foreach (var chunk in response.ReadChunksAsync())
            text.Append(Encoding.UTF8.GetString(chunk.Span));

        Assert.Equal("data: one\n\ndata: [DONE]\n\n", text.ToString());
    }

    /// <summary>
    /// The client's own timeout is disabled: a single HTTP timeout cannot express
    /// four budgets and would abort a healthy slow stream. The budgets are the
    /// caller's job, enforced through cancellation.
    /// </summary>
    [Fact]
    public async Task StreamAsync_HonoursCallerCancellation()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, "data: one\n\n");
        using var transport = new LiveProviderTransport(handler, disposeHandler: false);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.StreamAsync(Request, cts.Token));
    }

    /// <summary>A stub handler that answers from memory and records what it was sent.</summary>
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
