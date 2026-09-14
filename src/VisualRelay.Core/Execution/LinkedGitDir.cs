namespace VisualRelay.Core.Execution;

/// <summary>
/// The git directory a workspace whose <c>.git</c> is a file reads its objects and refs from.
/// A linked worktree (VR's planning, verify and rewrite worktrees) points at
/// <c>&lt;repo&gt;/.git/worktrees/&lt;name&gt;</c>, which shares the main repository's git dir;
/// a submodule points at its own git dir. Both lie outside the workspace, so a sandbox that
/// grants only the workspace leaves git inside it unable to find the repository.
/// </summary>
internal static class LinkedGitDir
{
    private const string GitDirPrefix = "gitdir:";

    /// <summary>
    /// The git dir to grant read for <paramref name="workspaceRoot"/>, or null when its
    /// <c>.git</c> is a directory, missing, or unreadable. Absolute Linux paths (written by the
    /// distro's git) are kept as they are; a relative path is resolved against the workspace.
    /// </summary>
    internal static string? For(string workspaceRoot)
    {
        var dotGit = Path.Combine(workspaceRoot, ".git");
        string text;
        try
        {
            if (!File.Exists(dotGit))
                return null;
            text = File.ReadAllText(dotGit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(GitDirPrefix, StringComparison.Ordinal));
        if (line is null)
            return null;
        var gitDir = line[GitDirPrefix.Length..].Trim();
        if (gitDir.Length == 0)
            return null;
        if (!gitDir.StartsWith('/') && !Path.IsPathRooted(gitDir))
            gitDir = Path.GetFullPath(Path.Combine(workspaceRoot, gitDir));

        // <common>/worktrees/<name> shares <common>; anything else is its own git dir.
        var name = ParentOf(gitDir);
        return name is not null && LastSegment(name) == "worktrees" ? ParentOf(name) : gitDir;
    }

    // Separator-agnostic, so a Linux path handled on Windows keeps its forward slashes.
    private static string? ParentOf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut > 0 ? trimmed[..cut] : null;
    }

    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        return trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
    }
}
