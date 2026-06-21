namespace VisualRelay.Core.ObsidianBridge;

/// <summary>
/// Pure path-computation helper for the Obsidian vault layout.
/// Given a vault root and repo name, computes all relevant paths and can scaffold
/// the directory tree with INFO.md guide files.
/// </summary>
public sealed class ObsidianVaultLayout
{
    private readonly string _vaultRoot;
    private readonly string _repoName;

    /// <summary>
    /// Reserved file names (case-insensitive) that must never be treated as tasks.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedFileNames = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "info.md",
        "readme.md"
    };

    // ReSharper disable once ConvertToPrimaryConstructor — the ctor runs
    // SanitizeRepoName over repoName before storing it; a primary ctor cannot
    // express that transform without an extra init member.
    public ObsidianVaultLayout(string vaultRoot, string repoName)
    {
        _vaultRoot = vaultRoot;
        _repoName = SanitizeRepoName(repoName);
    }

    public string RepoDir => Path.Combine(_vaultRoot, _repoName);
    public string NewTasksDir => Path.Combine(RepoDir, "New Tasks");
    public string RecognizedDir => Path.Combine(NewTasksDir, "Recognized");
    private string CompletedRootDir => Path.Combine(RepoDir, "Completed");

    public string CompletedDir(DateOnly date) =>
        Path.Combine(CompletedRootDir, date.ToString("yyyy-MM-dd"));

    public string SummaryPath(string taskId, DateOnly date) =>
        Path.Combine(CompletedDir(date), $"{taskId}.md");

    private static string SanitizeRepoName(string repoName)
    {
        if (string.IsNullOrWhiteSpace(repoName))
            return "project";

        // Strip directory separators (including backslash on Unix for cross-platform safety).
        var sanitized = repoName
            .Replace('\\', '-')
            .Replace('/', '-')
            .Trim();

        return string.IsNullOrWhiteSpace(sanitized) ? "project" : sanitized;
    }

    /// <summary>
    /// Creates all vault directories and seeds an INFO.md into each main folder
    /// when one is absent. Idempotent — never overwrites an existing INFO.md.
    /// </summary>
    public void EnsureScaffold()
    {
        EnsureDirectory(RepoDir);
        EnsureDirectory(NewTasksDir);
        EnsureDirectory(RecognizedDir);
        EnsureDirectory(CompletedRootDir);

        SeedInfoMd(RepoDir, BuildRootInfoMd());
        SeedInfoMd(NewTasksDir, BuildNewTasksInfoMd());
        SeedInfoMd(RecognizedDir, BuildRecognizedInfoMd());
        SeedInfoMd(CompletedRootDir, BuildCompletedInfoMd());
    }

    private static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
    }

    private static void SeedInfoMd(string directory, string content)
    {
        var infoPath = Path.Combine(directory, "INFO.md");
        if (!File.Exists(infoPath))
        {
            File.WriteAllText(infoPath, content);
        }
    }

    // ── INFO.md templates ─────────────────────────────────────────────────

    private string BuildRootInfoMd() => $"""
        # Visual Relay — {_repoName}

        This folder is a remote control for the Visual Relay project **{_repoName}**, synced via iCloud so
        you can use it from any device (including your phone) in Obsidian.

        - **Create a task:** add a markdown file in **New Tasks/**. See that folder's INFO.md.
        - **Watch results:** completed runs appear as summaries in **Completed/**, organized by date.

        Visual Relay manages these folders automatically. You only ever add files to **New Tasks/**.
        """;

    private static string BuildNewTasksInfoMd() => """
        # New Tasks

        Drop a markdown file here to ask Visual Relay to do something. The first `# Heading` (or the file
        name) becomes the task title; the rest is the task description.

        When the app is running and idle it will pick the file up, create the task, and (unless the queue is
        paused) start working on it. Your original file then moves to **Recognized/**, stamped so it is only
        ever taken once. Give a file a few seconds to finish syncing before expecting it to be picked up.

        (This INFO.md is ignored — it never becomes a task.)
        """;

    private static string BuildRecognizedInfoMd() => """
        # Recognized

        These are the original request files after Visual Relay turned them into tasks. Each is stamped with a
        `vr-recognized` id in its frontmatter. They are kept for your reference — safe to leave or delete.
        """;

    private static string BuildCompletedInfoMd() => """
        # Completed

        Auto-generated, read-only summaries of finished runs, in dated subfolders (`YYYY-MM-DD`). Each shows
        the outcome, cost, duration, per-stage results, and the task itself. Editing files here has no effect.
        """;
}
