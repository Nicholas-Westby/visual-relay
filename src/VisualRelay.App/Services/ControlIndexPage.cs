using System.Net;
using System.Text;

namespace VisualRelay.App.Services;

/// <summary>
/// Pure, static, UI-thread-free HTML renderer for the control API index page.
/// Every interpolated value is HTML-encoded with
/// <see cref="WebUtility.HtmlEncode"/>. Route rows and command names come from
/// the caller — this class contains NO hard-coded route path or command name.
/// </summary>
public static class ControlIndexPage
{
    /// <summary>
    /// Renders the complete HTML5 index page document.
    /// </summary>
    /// <param name="routes">The route catalog to render in the endpoints table.</param>
    /// <param name="commandNames">The command names to render in the commands list.</param>
    public static string Render(IReadOnlyList<ControlRoutes.RouteInfo> routes, IReadOnlyList<string> commandNames)
    {
        var sb = new StringBuilder();

        sb.Append("<!DOCTYPE html>\n");
        sb.Append("<html lang=\"en\">\n");
        sb.Append("<head>\n");
        sb.Append("  <meta charset=\"utf-8\">\n");
        sb.Append("  <title>Visual Relay — Control API</title>\n");
        sb.Append("</head>\n");
        sb.Append("<body>\n");
        sb.Append("  <main>\n");
        sb.Append("    <h1>Visual Relay Control API</h1>\n");
        sb.Append("    <p>The Visual Relay control API is a localhost-only HTTP surface for driving the running app the\n");
        sb.Append("       same way its on-screen buttons do: read live state, invoke commands, and capture screenshots.\n");
        sb.Append("       It exists so scripts and agents can automate and observe the GUI with no human at the keyboard.\n");
        sb.Append("       Every command maps to a real UI action and honors the same enabled/disabled gating as the\n");
        sb.Append("       corresponding button.</p>\n");

        sb.Append("\n    <h2>Endpoints</h2>\n");
        sb.Append("    <table>\n");
        sb.Append("      <caption>HTTP endpoints exposed by the control API</caption>\n");
        sb.Append("      <thead>\n");
        sb.Append("        <tr><th scope=\"col\">Method</th><th scope=\"col\">Path</th><th scope=\"col\">Purpose</th></tr>\n");
        sb.Append("      </thead>\n");
        sb.Append("      <tbody>\n");

        foreach (var route in routes)
        {
            sb.Append("        <tr>");
            sb.Append($"<td><code>{WebUtility.HtmlEncode(route.Method)}</code></td>");
            sb.Append($"<td><code>{WebUtility.HtmlEncode(route.DisplayPath)}</code></td>");
            sb.Append($"<td>{WebUtility.HtmlEncode(route.Summary)}</td>");
            sb.Append("</tr>\n");
        }

        sb.Append("      </tbody>\n");
        sb.Append("    </table>\n");

        sb.Append("\n    <h2>Commands</h2>\n");
        sb.Append("    <p>Invoke with <code>POST /command/{name}</code>. Available names:</p>\n");
        sb.Append("    <ul>\n");

        foreach (var name in commandNames)
        {
            sb.Append($"      <li><code>{WebUtility.HtmlEncode(name)}</code></li>\n");
        }

        sb.Append("    </ul>\n");
        sb.Append("  </main>\n");
        sb.Append("</body>\n");
        sb.Append("</html>\n");

        return sb.ToString();
    }
}
