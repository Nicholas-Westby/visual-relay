using System.Net;
using System.Net.Sockets;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class BackendReadinessProbeTests
{
    [Fact]
    public async Task CheckAsync_BackendListeningAndReturns200_IsReady()
    {
        var port = FreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
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
        var closedPort = FreePort();

        var result = await BackendReadinessProbe.CheckAsync(
            $"http://127.0.0.1:{closedPort}",
            TimeSpan.FromSeconds(2));

        Assert.False(result.IsReady);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
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
