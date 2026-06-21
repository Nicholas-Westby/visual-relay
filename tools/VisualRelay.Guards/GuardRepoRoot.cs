namespace VisualRelay.Guards;

/// <summary>
/// Resolves the repository root by walking up from the current directory to the
/// directory that holds the <c>visual-relay</c> launcher script. Returns
/// <c>null</c> when no such directory is found.
/// </summary>
public static class GuardRepoRoot
{
    public static string? Resolve()
    {
        var dir = Environment.CurrentDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "visual-relay")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir;
    }
}
