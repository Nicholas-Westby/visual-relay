using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
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
        Assert.False(Regex.IsMatch(html, " on[a-z]+=", RegexOptions.IgnoreCase),
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
/// Integration tests for GET / on the control server, running in-memory
/// via <see cref="DefaultHttpContext"/> (no sockets, no ports).
/// Requires the Avalonia headless UI thread (AvaloniaFact, Headless collection)
/// because constructing <see cref="ControlApi"/> needs it.
/// </summary>
[Collection("Headless")]
public sealed class ControlIndexPageTestsEndToEnd
{
    private static async Task<HttpContext> InvokeAsync(
        ControlApi api, ControlServerOptions options,
        string method, string path,
        string? token = null)
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

        await handler(context);
        context.Response.Body.Position = 0;
        return context;
    }

    [AvaloniaFact]
    public async Task IndexPage_Returns200_WithHtmlContentType()
    {
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);
        var options = new ControlServerOptions(Enabled: true, Port: 0, Token: null);

        var context = await InvokeAsync(api, options, "GET", "/");

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", context.Response.ContentType);

        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        Assert.StartsWith("<!doctype html", body, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task IndexPage_WithToken_Returns401WithoutHeader()
    {
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);
        var options = new ControlServerOptions(Enabled: true, Port: 0, Token: "letmein");

        var context = await InvokeAsync(api, options, "GET", "/");

        Assert.Equal(401, context.Response.StatusCode);
    }

    [AvaloniaFact]
    public async Task IndexPage_NonGetOnRoot_Returns404()
    {
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm };
        var api = new ControlApi(vm, window);
        var options = new ControlServerOptions(Enabled: true, Port: 0, Token: null);

        var context = await InvokeAsync(api, options, "POST", "/");

        Assert.Equal(404, context.Response.StatusCode);
    }
}
