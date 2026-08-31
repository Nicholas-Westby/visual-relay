using System.Net;
using System.Net.Sockets;

namespace VisualRelay.Tests;

/// <summary>
/// The test assembly's one HTTP composition root, and the mechanism that makes
/// the fast suite hermetic. Every handler it builds installs a
/// <see cref="SocketsHttpHandler.ConnectCallback"/> that refuses any connect to a
/// non-loopback host, so a test that reaches a real provider fails immediately —
/// naming the host and port — instead of spending money, leaking a key, or going
/// red only when the network is down.
/// <para>
/// Loopback is the one exemption: the control-API tests bind an in-process HTTP
/// server and call <c>http://127.0.0.1:&lt;port&gt;</c>, and the backend readiness
/// probe is exercised against a local <see cref="TcpListener"/>. Neither leaves
/// the machine, so neither is a hermeticity risk; a real provider host still
/// throws.
/// </para>
/// <para>
/// This is the only file in <c>tests/</c> allowed to construct an
/// <see cref="HttpClient"/> or a <see cref="SocketsHttpHandler"/>;
/// <c>HttpClientConstructionGuard</c> allowlists it by name, which is what stops
/// a test from quietly building an un-gated handler and routing around this file.
/// </para>
/// </summary>
internal static class HermeticHttpHandler
{
    /// <summary>
    /// Leading text of the refusal message. Tests match on this rather than on
    /// the whole sentence.
    /// </summary>
    internal const string RefusalPrefix = "Hermetic fast suite refused a network connect";

    /// <summary>
    /// Builds a fresh <see cref="SocketsHttpHandler"/> that throws on any
    /// non-loopback connect. Caller owns the handler.
    /// </summary>
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        ConnectCallback = ConnectAsync,
        // No proxy and no cookies: a proxy would redirect the endpoint the
        // callback is asked to vet, and a shared cookie jar is cross-test state.
        UseProxy = false,
        UseCookies = false,
    };

    /// <summary>
    /// Builds an <see cref="HttpClient"/> over a hermetic handler, which the
    /// client owns and disposes.
    /// </summary>
    /// <param name="timeout">The client-level timeout.</param>
    /// <returns>A client that can only ever reach loopback.</returns>
    internal static HttpClient CreateClient(TimeSpan timeout) =>
        new(CreateHandler()) { Timeout = timeout };

    /// <summary>
    /// Whether <paramref name="host"/> names this machine. Covers the literal
    /// names plus any address in 127.0.0.0/8 and <c>::1</c>.
    /// </summary>
    /// <param name="host">The host portion of the destination endpoint.</param>
    /// <returns><c>true</c> when a connect to it never leaves the machine.</returns>
    internal static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        // Strip the brackets an IPv6 literal carries in a URI authority.
        var bare = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        return IPAddress.TryParse(bare, out var address) && IPAddress.IsLoopback(address);
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var endPoint = context.DnsEndPoint;

        if (!IsLoopbackHost(endPoint.Host))
        {
            throw new HttpRequestException(
                $"{RefusalPrefix} to {endPoint.Host}:{endPoint.Port}. The fast suite is "
                + "hermetic: it opens no sockets except to loopback. Route this call "
                + "through IProviderTransport and record a cassette under "
                + "tests/VisualRelay.Tests/Cassettes/ instead of hitting the live provider.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
