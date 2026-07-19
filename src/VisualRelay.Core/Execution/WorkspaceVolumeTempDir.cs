namespace VisualRelay.Core.Execution;

/// <summary>
/// Pure path-string helper that detects whether a workspace root lives on an
/// external macOS volume and returns the volume's .TemporaryItems directory.
/// macOS Foundation atomic writes stage temp files in the DESTINATION volume's
/// temporary directory, which for external volumes lives at the volume root —
/// outside any --allow-cwd grant. This helper produces the path that nono needs
/// as an additional -a grant so sandboxed swift build / swiftformat / etc. avoid
/// EPERM (PolicyBlocked on file-write-create).
/// Returns null for system-volume paths and on non-macOS platforms.
/// No filesystem probing — pure string logic — so it stays trivially testable.
/// </summary>
public static class WorkspaceVolumeTempDir
{
    /// <summary>
    /// If <paramref name="rootPath"/> starts with <c>/Volumes/&lt;vol&gt;/</c>
    /// (non-empty volume name) on macOS, returns
    /// <c>/Volumes/&lt;vol&gt;/.TemporaryItems</c>; otherwise returns
    /// <c>null</c>.
    /// </summary>
    public static string? Resolve(string rootPath)
    {
        if (!OperatingSystem.IsMacOS())
            return null;

        // Normalize trailing slashes so "/Volumes/Tera/dev/" behaves the same as
        // "/Volumes/Tera/dev".
        var path = rootPath.TrimEnd('/');

        // Guard: must be under /Volumes/<vol>/... with a non-empty volume name.
        if (!path.StartsWith("/Volumes/", StringComparison.Ordinal))
            return null;

        // Extract everything after the "/Volumes/" prefix.
        var remainder = path.Substring("/Volumes/".Length);

        // Bare "/Volumes" or "/Volumes/" → no volume name → null.
        if (remainder.Length == 0)
            return null;

        // Take the first path component as the volume name.
        var slashIdx = remainder.IndexOf('/');
        var volumeName = slashIdx >= 0 ? remainder.Substring(0, slashIdx) : remainder;

        if (volumeName.Length == 0)
            return null;

        return $"/Volumes/{volumeName}/.TemporaryItems";
    }
}
