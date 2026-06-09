namespace VisualRelay.Tests;

internal static class RepoSetup
{
    /// <summary>
    /// The repository root (where the visual-relay script lives), resolved by
    /// walking up from the test assembly directory.
    /// </summary>
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "visual-relay")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName
                ?? throw new InvalidOperationException("Could not find repo root from " + AppContext.BaseDirectory);
        }
    }

    /// <summary>
    /// Copies the project's .githooks/pre-commit into the given repo's .git/hooks/
    /// and makes it executable. The hook file must exist; otherwise this throws.
    /// </summary>
    public static void InstallPreCommitHook(string repoRoot)
    {
        var srcPath = Path.Combine(Root, ".githooks", "pre-commit");
        var hooksDir = Path.Combine(repoRoot, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        var destPath = Path.Combine(hooksDir, "pre-commit");
        File.Copy(srcPath, destPath, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
