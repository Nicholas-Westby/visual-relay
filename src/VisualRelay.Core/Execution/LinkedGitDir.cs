namespace VisualRelay.Core.Execution;

/// <summary>
/// What a workspace whose <c>.git</c> is a file must be able to read outside itself: the main
/// worktree of a linked worktree (its git dir, and the dependencies a verify snapshot links to),
/// or a submodule's own git dir.
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

        // <common>/worktrees/<name> shares <common>, and a common dir named .git sits in the
        // main worktree, which is granted whole: a verify snapshot links its large ignored
        // dependencies there. Anything else (a submodule) is its own git dir.
        var name = ParentOf(gitDir);
        if (name is null || LastSegment(name) != "worktrees")
            return gitDir;
        var common = ParentOf(name);
        return common is not null && LastSegment(common) == ".git" ? ParentOf(common) ?? common : common;
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
