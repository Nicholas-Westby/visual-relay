namespace VisualRelay.GitSim.State;

/// <summary>
/// Helpers over the REAL filesystem under a worktree root — the working tree is
/// actual on-disk files, per the task contract. Enumerations skip the <c>.git</c>
/// metadata dir; paths are returned repo-relative with <c>/</c> separators.
/// </summary>
internal static class WorkingTree
{
    /// <summary>Every file under <paramref name="root"/> (recursive), repo-relative, excluding <c>.git</c>.</summary>
    public static IEnumerable<string> EnumerateFiles(string root)
    {
        if (!Directory.Exists(root))
            yield break;
        foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Rel(root, full);
            if (rel == ".git" || rel.StartsWith(".git/", StringComparison.Ordinal))
                continue;
            yield return rel;
        }
    }

    public static string Rel(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    public static string Full(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public static bool FileExists(string root, string relativePath) => File.Exists(Full(root, relativePath));

    public static bool PathExists(string root, string relativePath)
    {
        var full = Full(root, relativePath);
        return File.Exists(full) || Directory.Exists(full);
    }

    public static byte[] ReadBytes(string root, string relativePath) => File.ReadAllBytes(Full(root, relativePath));

    public static void WriteBytes(string root, string relativePath, byte[] content)
    {
        var full = Full(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    public static void Delete(string root, string relativePath)
    {
        var full = Full(root, relativePath);
        if (File.Exists(full))
            File.Delete(full);
    }

    /// <summary>Git mode for the on-disk file: <c>100755</c> when the owner-exec bit is set, else <c>100644</c>.</summary>
    public static string ModeOnDisk(string root, string relativePath)
    {
        if (OperatingSystem.IsWindows())
            return "100644";
        try
        {
            var mode = File.GetUnixFileMode(Full(root, relativePath));
            return (mode & UnixFileMode.UserExecute) != 0 ? "100755" : "100644";
        }
        catch
        {
            return "100644";
        }
    }

    /// <summary>Stores the on-disk file's content as a blob and returns its sha.</summary>
    public static string StageBlob(GitObjectStore store, string root, string relativePath) =>
        store.PutBlob(new GitBlob(ReadBytes(root, relativePath)));
}
