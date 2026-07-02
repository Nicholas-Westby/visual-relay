using System.Net;
using System.Net.Sockets;
using System.Text;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;

namespace VisualRelay.Tests;

/// <summary>
/// A control command must never execute while the client receives an error: a raw
/// bodyless POST (no Content-Length) is refused BEFORE the command runs, while a
/// Content-Length: 0 POST still runs normally. Split out of ControlServerTests to
/// keep each test file within the 300-line guard.
/// </summary>
[Collection("Headless")]
public sealed class ControlServerBodylessPostTests
{
    /// <summary>
    /// A POST with no Content-Length header (raw bodyless POST) must NOT execute
    /// the command. The server must detect this shape and refuse before invoking
    /// the command, returning 411. This guards against the HttpListener silently
    /// dispatching the request to the handler while the client receives a 411
    /// error page — the command would execute behind an error response.
    /// </summary>
    [AvaloniaFact]
    public async Task BodylessPost_NoContentLength_DoesNotExecuteCommand()
    {
        var port = GetFreePort();
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);
        var server = new ControlServer(api, new ControlServerOptions(Enabled: true, Port: port, Token: null));

        server.Start();
        try
        {
            // Sanity: PauseRequested starts false.
            Assert.False(vm.PauseRequested);

            // Send a raw HTTP POST with no Content-Length header.
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);

            var request = $"POST /command/pause-toggle HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n";
            var requestBytes = Encoding.ASCII.GetBytes(request);
            await client.GetStream().WriteAsync(requestBytes);

            // Read the response status line.
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
            var statusLine = await reader.ReadLineAsync();
            Assert.NotNull(statusLine);
            Assert.StartsWith("HTTP/1.1 411", statusLine);

            // The command must NOT have executed — PauseRequested stays false.
            Assert.False(vm.PauseRequested,
                "A bodyless POST (no Content-Length) must not execute the command. " +
                "PauseRequested should remain false but was flipped.");
        }
        finally
        {
            server.Stop();
        }
    }

    /// <summary>
    /// A POST with Content-Length: 0 must still execute the command normally
    /// and return a 200 JSON response. This confirms the guard does not
    /// accidentally block valid bodyless (zero-length-body) requests.
    /// </summary>
    [AvaloniaFact]
    public async Task ContentLengthZero_Post_ExecutesCommandNormally()
    {
        var port = GetFreePort();
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);
        var server = new ControlServer(api, new ControlServerOptions(Enabled: true, Port: port, Token: null));

        server.Start();
        try
        {
            // Sanity: PauseRequested starts false.
            Assert.False(vm.PauseRequested);

            // Send a raw HTTP POST with explicit Content-Length: 0.
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);

            var request = $"POST /command/pause-toggle HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            var requestBytes = Encoding.ASCII.GetBytes(request);
            await client.GetStream().WriteAsync(requestBytes);

            // Read the response status line.
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
            var statusLine = await reader.ReadLineAsync();
            Assert.NotNull(statusLine);
            Assert.StartsWith("HTTP/1.1 200", statusLine);

            // The command must have executed — PauseRequested flipped to true.
            Assert.True(vm.PauseRequested,
                "A Content-Length: 0 POST must execute the command normally. " +
                "PauseRequested should be true but is still false.");
        }
        finally
        {
            server.Stop();
        }
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
