using System.Net;
using System.Net.Sockets;

namespace VisualRelay.Tests;

/// <summary>
/// Proves the fast suite is hermetic: the assembly-wide
/// <see cref="HermeticHttpHandler"/> factory refuses every connect that would
/// leave the machine, and still lets the loopback traffic the control-API and
/// readiness-probe tests depend on through.
/// </summary>
public sealed class HermeticFastSuiteTests
{
    // ── Refusal tests ──────────────────────────────────────────────────────

    /// <summary>
    /// Teeth: a request to a real provider host fails before any DNS lookup or
    /// socket, and the message names the host and port so the offending call is
    /// identifiable from CI output alone.
    /// </summary>
    [Fact]
    public async Task ProviderHost_IsRefused_WithHostAndPortInMessage()
    {
        using var client = HermeticHttpHandler.CreateClient(TimeSpan.FromSeconds(5));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("https://api.deepseek.com/v1/chat/completions"));

        var text = ex.ToString();
        Assert.Contains(HermeticHttpHandler.RefusalPrefix, text, StringComparison.Ordinal);
        Assert.Contains("api.deepseek.com:443", text, StringComparison.Ordinal);
        Assert.Contains("Cassettes", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A plain-HTTP non-loopback host is refused too, with its own port in the
    /// message — the gate is on the endpoint, not on the scheme.
    /// </summary>
    [Fact]
    public async Task NonLoopbackPlainHttpHost_IsRefused()
    {
        using var client = HermeticHttpHandler.CreateClient(TimeSpan.FromSeconds(5));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("http://router.huggingface.co:8080/v1/models"));

        Assert.Contains("router.huggingface.co:8080", ex.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The factory itself, not just the client wrapper, is gated: every handler
    /// it hands out carries the connect callback, and neither a proxy nor a
    /// shared cookie jar can redirect or leak across the endpoint it vets.
    /// </summary>
    [Fact]
    public void CreateHandler_ReturnsAGatedHandler()
    {
        using var handler = HermeticHttpHandler.CreateHandler();

        Assert.NotNull(handler.ConnectCallback);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
    }

    // ── Loopback exemption tests ───────────────────────────────────────────

    /// <summary>
    /// The one exemption: a loopback request completes normally. The control-API
    /// tests bind an in-process server and call <c>http://127.0.0.1:&lt;port&gt;</c>,
    /// so breaking this would take a chunk of the suite with it.
    /// </summary>
    [Fact]
    public async Task LoopbackRequest_IsAllowedThrough()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // ReSharper disable once AccessToDisposedClosure — the finally block stops the
        // listener and 'await serve' joins this task before 'listener' (using var) is
        // disposed at method exit; the catch handles any stop-while-serving race.
        var serve = Task.Run(async () =>
        {
            try
            {
                using var accepted = await listener.AcceptTcpClientAsync();
                await using var stream = accepted.GetStream();
                // Drain a chunk of the request (count used to satisfy CA2022) so the
                // response is not RST'd; one read covers a small GET's headers.
                var read = await stream.ReadAsync(new byte[1024]);
                if (read == 0)
                {
                    return;
                }

                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
                await stream.FlushAsync();
            }
            catch (Exception)
            {
                // Listener stopped before/while serving; nothing to do.
            }
        });

        try
        {
            using var client = HermeticHttpHandler.CreateClient(TimeSpan.FromSeconds(5));

            using var response = await client.GetAsync($"http://127.0.0.1:{port}/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            listener.Stop();
            await serve;
        }
    }

    // ── Host classification ────────────────────────────────────────────────

    /// <summary>
    /// Every spelling of "this machine" is treated as loopback, including a
    /// bracketed IPv6 literal and an address elsewhere in 127.0.0.0/8.
    /// </summary>
    /// <param name="host">The host portion of a destination endpoint.</param>
    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    public void IsLoopbackHost_RecognizesEverySpellingOfThisMachine(string host)
    {
        Assert.True(HermeticHttpHandler.IsLoopbackHost(host));
    }

    /// <summary>
    /// Hosts that leave the machine are not loopback — including a LAN address
    /// and a name that merely contains "localhost".
    /// </summary>
    /// <param name="host">The host portion of a destination endpoint.</param>
    [Theory]
    [InlineData("api.deepseek.com")]
    [InlineData("router.huggingface.co")]
    [InlineData("192.168.1.10")]
    [InlineData("localhost.example.com")]
    [InlineData("0.0.0.0")]
    public void IsLoopbackHost_RejectsHostsThatLeaveTheMachine(string host)
    {
        Assert.False(HermeticHttpHandler.IsLoopbackHost(host));
    }
}
