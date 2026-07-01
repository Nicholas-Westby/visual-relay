using System.Net;
using System.Text.Json;
using System.Threading;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the localhost HTTP control server: deterministic env-var option
/// parsing (port/disable/token defaults + overrides) plus one end-to-end
/// HttpListener round-trip (GET /health on an ephemeral free port). The E2E
/// test binds 127.0.0.1 only.
/// </summary>
public sealed class ControlServerOptionsTests
{
    [Fact]
    public void Defaults_WhenNoEnv_PortIs8765_EnabledNoToken()
    {
        var env = new DictionaryEnvironmentAccessor();

        var options = ControlServerOptions.FromEnvironment(env);

        Assert.True(options.Enabled);
        Assert.Equal(8765, options.Port);
        Assert.Null(options.Token);
    }

    [Fact]
    public void Disable_SetTo1_DisablesServer()
    {
        var env = new DictionaryEnvironmentAccessor { ["VR_CONTROL_DISABLE"] = "1" };

        var options = ControlServerOptions.FromEnvironment(env);

        Assert.False(options.Enabled);
    }

    [Fact]
    public void Port_OverrideParsed_AndInvalidFallsBackToDefault()
    {
        var ok = ControlServerOptions.FromEnvironment(
            new DictionaryEnvironmentAccessor { ["VR_CONTROL_PORT"] = "9100" });
        Assert.Equal(9100, ok.Port);

        var bad = ControlServerOptions.FromEnvironment(
            new DictionaryEnvironmentAccessor { ["VR_CONTROL_PORT"] = "not-a-port" });
        Assert.Equal(8765, bad.Port);
    }

    [Fact]
    public void Token_WhenSet_IsCaptured()
    {
        var env = new DictionaryEnvironmentAccessor { ["VR_CONTROL_TOKEN"] = "s3cret" };

        var options = ControlServerOptions.FromEnvironment(env);

        Assert.Equal("s3cret", options.Token);
    }
}

[Collection("Headless")]
public sealed class ControlServerEndToEndTests
{
    // One shared client for the whole class: HttpClient is designed to be reused
    // (a fresh one per call leaks sockets — the ShortLivedHttpClient inspection).
    // All requests target loopback over a short test run. Per-request headers
    // (e.g. X-VR-Token) go on a per-call HttpRequestMessage, never on this shared
    // client's DefaultRequestHeaders, so no auth state leaks between tests.
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(5) };

    [AvaloniaFact]
    public async Task HealthEndpoint_RoundTripsOverLoopback()
    {
        var port = GetFreePort();
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);
        var server = new ControlServer(api, new ControlServerOptions(Enabled: true, Port: port, Token: null));

        server.Start();
        try
        {
            var response = await Client.GetAsync($"http://127.0.0.1:{port}/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("Visual Relay", doc.RootElement.GetProperty("app").GetString());
        }
        finally
        {
            server.Stop();
        }
    }

    [AvaloniaFact]
    public async Task Token_WhenConfigured_RejectsMissingHeaderWith401_AndAcceptsMatch()
    {
        var port = GetFreePort();
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);
        var server = new ControlServer(api, new ControlServerOptions(Enabled: true, Port: port, Token: "letmein"));

        server.Start();
        try
        {
            // No header → 401. Uses the shared client with no auth state.
            var noTok = await Client.GetAsync($"http://127.0.0.1:{port}/health");
            Assert.Equal(HttpStatusCode.Unauthorized, noTok.StatusCode);

            // Correct token → 200. The header rides on this one request message,
            // not the shared client, so it never leaks to other tests.
            using var withTok = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/health");
            withTok.Headers.Add("X-VR-Token", "letmein");
            var ok = await Client.SendAsync(withTok);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        finally
        {
            server.Stop();
        }
    }

    [AvaloniaFact]
    public async Task StateAndCommand_RoundTrip_GatesDisabledCommandWith409()
    {
        var port = GetFreePort();
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);
        var server = new ControlServer(api, new ControlServerOptions(Enabled: true, Port: port, Token: null));

        server.Start();
        try
        {
            // /state returns the snapshot with the commands map.
            var state = await Client.GetStringAsync($"http://127.0.0.1:{port}/state");
            using (var doc = JsonDocument.Parse(state))
            {
                Assert.True(doc.RootElement.TryGetProperty("commands", out _));
            }

            // A disabled command (run-selected with no selection) → 409 over the wire.
            var disabled = await Client.PostAsync(
                $"http://127.0.0.1:{port}/command/run-selected", null);
            Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);

            // A safe enabled command (pause-toggle) → 200.
            var ok = await Client.PostAsync(
                $"http://127.0.0.1:{port}/command/pause-toggle", null);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        finally
        {
            server.Stop();
        }
    }

    /// <summary>
    /// Verifies that the shared headless test app disables the vr-control listener via
    /// the process environment variable the App reads on boot (VR_CONTROL_DISABLE=1).
    /// This is a deterministic in-process assertion that no listener will start.
    /// </summary>
    [AvaloniaFact]
    public void HeadlessApp_DisablesControlServer_ViaProcessEnv()
    {
        var options = ControlServerOptions.FromEnvironment(new ProcessEnvironmentAccessor());
        Assert.False(options.Enabled,
            "Headless test app must disable the vr-control listener (VR_CONTROL_DISABLE=1) so booting the App in tests starts no leaked HttpListener.");
    }

    /// <summary>
    /// Verifies that ControlServer releases its HttpListener/port when Dispose() is
    /// called, so a fresh listener can bind the same port immediately after disposal.
    /// </summary>
    [AvaloniaFact]
    public async Task ControlServer_Dispose_ReleasesListener()
    {
        var port = GetFreePort();
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);

        var server = new ControlServer(api, new ControlServerOptions(Enabled: true, Port: port, Token: null));
        server.Start();

        // Confirm it is listening before dispose.
        var response = await Client.GetAsync($"http://127.0.0.1:{port}/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Dispose must release the port so a fresh listener can bind the same prefix.
        // Kernel socket teardown (e.g. TIME_WAIT) can vary — poll with a bounded
        // retry instead of assuming the port is instantly bindable.
        server.Dispose();

        const int retryMs = 50;
        const int maxRetries = 40; // ~2 s total
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                using var probe = new HttpListener();
                probe.Prefixes.Add($"http://127.0.0.1:{port}/");
                probe.Start();
                Assert.True(probe.IsListening,
                    "Dispose() must release the listener's port so a fresh HttpListener can bind the same prefix.");
                probe.Stop();
                return;
            }
            catch (HttpListenerException)
            {
                if (attempt < maxRetries - 1)
                    Thread.Sleep(retryMs);
            }
        }

        Assert.Fail(
            $"Dispose() did not release port {port} within {maxRetries * retryMs}ms. " +
            "The accept-loop task may not have completed socket teardown.");
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
