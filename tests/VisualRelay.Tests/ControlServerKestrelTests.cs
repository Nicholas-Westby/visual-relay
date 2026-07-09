using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;

namespace VisualRelay.Tests;

/// <summary>
/// In-memory handler tests for screenshot and state routes, running on
/// the Avalonia headless UI thread because ControlApi marshals through
/// Dispatcher.UIThread.
/// </summary>
[Collection("Headless")]
public sealed class ScreenshotAndStateTests
{
    private static async Task<(HttpContext Context, ControlApi Api, MainWindowViewModel Vm)> InvokeOnUiAsync(
        string method, string path, string? token = null)
    {
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);
        var options = new ControlServerOptions(Enabled: true, Port: 0, Token: token);
        var handler = ControlServer.BuildHandler(api, options);

        var context = new DefaultHttpContext
        {
            Request = { Method = method, Path = path },
            Response = { Body = new MemoryStream() }
        };

        await handler(context);
        context.Response.Body.Position = 0;
        return (context, api, vm);
    }

    [AvaloniaFact]
    public async Task Screenshot_ReturnsPngContentType()
    {
        var (context, _, _) = await InvokeOnUiAsync("GET", "/screenshot");

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("image/png", context.Response.ContentType);
    }

    [AvaloniaFact]
    public async Task Screenshot_WithPath_ReturnsXScreenshotPathHeader()
    {
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);
        var options = new ControlServerOptions(Enabled: true, Port: 0, Token: null);
        var handler = ControlServer.BuildHandler(api, options);

        var targetPath = Path.Combine(Path.GetTempPath(), "vr-kestrel-test-shot.png");
        try
        {
            var context = new DefaultHttpContext
            {
                Request =
                {
                    Method = "GET",
                    Path = "/screenshot",
                    QueryString = new QueryString($"?path={Uri.EscapeDataString(targetPath)}")
                },
                Response = { Body = new MemoryStream() }
            };

            await handler(context);
            context.Response.Body.Position = 0;

            Assert.Equal(200, context.Response.StatusCode);
            Assert.Equal("image/png", context.Response.ContentType);

            Assert.True(context.Response.Headers.TryGetValue("X-Screenshot-Path", out var headerValues));
            var resolvedPath = headerValues.FirstOrDefault();
            Assert.NotNull(resolvedPath);
            Assert.Equal(Path.GetFullPath(targetPath), resolvedPath);
        }
        finally
        {
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
        }
    }

    [AvaloniaFact]
    public async Task State_Returns200_WithJsonBody()
    {
        var (context, _, _) = await InvokeOnUiAsync("GET", "/state");

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("commands", out _));
    }
}

/// <summary>
/// In-memory handler tests for POST /command/{name} routes, running on the
/// Avalonia headless UI thread because ControlApi.InvokeCommandAsync
/// marshals through Dispatcher.UIThread.
/// </summary>
[Collection("Headless")]
public sealed class CommandTests
{
    private static async Task<(HttpContext Context, MainWindowViewModel Vm)> InvokeCommandAsync(
        string commandName, string? body = null, string? token = null,
        Dictionary<string, string>? extraHeaders = null)
    {
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);
        var options = new ControlServerOptions(Enabled: true, Port: 0, Token: token);
        var handler = ControlServer.BuildHandler(api, options);

        var context = new DefaultHttpContext
        {
            Request = { Method = "POST", Path = $"/command/{commandName}" },
            Response = { Body = new MemoryStream() }
        };

