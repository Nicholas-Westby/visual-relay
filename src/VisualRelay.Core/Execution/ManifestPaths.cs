using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Core.Execution;

/// <summary>One entry a reader refused, with the reason it is reported under.</summary>
internal sealed record PathEntryDrop(string Entry, string Reason);

/// <summary>
/// One rule for every model-written path list: the plan's manifest, the
/// plan-completeness retry's manifest and Author-tests' <c>testFiles</c>. All three
/// are repo-relative by contract, and a model that writes an absolute path instead
/// used to have its leading <c>/</c> trimmed — leaving a relative name that exists
/// nowhere, dropped in silence by everything that reads the list. An absolute path
/// under the workspace now resolves to the name the model meant; any other one is a
/// rejection the caller reports.
/// </summary>
internal static class ManifestPaths
{
    /// <summary>Why a rooted entry that does not name a file in the workspace is dropped.</summary>
    private const string OutsideWorkspaceReason = "absolute path outside the workspace";

    /// <summary>
    /// Resolves one entry as the stage wrote it.
    /// </summary>
    /// <param name="rootPath">The workspace the stage is running against — during
    /// stages 1 to 4 the planning worktree, which is what a model copying a path out
    /// of its own tool output names.</param>
    /// <param name="entry">The path exactly as the stage declared it.</param>
    /// <param name="repoRelative">The repo-relative name, empty when the entry names
    /// nothing (a blank entry, or the workspace root itself).</param>
    /// <param name="rejection">Why the entry was refused, or null when it was not.</param>
    /// <returns>True when the entry resolved; false when it is a reported drop.</returns>
    internal static bool TryResolve(string rootPath, string entry, out string repoRelative, out string? rejection)
    {
        repoRelative = string.Empty;
        rejection = null;

        // The Plan stage marks a new file with a leading '+', and a model on any host
        // may write backslashes; both are spelling, not location.
        var candidate = entry.StartsWith('+') ? entry[1..] : entry;
        candidate = candidate.Replace('\\', '/');

        if (!IsRooted(candidate))
        {
            repoRelative = WorktreeFilter.NormalizeRepoRelativePath(candidate);
            return true;
        }

        var comparison = HasDriveForm(candidate) || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var full = Canonicalize(candidate);

        foreach (var root in RootSpellings(rootPath))
        {
            // The workspace itself names no file; downstream readers already drop an
            // empty entry, so this is not worth reporting as a refusal.
            if (string.Equals(full, root, comparison))
                return true;

            if (full.Length > root.Length && full[root.Length] == '/'
                && full.AsSpan(0, root.Length).Equals(root, comparison))
            {
                repoRelative = WorktreeFilter.NormalizeRepoRelativePath(full[(root.Length + 1)..]);
                return true;
            }
        }

        rejection = OutsideWorkspaceReason;
        return false;
    }

    /// <summary>
    /// Resolves a whole list, keeping the entries that named a file and collecting the
    /// ones that did not. Blank results are dropped without a rejection, as every
    /// reader of these lists has always done.
    /// </summary>
    internal static (IReadOnlyList<string> Resolved, IReadOnlyList<PathEntryDrop> Dropped) ResolveAll(
        string rootPath, IEnumerable<string> entries)
    {
        var resolved = new List<string>();
        var dropped = new List<PathEntryDrop>();
        foreach (var entry in entries)
        {
            if (TryResolve(rootPath, entry, out var repoRelative, out var rejection))
            {
                if (repoRelative.Length > 0)
                    resolved.Add(repoRelative);
            }
            else
            {
                dropped.Add(new PathEntryDrop(entry, rejection!));
            }
        }

        return (resolved, dropped);
    }

    /// <summary>
    /// Whether a path names a location rather than a file inside the workspace. A
    /// drive letter is never a repo-relative first segment on any host, so the form is
    /// recognised everywhere and a Windows path is judged the same on macOS.
    /// </summary>
    internal static bool IsRooted(string path)
    {
        var candidate = path.Replace('\\', '/');
        return candidate.StartsWith('/') || HasDriveForm(candidate) || Path.IsPathRooted(candidate);
    }

    private static bool HasDriveForm(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/';

    /// <summary>
    /// Every spelling of the workspace an entry may legitimately be written against.
    /// <para>
    /// The root is canonicalized TEXTUALLY, never through <c>Path.GetFullPath</c>: on
    /// Windows that resolves a POSIX-rooted path against the current drive
    /// (<c>/repo</c> becomes <c>C:\repo</c>) while the entry beside it is canonicalized
    /// textually, and the asymmetry rejected every rooted entry as outside the
    /// workspace.
    /// </para>
    /// <para>
    /// On Windows the two sides are also in different NAMESPACES. Visual Relay holds
    /// the workspace as a UNC path into the distro, while a stage runs inside that
    /// distro and writes distro-absolute paths, so
    /// <c>\\wsl.localhost\Ubuntu\home\u\repo</c> and <c>/home/u/repo/src/x.rs</c> name
    /// the same tree and match under neither spelling alone. Both are offered.
    /// </para>
    /// </summary>
    private static IEnumerable<string> RootSpellings(string rootPath)
    {
        var forwardSlashed = rootPath.Replace('\\', '/');
        yield return Canonicalize(forwardSlashed);

        if (WslPath.TryParseUnc(rootPath, out _, out var linuxPath))
            yield return Canonicalize(linuxPath);
    }

    // Collapses "." and ".." segments and repeated separators. The leading separator
    // goes with them, which is harmless: only a root and a rooted entry are ever
    // compared, and both lose it.
    private static string Canonicalize(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == ".." && segments.Count > 0 && segments[^1] != "..")
            {
                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }
}
