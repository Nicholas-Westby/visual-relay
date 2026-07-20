using System.Text;
using Microsoft.AspNetCore.Http;

namespace VisualRelay.App.Services;

public sealed partial class ControlServer
{
    /// <summary>
    /// Creates a transport-agnostic <see cref="RequestDelegate"/> that handles
    /// every control API route. Both the Kestrel host and in-memory tests share
    /// this single handler factory — no sockets, no ports, no HttpListener.
    /// </summary>
    public static RequestDelegate BuildHandler(ControlApi api, ControlServerOptions options)
    {
        return async (context) =>
        {
            try
            {
                await RouteAsync(context, api, options);
            }
            catch (Exception ex)
            {
                await TryWriteErrorAsync(context, ex);
            }
        };
    }

    private static async Task RouteAsync(HttpContext context, ControlApi api, ControlServerOptions options)
    {
        var request = context.Request;

        // Optional token auth: when a token is configured, require a matching
        // X-VR-Token header. /health is also gated so an unauthorized caller
        // learns nothing about the surface.
        if (options.Token is { } token)
        {
            var provided = request.Headers["X-VR-Token"].FirstOrDefault();
            if (!string.Equals(provided, token, StringComparison.Ordinal))
            {
                context.Response.StatusCode = 401;
                await WriteJsonAsync(context, Json.Object(("ok", false), ("error", "unauthorized")));
                return;
            }
        }

        var path = request.Path.Value ?? "/";
        var method = request.Method;

        if (path == ControlRoutes.Index.Path && method == ControlRoutes.Index.Method)
        {
            var html = ControlIndexPage.Render(ControlRoutes.All, ControlApi.CommandNames);
            await WriteHtmlAsync(context, html);
            return;
        }

        if (path == ControlRoutes.Health.Path && method == ControlRoutes.Health.Method)
        {
            await WriteJsonAsync(context, Json.Object(
                ("status", "ok"),
                ("app", "Visual Relay"),
                ("pid", options.Pid),
                ("startedUtc", options.StartedUtc),
                ("version", options.Version),
                ("controlPort", options.ControlPort),
                ("instanceId", options.InstanceId!)));
            return;
        }

        if (path == ControlRoutes.State.Path && method == ControlRoutes.State.Method)
        {
            var json = await api.BuildStateJsonAsync(options.InstanceId);
            await WriteJsonAsync(context, json);
            return;
        }

        if (path == ControlRoutes.Screenshot.Path && method == ControlRoutes.Screenshot.Method)
        {
            await HandleScreenshotAsync(context, api);
            return;
        }

        if (path.StartsWith(ControlRoutes.Command.Path, StringComparison.Ordinal)
            && method == ControlRoutes.Command.Method)
        {
            await HandleCommandAsync(context, api, path);
            return;
        }

        context.Response.StatusCode = 404;
        await WriteJsonAsync(context, Json.Object(("ok", false), ("error", "not found")));
    }

    /// <summary>
    /// Handles a POST /command/{name} request. Under Kestrel (RFC 9112), a
    /// POST with no Content-Length and no Transfer-Encoding has a zero-length
    /// body — it is a valid empty-body request and executes the command. The
    /// old HttpListener 411 guard was removed because Kestrel never writes an
    /// unsolicited error behind the handler's back: "a command never executes
    /// while the client receives an error" holds by construction.
    /// </summary>
    private static async Task HandleCommandAsync(HttpContext context, ControlApi api, string path)
    {
        var name = Uri.UnescapeDataString(path[ControlRoutes.Command.Path.Length..]);

        var body = await ReadBodyAsync(context.Request);

        var (status, json) = await api.InvokeCommandAsync(name, body);
        context.Response.StatusCode = status;
        await WriteJsonAsync(context, json);
    }

    private static async Task HandleScreenshotAsync(HttpContext context, ControlApi api)
    {
        var path = context.Request.Query["path"].FirstOrDefault();
        var (png, writtenPath) = await api.CaptureScreenshotAsync(path);

        if (png is null)
        {
            context.Response.StatusCode = 503;
            await WriteJsonAsync(context, Json.Object(("error", "window unavailable")));
            return;
        }

        if (writtenPath is not null)
        {
            context.Response.Headers["X-Screenshot-Path"] = writtenPath;
        }

        context.Response.StatusCode = 200;
        context.Response.ContentType = "image/png";
        await context.Response.Body.WriteAsync(png);
    }

    private static async Task<string?> ReadBodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }

    private static async Task WriteJsonAsync(HttpContext context, string json)
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(json, Encoding.UTF8);
    }

    private static async Task WriteHtmlAsync(HttpContext context, string html)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html, Encoding.UTF8);
    }

    private static async Task TryWriteErrorAsync(HttpContext context, Exception ex)
    {
        try
        {
            var json = Json.Object(("ok", false), ("error", "internal error"), ("detail", ex.Message));
            context.Response.StatusCode = 500;
            await WriteJsonAsync(context, json);
        }
        catch
        {
            // Nothing more we can do.
        }
    }
}
