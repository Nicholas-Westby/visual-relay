using System.Net;
using System.Text.RegularExpressions;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;

namespace VisualRelay.Tests;

/// <summary>
/// Pure renderer tests for <see cref="ControlIndexPage.Render"/> —
/// derivation, no-CSS/JS tripwires, and structure assertions.
/// No Avalonia UI thread needed; these are plain [Fact].
/// </summary>
public sealed class ControlIndexPageTests
{
    [Fact]
    public void Render_ContainsEveryCommandName()
    {
        var html = ControlIndexPage.Render(ControlRoutes.All, ControlApi.CommandNames);

        foreach (var name in ControlApi.CommandNames)
        {
            Assert.Contains($"<code>{name}</code>", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Render_ContainsEveryRoute()
    {
        var html = ControlIndexPage.Render(ControlRoutes.All, ControlApi.CommandNames);

        foreach (var route in ControlRoutes.All)
        {
            Assert.Contains($"<code>{route.Method}</code>", html, StringComparison.Ordinal);
            Assert.Contains($"<code>{route.DisplayPath}</code>", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Render_HasNoCss()
    {
        var html = ControlIndexPage.Render(ControlRoutes.All, ControlApi.CommandNames);

        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stylesheet", html, StringComparison.OrdinalIgnoreCase);
        Assert.False(Regex.IsMatch(html, " style=", RegexOptions.IgnoreCase),
            "Output must not contain any style= attribute.");
    }

    [Fact]
    public void Render_HasNoJavaScript()
    {
        var html = ControlIndexPage.Render(ControlRoutes.All, ControlApi.CommandNames);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.False(Regex.IsMatch(html, @" on[a-z]+=", RegexOptions.IgnoreCase),
            "Output must not contain any inline event-handler attribute like onclick=.");
    }

    [Fact]
    public void Render_HasValidStructure()
    {
        var html = ControlIndexPage.Render(ControlRoutes.All, ControlApi.CommandNames);

        Assert.StartsWith("<!doctype html", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<html lang=", html, StringComparison.Ordinal);
        Assert.Contains("<title>", html, StringComparison.Ordinal);
        Assert.Contains("<main", html, StringComparison.Ordinal);
        Assert.Contains("<caption>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scope=\"col\"", html, StringComparison.Ordinal);

        // Exactly one <h1>.
        var h1Count = CountTag(html, "h1");
        Assert.Equal(1, h1Count);

        // At least one <h2>.
        var h2Count = CountTag(html, "h2");
        Assert.True(h2Count >= 1, "Expected at least one <h2>.");
    }

    private static int CountTag(string html, string tag)
    {
        var pattern = $@"<{tag}[\s>]";
        return Regex.Matches(html, pattern, RegexOptions.IgnoreCase).Count;
    }
}

/// <summary>
/// End-to-end round-trip tests for GET / on the control server.
/// Requires the Avalonia headless UI thread (AvaloniaFact, Headless collection).
/// </summary>
[Collection("Headless")]
public sealed class ControlIndexPageTestsEndToEnd
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(5) };

    [AvaloniaFact]
    public async Task IndexPage_Returns200_WithHtmlContentType()
    {
        var port = GetFreePort();
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);
        var server = new ControlServer(api, new ControlServerOptions(Enabled: true, Port: port, Token: null));

        server.Start();
        try
        {
            var response = await Client.GetAsync($"http://127.0.0.1:{port}/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);

            var body = await response.Content.ReadAsStringAsync();
            Assert.StartsWith("<!doctype html", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            server.Stop();
        }
    }

    [AvaloniaFact]
    public async Task IndexPage_WithToken_Returns401WithoutHeader()
    {
        var port = GetFreePort();
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);
        var server = new ControlServer(api, new ControlServerOptions(Enabled: true, Port: port, Token: "letmein"));

        server.Start();
        try
        {
            var noTok = await Client.GetAsync($"http://127.0.0.1:{port}/");
            Assert.Equal(HttpStatusCode.Unauthorized, noTok.StatusCode);
        }
        finally
        {
            server.Stop();
        }
    }

    [AvaloniaFact]
    public async Task IndexPage_NonGetOnRoot_Returns404()
    {
        var port = GetFreePort();
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);
        var server = new ControlServer(api, new ControlServerOptions(Enabled: true, Port: port, Token: null));

        server.Start();
        try
        {
            var post = await Client.PostAsync($"http://127.0.0.1:{port}/", null);
            Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
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
