using System.Net;
using System.Net.Sockets;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class BackendReadinessProbeTests
{
    // FreePort() must release the port before the test can use it, leaving a race
    // window in which another process claims it. On a busy machine — e.g. the live
    // model backend plus swival subprocesses running during a pipeline's stage-9
    // verify — that window gets hit and the listener bind throws
    // "Address already in use", flaking the whole suite. The retries below close
    // the race by re-rolling onto a fresh port instead of failing.
    private const int PortRollAttempts = 25;

    [Fact]
    public async Task CheckAsync_BackendListeningAndReturns200_IsReady()
    {
        using var listener = StartListenerOnFreePort(out var port);
        var serve = Task.Run(async () =>
        {
            try
            {
                var context = await listener.GetContextAsync();
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
            catch (HttpListenerException)
            {
                // Listener stopped before a request arrived; nothing to serve.
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

    // Starting an HttpListener needs a concrete port, but FreePort() releases the
    // port before we bind it here. Retry the bind on a fresh port so the
    // release-then-rebind race never fails the test.
    private static HttpListener StartListenerOnFreePort(out int port)
    {
        for (var attempt = 0; ; attempt++)
        {
            port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException) when (attempt < PortRollAttempts)
            {
                ((IDisposable)listener).Dispose();
            }
        }
    }

    // Bind to port 0 to let the OS assign a free port, read it, then release it
    // so the caller can decide whether to listen (reachable) or leave it closed
    // (refused).
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
