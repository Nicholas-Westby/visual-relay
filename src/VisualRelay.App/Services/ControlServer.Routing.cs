using System.Net;
using System.Text;

namespace VisualRelay.App.Services;

public sealed partial class ControlServer
{
    private async Task RouteAsync(HttpListenerContext context)
    {
        var request = context.Request;

        // Optional token auth: when a token is configured, require a matching
        // X-VR-Token header. /health is also gated so an unauthorized caller
        // learns nothing about the surface.
        if (options.Token is { } token)
        {
            var provided = request.Headers["X-VR-Token"];
            if (!string.Equals(provided, token, StringComparison.Ordinal))
            {
                context.Response.StatusCode = 401;
                await WriteJsonAsync(context, Json.Object(("ok", false), ("error", "unauthorized")));
                return;
            }
        }

        var path = request.Url?.AbsolutePath ?? "/";
        var method = request.HttpMethod;

        if (path == ControlRoutes.Index.Path && method == ControlRoutes.Index.Method)
        {
            var html = ControlIndexPage.Render(ControlRoutes.All, ControlApi.CommandNames);
            await WriteHtmlAsync(context, html);
            return;
        }

        if (path == ControlRoutes.Health.Path && method == ControlRoutes.Health.Method)
        {
            await WriteJsonAsync(context, Json.Object(("status", "ok"), ("app", "Visual Relay")));
            return;
        }

        if (path == ControlRoutes.State.Path && method == ControlRoutes.State.Method)
        {
            var json = await api.BuildStateJsonAsync();
            await WriteJsonAsync(context, json);
            return;
        }

        if (path == ControlRoutes.Screenshot.Path && method == ControlRoutes.Screenshot.Method)
        {
            await HandleScreenshotAsync(context, request);
            return;
        }

        if (path.StartsWith(ControlRoutes.Command.Path, StringComparison.Ordinal) && method == ControlRoutes.Command.Method)
        {
            await HandleCommandAsync(context, request, path);
            return;
        }

        context.Response.StatusCode = 404;
        await WriteJsonAsync(context, Json.Object(("ok", false), ("error", "not found")));
    }

    private async Task HandleCommandAsync(HttpListenerContext context, HttpListenerRequest request, string path)
    {
        var name = Uri.UnescapeDataString(path[ControlRoutes.Command.Path.Length..]);
        var body = await ReadBodyAsync(request);

        // Refuse a POST with no declared body length. On macOS/Linux HttpListener
        // internally 411s this shape, but the request context IS still dispatched
        // to the handler — so the command would execute behind a 411 error page.
        // Detect the distinguishing shape (ContentLength64 == -1, no chunked
        // transfer-encoding indicated by HasEntityBody == false) and reject
        // BEFORE executing the command.
        if (request.ContentLength64 < 0 && !request.HasEntityBody)
        {
            context.Response.StatusCode = 411;
            await WriteJsonAsync(context, Json.Object(("ok", false), ("error", "length required")));
            return;
        }

        var (status, json) = await api.InvokeCommandAsync(name, body);
        context.Response.StatusCode = status;
        await WriteJsonAsync(context, json);
    }

    private async Task HandleScreenshotAsync(HttpListenerContext context, HttpListenerRequest request)
    {
        var path = request.QueryString["path"];
        var (png, writtenPath) = await api.CaptureScreenshotAsync(path);

        if (writtenPath is not null)
        {
            context.Response.Headers["X-Screenshot-Path"] = writtenPath;
        }

        context.Response.StatusCode = 200;
        context.Response.ContentType = "image/png";
        context.Response.ContentLength64 = png.Length;
        await context.Response.OutputStream.WriteAsync(png);
    }

    private static async Task<string?> ReadBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody)
        {
            return null;
        }

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    private static async Task WriteHtmlAsync(HttpListenerContext context, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }
}
