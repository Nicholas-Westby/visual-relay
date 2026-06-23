using VisualRelay.Domain;

namespace VisualRelay.Cli.Commands;

/// <summary>
/// <c>bump-version</c>: increments the tracked <c>VERSION</c> file at the repo
/// root and writes the resulting version to stdout. Called by the pre-commit hook
/// on normal developer commits (never during an active Visual Relay run).
/// </summary>
public static class BumpVersionCommand
{
    public static int Run(RepoPaths paths)
    {
        try
        {
            var versionPath = Path.Combine(paths.Root, "VERSION");
            var newVersion = VersionHelper.BumpVersionFile(versionPath);
            Console.WriteLine(newVersion);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"bump-version: {ex.Message}");
            return 1;
        }
    }
}
