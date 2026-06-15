using System.Net;
using System.Net.Sockets;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class BackendReadinessProbeTests
{
    // The fake backend is a raw TcpListener rather than HttpListener: the listener
    // holds its own port for its whole lifetime (no release-then-rebind race), and
    // it sidesteps HttpListener's process-wide static HttpEndPointManager, which
    // throws "Address already in use" during Dispose on macOS when several
    // listeners churn in one test process (as the full suite does during a
    // pipeline's stage-9 verify). A handful of header bytes plus a fixed 200 line
    // is all the probe's HttpClient needs to see a reachable backend.
    private const int PortRollAttempts = 25;

    [Fact]
    public async Task CheckAsync_BackendListeningAndReturns200_IsReady()
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
                using var client = await listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
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
            var result = await BackendReadinessProbe.CheckAsync(
                $"http://127.0.0.1:{port}",
                TimeSpan.FromSeconds(2));

            Assert.True(result.IsReady);
            Assert.Null(result.Message);
        }
        finally
        {
            listener.Stop();
            await serve;
        }
    }

    [Fact]
    public async Task CheckAsync_NothingListening_IsNotReadyWithMessage()
    {
        for (var attempt = 0; ; attempt++)
        {
            var closedPort = FreePort();

            var result = await BackendReadinessProbe.CheckAsync(
                $"http://127.0.0.1:{closedPort}",
                TimeSpan.FromSeconds(2));

            // If something grabbed the just-freed port it would answer as "ready";
            // re-roll onto a new closed port rather than assert on a stolen one.
            if (!result.IsReady || attempt >= PortRollAttempts)
            {
                Assert.False(result.IsReady);
                Assert.False(string.IsNullOrWhiteSpace(result.Message));
                return;
            }
        }
    }

    // Bind to port 0 to let the OS assign a free port, read it, then release it so
    // the caller can probe a port with nothing listening (connection refused).
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
