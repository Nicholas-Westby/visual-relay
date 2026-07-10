namespace VisualRelay.App.Services;

/// <summary>
/// Single source of truth for the HTTP control API surface. Every route the
/// server dispatches is declared here. <see cref="ControlServer.RouteAsync"/>
/// matches incoming requests against the <see cref="Path"/> values in this
/// catalog, and <see cref="ControlIndexPage.Render"/> renders them — so a
/// route cannot exist in the router but be missing from the index page (or
/// vice versa).
/// </summary>
public static class ControlRoutes
{
    public sealed record RouteInfo(string Method, string Path, string DisplayPath, string Summary);

    public static readonly RouteInfo Index = new(
        "GET", "/", "/",
        "HTML index page documenting the API surface (routes and commands).");

    public static readonly RouteInfo Health = new(
        "GET", "/health", "/health",
        "Liveness check with instance identity: {\"status\":\"ok\",\"app\":\"Visual Relay\",\"pid\":...,\"startedUtc\":...,\"version\":...,\"controlPort\":...,\"instanceId\":...}.");

    public static readonly RouteInfo State = new(
        "GET", "/state", "/state",
        "Full state snapshot: instanceId, rootPath, isBusy, tasks[], stages[], commands enabled map.");

    public static readonly RouteInfo Screenshot = new(
        "GET", "/screenshot", "/screenshot[?path=…]",
        "PNG screenshot of the live window; optional ?path= writes the file to disk.");

    public static readonly RouteInfo Command = new(
        "POST", "/command/", "/command/{name}",
        "Invoke a command by name (see Commands list below).");

    /// <summary>Route catalog in display order for the index page.</summary>
    public static readonly IReadOnlyList<RouteInfo> All = [Index, Health, State, Screenshot, Command];
}
