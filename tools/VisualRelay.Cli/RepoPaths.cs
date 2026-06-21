namespace VisualRelay.Cli;

/// <summary>
/// Resolves the repository root the CLI operates on. The bash bootstrap exports
/// <c>VISUAL_RELAY_SCRIPT_DIR</c> (the launcher's own resolved directory) before
/// exec-ing the CLI; when absent (e.g. unit context) we walk up from the current
/// directory to the directory that holds the <c>visual-relay</c> script. The
/// caller's original working directory is forwarded as <c>ORIGINAL_CWD</c>.
/// </summary>
public sealed class RepoPaths
{
    public string Root { get; }
    public string OriginalCwd { get; }

    private RepoPaths(string root, string originalCwd)
    {
        Root = root;
        OriginalCwd = originalCwd;
    }

    public static RepoPaths Resolve()
    {
        var scriptDir = Environment.GetEnvironmentVariable("VISUAL_RELAY_SCRIPT_DIR");
        var root = !string.IsNullOrEmpty(scriptDir) && Directory.Exists(scriptDir)
            ? Path.GetFullPath(scriptDir)
            : WalkUpForLauncher();

        var originalCwd = Environment.GetEnvironmentVariable("ORIGINAL_CWD");
        if (string.IsNullOrEmpty(originalCwd))
            originalCwd = Directory.GetCurrentDirectory();

        return new RepoPaths(root, originalCwd);
    }

    private static string WalkUpForLauncher()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "visual-relay")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? Directory.GetCurrentDirectory();
    }

    // ── Project / script locations (absolute, anchored at Root) ──
    public string Solution => Path.Combine(Root, "VisualRelay.slnx");
    public string AppProject => Path.Combine(Root, "src", "VisualRelay.App", "VisualRelay.App.csproj");
    public string TestsProject => Path.Combine(Root, "tests", "VisualRelay.Tests", "VisualRelay.Tests.csproj");
    public string ToolProject(string name) => Path.Combine(Root, "tools", name, name + ".csproj");
    public string ToolManifest => Path.Combine(Root, ".config", "dotnet-tools.json");
    public string GitHooksDir => Path.Combine(Root, ".githooks");
    public string CheckCommitMessageOut => Path.Combine(Root, "check-commit-message");
    public string DocsImage(string name) => Path.Combine(Root, "docs", "images", name);
}
