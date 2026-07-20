using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
namespace VisualRelay.Tests;

/// <summary>
/// In-memory handler tests for the basic control API surface (health, token
/// auth, 404, index page) — no sockets, no ports. The handler
/// (<see cref="ControlServer.BuildHandler"/>) operates on
/// <see cref="HttpContext"/> and is exercised via
/// <see cref="DefaultHttpContext"/>.
/// </summary>
public sealed class ControlServerKestrelHandlerTests
{
    private static async Task<(HttpContext Context, ControlApi Api, MainWindowViewModel Vm)> InvokeAsync(
        string method, string path, string? token = null, string? requestBody = null,
        Dictionary<string, string>? requestHeaders = null)
    {
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var api = new ControlApi(vm);
        var options = new ControlServerOptions(Enabled: true, Port: 0, Token: token,
            InstanceId: "h-" + Guid.NewGuid().ToString("N"),
            Pid: Environment.ProcessId,
            StartedUtc: DateTime.UtcNow.ToString("o"),
            Version: "0.0-hdlr-test");
        var handler = ControlServer.BuildHandler(api, options);

        var context = new DefaultHttpContext
        {
            Request = { Method = method, Path = path },
            Response = { Body = new MemoryStream() }
        };

        if (requestHeaders is not null)
        {
            foreach (var (key, value) in requestHeaders)
            {
                context.Request.Headers[key] = value;
            }
        }

        if (requestBody is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(requestBody);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.Headers.ContentLength = bytes.Length;
        }

        await handler(context);
        context.Response.Body.Position = 0;
        return (context, api, vm);
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static async Task<JsonDocument> ReadJsonBodyAsync(HttpContext context)
    {
        var body = await ReadBodyAsync(context);
        return JsonDocument.Parse(body);
    }

    // ── Health ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HealthEndpoint_Returns200_WithJsonBody()
    {
        var (context, _, _) = await InvokeAsync("GET", "/health");

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        using var doc = await ReadJsonBodyAsync(context);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("Visual Relay", doc.RootElement.GetProperty("app").GetString());

        // Instance identity fields.
        Assert.True(doc.RootElement.TryGetProperty("pid", out var pid), "/health must include pid");
        Assert.True(pid.GetInt32() > 0, "pid must be a positive integer");
        Assert.True(doc.RootElement.TryGetProperty("startedUtc", out var startedUtc), "/health must include startedUtc");
        Assert.False(string.IsNullOrEmpty(startedUtc.GetString()), "startedUtc must be non-empty");
        Assert.True(doc.RootElement.TryGetProperty("version", out var version), "/health must include version");
        Assert.False(string.IsNullOrEmpty(version.GetString()), "version must be non-empty");
        Assert.True(doc.RootElement.TryGetProperty("controlPort", out var controlPort), "/health must include controlPort");
        Assert.Equal(0, controlPort.GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("instanceId", out var instanceId), "/health must include instanceId");
        Assert.False(string.IsNullOrEmpty(instanceId.GetString()), "instanceId must be non-empty");
    }

    // ── Token auth ──────────────────────────────────────────────────────

    [Fact]
    public async Task Token_WhenConfigured_Returns401_WithoutHeader()
    {
        var (context, _, _) = await InvokeAsync("GET", "/health", token: "letmein");

        Assert.Equal(401, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        using var doc = await ReadJsonBodyAsync(context);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("unauthorized", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Token_WhenConfigured_Returns200_WithCorrectHeader()
    {
        var (context, _, _) = await InvokeAsync(
            "GET", "/health", token: "letmein",
            requestHeaders: new Dictionary<string, string> { ["X-VR-Token"] = "letmein" });

        Assert.Equal(200, context.Response.StatusCode);

        using var doc = await ReadJsonBodyAsync(context);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Token_GatesAllRoutes_IncludingHealth()
    {
        // /health is also gated — an unauthorized caller learns nothing.
        var (context, _, _) = await InvokeAsync("GET", "/health", token: "s3cret");

        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task Token_WithoutToken_AllRoutesSucceed()
    {
        // No token configured → no auth; every route passes through.
        var (context, _, _) = await InvokeAsync("GET", "/health", token: null);

        Assert.Equal(200, context.Response.StatusCode);
    }

    // ── 404 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownRoute_Returns404_Json()
    {
        var (context, _, _) = await InvokeAsync("GET", "/nonexistent");

        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        using var doc = await ReadJsonBodyAsync(context);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("not found", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WrongMethodOnRoute_Returns404()
    {
        // POST /health is not a valid route (only GET).
        var (context, _, _) = await InvokeAsync("POST", "/health");

        Assert.Equal(404, context.Response.StatusCode);
    }

    // ── Index page ──────────────────────────────────────────────────────

    [Fact]
    public async Task IndexPage_Returns200_WithHtmlContentType()
    {
        var (context, _, _) = await InvokeAsync("GET", "/");

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", context.Response.ContentType);

        var body = await ReadBodyAsync(context);
        Assert.StartsWith("<!DOCTYPE html>", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<title>Visual Relay — Control API</title>", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndexPage_NonGet_Returns404()
    {
        var (context, _, _) = await InvokeAsync("POST", "/");

        Assert.Equal(404, context.Response.StatusCode);
    }

    // ── Screenshot without window ────────────────────────────────────────

    [Fact]
    public async Task Screenshot_WithoutWindow_Returns503_WithErrorBody()
    {
        var (context, _, _) = await InvokeAsync("GET", "/screenshot");

        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        using var doc = await ReadJsonBodyAsync(context);
        Assert.Equal("window unavailable", doc.RootElement.GetProperty("error").GetString());
    }
}
