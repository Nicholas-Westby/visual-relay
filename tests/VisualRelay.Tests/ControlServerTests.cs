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

    [Theory]
    [InlineData("9100", true)]
    [InlineData("1", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void PortWasExplicitlySet_WhenPortEnvSet_IsTrue(string? portValue, bool expected)
    {
        var env = new DictionaryEnvironmentAccessor();
        if (portValue is not null)
            env["VR_CONTROL_PORT"] = portValue;

        var options = ControlServerOptions.FromEnvironment(env);

        Assert.Equal(expected, options.PortWasExplicitlySet);
    }

    [Fact]
    public void InstanceId_FromEnvironment_IsNonEmptyAndEndsWithProcessId()
    {
        var env = new DictionaryEnvironmentAccessor();

        var options = ControlServerOptions.FromEnvironment(env);

        Assert.NotNull(options.InstanceId);
        Assert.NotEmpty(options.InstanceId);
        Assert.EndsWith("-" + Environment.ProcessId, options.InstanceId);
    }
}

[Collection("Headless")]
public sealed class ControlServerTests
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static (MainWindowViewModel, MainWindow, ControlApi) NewServerDeps()
    {
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        return (vm, window, new ControlApi(vm, window));
    }

    private static ControlServerOptions NewTestOptions(
        int Port = 0, string? Token = null, bool PortWasExplicitlySet = false,
        string? InstanceId = null)
    {
        return new ControlServerOptions(Enabled: true, Port: Port, Token: Token,
            PortWasExplicitlySet: PortWasExplicitlySet, InstanceId: InstanceId,
            Pid: Environment.ProcessId, StartedUtc: DateTime.UtcNow.ToString("o"),
            Version: "0.0-test");
    }

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

    [AvaloniaFact]
    public async Task KestrelSmokeTest_BindsOnPort0_AndServesHealth()
    {
        var (vm, window, api) = NewServerDeps();
        var server = new ControlServer(api, NewTestOptions(Port: 0,
            InstanceId: "smoke-" + Guid.NewGuid().ToString("N") + "-" + Environment.ProcessId));

        server.Start();
        try
        {
            var port = server.BoundPort;
            Assert.True(port > 0);

            var response = await Client.GetAsync($"http://127.0.0.1:{port}/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("Visual Relay", doc.RootElement.GetProperty("app").GetString());
            Assert.True(doc.RootElement.TryGetProperty("pid", out var pid));
            Assert.True(pid.GetInt32() > 0);
            Assert.True(doc.RootElement.TryGetProperty("startedUtc", out var su));
            Assert.False(string.IsNullOrEmpty(su.GetString()));
            Assert.True(doc.RootElement.TryGetProperty("version", out var ver));
            Assert.False(string.IsNullOrEmpty(ver.GetString()));
            Assert.True(doc.RootElement.TryGetProperty("controlPort", out var cp));
            Assert.Equal(port, cp.GetInt32());
            Assert.True(doc.RootElement.TryGetProperty("instanceId", out var iid));
            Assert.EndsWith("-" + Environment.ProcessId, iid.GetString());
        }
        finally { server.Stop(); }
    }

    [AvaloniaFact]
    public async Task Token_WhenConfigured_RejectsMissingHeaderWith401_AndAcceptsMatch()
    {
        var (_, _, api) = NewServerDeps();
        var options = NewTestOptions(Token: "letmein");

        var noTok = await InvokeAsync(api, options, "GET", "/health");
        Assert.Equal(401, noTok.Response.StatusCode);

        var ok = await InvokeAsync(api, options, "GET", "/health", token: "letmein");
        Assert.Equal(200, ok.Response.StatusCode);
    }

    [AvaloniaFact]
    public async Task StateAndCommand_RoundTrip_GatesDisabledCommandWith409()
    {
        var (_, _, api) = NewServerDeps();
        var options = NewTestOptions();

        var stateCtx = await InvokeAsync(api, options, "GET", "/state");
        Assert.Equal(200, stateCtx.Response.StatusCode);
        stateCtx.Response.Body.Position = 0;
        using (var reader = new StreamReader(stateCtx.Response.Body, Encoding.UTF8, leaveOpen: true))
        {
            var state = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(state);
            Assert.True(doc.RootElement.TryGetProperty("commands", out _));
        }

        var disabledCtx = await InvokeAsync(api, options, "POST", "/command/run-selected");
        Assert.Equal(409, disabledCtx.Response.StatusCode);

        var okCtx = await InvokeAsync(api, options, "POST", "/command/pause-toggle");
        Assert.Equal(200, okCtx.Response.StatusCode);
    }

    [AvaloniaFact]
    public void HeadlessApp_DisablesControlServer_ViaProcessEnv()
    {
        var options = ControlServerOptions.FromEnvironment(new ProcessEnvironmentAccessor());
        Assert.False(options.Enabled);
    }

    [AvaloniaFact]
    public async Task ControlServer_Dispose_ReleasesListener()
    {
        var (_, _, api) = NewServerDeps();
        var server = new ControlServer(api, NewTestOptions());
        server.Start();

        var port = server.BoundPort;
        Assert.True(port > 0);

        var response = await Client.GetAsync($"http://127.0.0.1:{port}/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        server.Dispose();

        const int maxRetries = 2_000;
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                using var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                Assert.True(probe.Server.IsBound);
                probe.Stop();
                return;
            }
            catch (SocketException)
            {
                if (attempt < maxRetries - 1) await Task.Yield();
            }
        }
        Assert.Fail($"Dispose() did not release port {port} within {maxRetries} rebind attempts.");
    }

    [AvaloniaFact]
    public async Task BindConflict_WithExplicitPort_Throws()
    {
        using var occupier = new TcpListener(IPAddress.Loopback, 0);
        occupier.Start();
        var occupiedPort = ((IPEndPoint)occupier.LocalEndpoint).Port;

        var (_, _, api) = NewServerDeps();
        var options = NewTestOptions(Port: occupiedPort, PortWasExplicitlySet: true);
        var server = new ControlServer(api, options);

        var ex = Assert.ThrowsAny<Exception>(() => server.Start());
        Assert.Contains("port", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(server.IsAvailable);
    }
    /// VR_CONTROL_PORT explicitly set → bind conflict throws. Without it,
    /// startup continues banner-only.
    [AvaloniaFact]
    public async Task BindConflict_WithoutExplicitPort_DoesNotThrow_AndIsAvailableIsFalse()
    {
        using var occupier = new TcpListener(IPAddress.Loopback, 0);
        occupier.Start();
        var occupiedPort = ((IPEndPoint)occupier.LocalEndpoint).Port;

        var (vm, _, api) = NewServerDeps();
        var options = NewTestOptions(Port: occupiedPort);
        var server = new ControlServer(api, options);

        var ex = Record.Exception(() => server.Start());
        Assert.Null(ex);
        Assert.False(server.IsAvailable);

        // VM banner property exists for the main window's persistent error bar.
        vm.ControlApiUnavailableBanner = server.IsAvailable
            ? null
            : $"Control API unavailable — port {options.Port} in use by another process";
        Assert.NotNull(vm.ControlApiUnavailableBanner);
        Assert.Contains(options.Port.ToString(), vm.ControlApiUnavailableBanner);
    }
}
