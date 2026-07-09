using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the localhost HTTP control server: deterministic env-var option
/// parsing (port/disable/token defaults + overrides), one real-socket smoke
/// test to prove Kestrel binding, and in-memory handler integration tests.
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
public sealed class ControlServerTests
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>
    /// Invokes the control API handler directly via <see cref="DefaultHttpContext"/>
    /// — in-memory, no sockets, no ports. Returns the context for assertions.
    /// </summary>
    private static async Task<HttpContext> InvokeAsync(
        ControlApi api, ControlServerOptions options,
        string method, string path,
        string? token = null,
        string? requestBody = null)
    {
        var handler = ControlServer.BuildHandler(api, options);

        var context = new DefaultHttpContext
        {
            Request = { Method = method, Path = path },
            Response = { Body = new MemoryStream() }
        };

        if (token is not null)
        {
            context.Request.Headers["X-VR-Token"] = token;
        }

        if (requestBody is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(requestBody);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.Headers.ContentLength = bytes.Length;
        }

        await handler(context);
        context.Response.Body.Position = 0;
        return context;
    }

    /// <summary>
    /// The ONE real-socket smoke test that proves Kestrel binds on port 0
    /// and serves a request. Uses BoundPort to eliminate the GetFreePort
    /// TOCTOU. All other integration tests run in-memory via DefaultHttpContext.
    /// </summary>
    [AvaloniaFact]
    public async Task KestrelSmokeTest_BindsOnPort0_AndServesHealth()
    {
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);
        var server = new ControlServer(api, new ControlServerOptions(Enabled: true, Port: 0, Token: null));

        server.Start();
        try
        {
            var port = server.BoundPort;
            Assert.True(port > 0, "BoundPort must reflect the OS-assigned port when Port=0.");

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
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);
        var options = new ControlServerOptions(Enabled: true, Port: 0, Token: "letmein");

        // No header → 401.
        var noTok = await InvokeAsync(api, options, "GET", "/health");
        Assert.Equal(401, noTok.Response.StatusCode);

        // Correct token → 200.
        var ok = await InvokeAsync(api, options, "GET", "/health", token: "letmein");
        Assert.Equal(200, ok.Response.StatusCode);
    }

    [AvaloniaFact]
    public async Task StateAndCommand_RoundTrip_GatesDisabledCommandWith409()
    {
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);
        var options = new ControlServerOptions(Enabled: true, Port: 0, Token: null);

        // /state returns the snapshot with the commands map.
        var stateCtx = await InvokeAsync(api, options, "GET", "/state");
        Assert.Equal(200, stateCtx.Response.StatusCode);

        stateCtx.Response.Body.Position = 0;
        using (var reader = new StreamReader(stateCtx.Response.Body, Encoding.UTF8, leaveOpen: true))
        {
            var state = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(state);
            Assert.True(doc.RootElement.TryGetProperty("commands", out _));
        }

        // A disabled command (run-selected with no selection) → 409.
        var disabledCtx = await InvokeAsync(api, options, "POST", "/command/run-selected");
        Assert.Equal(409, disabledCtx.Response.StatusCode);

        // A safe enabled command (pause-toggle) → 200.
        var okCtx = await InvokeAsync(api, options, "POST", "/command/pause-toggle");
        Assert.Equal(200, okCtx.Response.StatusCode);
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
            "Headless test app must disable the vr-control listener (VR_CONTROL_DISABLE=1) so booting the App in tests starts no leaked listener.");
    }

    /// <summary>
    /// Verifies that ControlServer releases its Kestrel socket when Dispose() is
    /// called, so a fresh TcpListener can bind the same port immediately after disposal.
    /// </summary>
    [AvaloniaFact]
    public async Task ControlServer_Dispose_ReleasesListener()
    {
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);

        var server = new ControlServer(api, new ControlServerOptions(Enabled: true, Port: 0, Token: null));
        server.Start();

        var port = server.BoundPort;
        Assert.True(port > 0);

        // Confirm it is listening before dispose.
        var response = await Client.GetAsync($"http://127.0.0.1:{port}/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Dispose must release the port so a fresh TcpListener can bind it. The
        // accept-loop task finishes its socket teardown asynchronously, so re-probe
        // the bind and YIELD (a scheduler turn, not a wall-clock sleep) between tries —
        // the bind succeeds the instant the port is free.
        server.Dispose();

        const int maxRetries = 2_000;
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                using var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                Assert.True(probe.Server.IsBound,
                    "Dispose() must release the listener's port so a fresh TcpListener can bind the same port.");
                probe.Stop();
                return;
            }
            catch (SocketException)
            {
                if (attempt < maxRetries - 1)
                    await Task.Yield();
            }
        }

        Assert.Fail(
            $"Dispose() did not release port {port} within {maxRetries} rebind attempts. " +
            "The accept-loop task may not have completed socket teardown.");
    }
}