        if (extraHeaders is not null)
        {
            foreach (var (key, value) in extraHeaders)
            {
                context.Request.Headers[key] = value;
            }
        }

        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.Headers.ContentLength = bytes.Length;
        }

        await handler(context);
        context.Response.Body.Position = 0;
        return (context, vm);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        var bodyString = await reader.ReadToEndAsync();
        return JsonDocument.Parse(bodyString);
    }

    // ── Bodyless POST (the key behavioural change) ──────────────────────

    /// <summary>
    /// Under Kestrel (RFC 9112), a POST with no Content-Length is a
    /// valid empty-body request — the command MUST execute and return 200.
    /// This is the deliberate semantic change from the old HttpListener
    /// 411 guard.
    /// </summary>
    [AvaloniaFact]
    public async Task BodylessPost_NoContentLength_Returns200_AndExecutesCommand()
    {
        var (context, vm) = await InvokeCommandAsync("pause-toggle");

        Assert.Equal(200, context.Response.StatusCode);

        using var doc = await ReadJsonAsync(context);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("pause-toggle", doc.RootElement.GetProperty("command").GetString());

        // Command executed: PauseRequested flipped.
        Assert.True(vm.PauseRequested,
            "A bodyless POST must execute the command. PauseRequested should be true.");
    }

    /// <summary>
    /// A POST with Content-Length: 0 is semantically identical to a
    /// bodyless POST under Kestrel — both are valid empty-body requests.
    /// </summary>
    [AvaloniaFact]
    public async Task ContentLengthZero_Post_Returns200_AndExecutesCommand()
    {
        var (context, vm) = await InvokeCommandAsync(
            "pause-toggle",
            extraHeaders: new Dictionary<string, string> { ["Content-Length"] = "0" });

        Assert.Equal(200, context.Response.StatusCode);

        using var doc = await ReadJsonAsync(context);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

        Assert.True(vm.PauseRequested,
            "A Content-Length: 0 POST must execute the command. PauseRequested should be true.");
    }

    // ── Disabled command ────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task DisabledCommand_Returns409()
    {
        // run-selected is disabled when no task is selected.
        var (context, _) = await InvokeCommandAsync("run-selected");

        Assert.Equal(409, context.Response.StatusCode);

        using var doc = await ReadJsonAsync(context);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("disabled", doc.RootElement.GetProperty("error").GetString());
    }

    // ── Unknown command ─────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task UnknownCommand_Returns404()
    {
        var (context, _) = await InvokeCommandAsync("nonexistent-command");

        Assert.Equal(404, context.Response.StatusCode);

        using var doc = await ReadJsonAsync(context);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown command", doc.RootElement.GetProperty("error").GetString());
    }

    // ── Confirm-gated command ───────────────────────────────────────────

    [AvaloniaFact]
    public async Task ConfirmGatedCommand_WithoutConfirm_Returns409_AndNoEffect()
    {
        // mark-done requires {"confirm":true} — an empty-body POST
        // does not carry confirm, so the command must be refused.
        var (context, _) = await InvokeCommandAsync("mark-done");

        Assert.Equal(409, context.Response.StatusCode);

        using var doc = await ReadJsonAsync(context);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        // Without a selected task mark-done is disabled (the task-selection
        // gate fires before the confirm gate). Either gate returning 409
        // proves the command was refused, which is what matters.
        Assert.Equal("disabled", doc.RootElement.GetProperty("error").GetString());

        // No task was selected, and the command was gated BEFORE execution —
        // the safety hold is intact.
    }

    // ── Token auth on commands ──────────────────────────────────────────

    [AvaloniaFact]
    public async Task Command_WithToken_Returns401_WithoutHeader()
    {
        var (context, _) = await InvokeCommandAsync("pause-toggle", token: "s3cret");

        Assert.Equal(401, context.Response.StatusCode);

        using var doc = await ReadJsonAsync(context);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("unauthorized", doc.RootElement.GetProperty("error").GetString());
    }

    [AvaloniaFact]
    public async Task Command_WithToken_Returns200_WithCorrectHeader()
    {
        var (context, vm) = await InvokeCommandAsync(
            "pause-toggle", token: "letmein",
            extraHeaders: new Dictionary<string, string> { ["X-VR-Token"] = "letmein" });

        Assert.Equal(200, context.Response.StatusCode);
        Assert.True(vm.PauseRequested);
    }
}
