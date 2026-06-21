namespace VisualRelay.Cli.Commands;

/// <summary>
/// <c>screenshot</c>: renders the two README screenshots via the Screenshots
/// tool (Avalonia Headless) — main, then compact at 1060x720.
/// </summary>
public static class ScreenshotCommand
{
    public static int Run(RepoPaths paths)
    {
        var proj = paths.ToolProject("VisualRelay.Screenshots");

        var main = ProcessLauncher.Run(ProcessLauncher.Dotnet,
            ["run", "--project", proj, "--", paths.DocsImage("visual-relay-main.png")], paths.Root);
        if (main != 0)
            return main;

        return ProcessLauncher.Run(ProcessLauncher.Dotnet,
            ["run", "--project", proj, "--", paths.DocsImage("visual-relay-compact.png"), "1060", "720"],
            paths.Root);
    }
}
