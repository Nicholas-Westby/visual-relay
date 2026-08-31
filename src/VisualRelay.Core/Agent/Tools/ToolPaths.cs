using System.Diagnostics.CodeAnalysis;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// Where a walk started, and how paths beneath it are rendered.
///
/// <see cref="ToolPaths.Display"/> canonicalises the root on every call, which
/// costs two syscalls per path segment. A walk visits thousands of entries, so
/// the walking tools resolve their root once into one of these and render each
/// child by joining strings.
/// </summary>
/// <param name="Shown">The walk's root as the model should type it.</param>
/// <param name="Root">The walk's root as an absolute, already-canonical path.</param>
internal sealed record ToolPathScope(string Shown, string Root)
{
    /// <summary>Renders a path beneath the root.</summary>
    /// <param name="absolute">An absolute path under <see cref="Root"/>.</param>
    /// <returns>The path as the model should type it.</returns>
    internal string Display(string absolute)
    {
        var relative = Path.GetRelativePath(Root, absolute).Replace(Path.DirectorySeparatorChar, '/');
        return Shown == "." ? relative : $"{Shown}/{relative}";
    }
}

/// <summary>
/// Path plumbing shared by every file tool: resolve what the model typed against
/// the run's target root and refuse anything that lands outside it.
///
/// Containment is checked on the CANONICAL path — every segment walked with its
/// symlinks followed — because a symlink inside the tree is otherwise an
/// unguarded door out of it. An absolute path is accepted only when it still
/// canonicalizes inside the root; a relative path with <c>..</c> in it is
/// accepted only when the result stays inside. Both sides of the comparison are
/// canonicalized the same way, so a rooted-at-a-symlink target (macOS
/// <c>/tmp</c>, <c>/var</c>) matches itself rather than reading as an escape.
/// </summary>
internal static class ToolPaths
{
    /// <summary>Directories no listing or search walks into: VCS internals and build output.</summary>
    internal static readonly string[] SkippedDirectories =
        [".git", "bin", "obj", "node_modules", ".vs", ".idea", ".venv", "__pycache__"];

    private const int MaxLinkHops = 64;

    /// <summary>
    /// Resolves a model-supplied path inside the target root.
    /// </summary>
    /// <param name="context">The run, which carries the root.</param>
    /// <param name="path">The path the model typed, relative or absolute.</param>
    /// <param name="absolute">The resolved absolute path, when contained.</param>
    /// <param name="error">A message telling the model how to adapt, when not.</param>
    /// <returns>True when the path is inside the root.</returns>
    internal static bool TryResolve(
        ToolContext context,
        string? path,
        [NotNullWhen(true)] out string? absolute,
        [NotNullWhen(false)] out string? error)
    {
        absolute = null;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "path is required and must not be empty — pass a path relative to the "
                + "repository root, e.g. \"src/Program.cs\". Use list_files to see what exists.";
            return false;
        }

        var root = Canonicalize(context.TargetRoot);
        var trimmed = path.Trim();
        var combined = Path.IsPathRooted(trimmed)
            ? trimmed
            : Path.Combine(context.TargetRoot, trimmed);
        var resolved = Canonicalize(combined);

        if (!IsInside(root, resolved))
        {
            error = $"path escapes the repository root: \"{trimmed}\" resolves to \"{resolved}\", "
                + $"which is outside \"{root}\". Every tool is confined to that root — pass a path "
                + "relative to it (no leading '/', no '..' above the root, and no symlink pointing "
                + "out of the tree). Use list_files to see what is available.";
            return false;
        }

        absolute = resolved;
        return true;
    }

    /// <summary>
    /// Rejects writes into version-control internals. Reading <c>.git</c> is
    /// harmless; a corrupted index is not, and no task is served by editing one.
    /// </summary>
    /// <param name="context">The run, which carries the root.</param>
    /// <param name="absolute">An already-resolved absolute path.</param>
    /// <returns>An error message, or null when the path is safe to modify.</returns>
    internal static string? RejectIfVersionControl(ToolContext context, string absolute)
    {
        var relative = Display(context, absolute);
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase))
            ? $"refusing to modify \"{relative}\": paths under .git are version-control internals. "
              + "Change tracked files instead and let git record the result."
            : null;
    }

    /// <summary>Renders an absolute path the way the model should refer to it.</summary>
    /// <param name="context">The run, which carries the root.</param>
    /// <param name="absolute">The absolute path.</param>
    /// <returns>A root-relative path with forward slashes.</returns>
    internal static string Display(ToolContext context, string absolute)
    {
        var root = Canonicalize(context.TargetRoot);
        var relative = Path.GetRelativePath(root, absolute);
        return relative == "." ? "." : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    // Expands a path to its real location, following a symlink at every segment.
    private static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(prefix)) return full;

        var current = prefix;
        var hops = 0;
        foreach (var segment in full[prefix.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var target = LinkTarget(current);
            if (target is null) continue;

            current = Path.IsPathRooted(target)
                ? Path.GetFullPath(target)
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current) ?? prefix, target));
            if (++hops > MaxLinkHops) break;
        }

        return current;
    }

    private static string? LinkTarget(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? info.ResolveLinkTarget(returnFinalTarget: false)?.FullName;
        }
        catch (IOException)
        {
            // A broken or cyclic link: fall back to the single hop, which is
            // still enough to see that it points outside the root.
            return SingleHop(path);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? SingleHop(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.ResolveLinkTarget(returnFinalTarget: false)?.FullName;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool IsInside(string root, string candidate)
    {
        if (string.Equals(root, candidate, StringComparison.Ordinal)) return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.Ordinal);
    }
}
