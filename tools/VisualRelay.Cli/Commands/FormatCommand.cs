using VisualRelay.Core.Execution;
using VisualRelay.Guards;

namespace VisualRelay.Cli.Commands;

/// <summary>
/// <c>format</c>: <c>dotnet format</c> over the solution, then <c>shfmt --write</c>
/// over every git-tracked shell script. Both steps apply formatting; the <c>check</c>
/// gate verifies both with <c>--verify-no-changes</c> / <c>--diff</c>.
/// </summary>
public static class FormatCommand
{
    public static async Task<int> RunAsync(RepoPaths paths, IReadOnlyList<string> args)
    {
        var formatArgs = new List<string> { "format", paths.Solution };
        formatArgs.AddRange(args);
        var dotnetRc = ProcessLauncher.Run(ProcessLauncher.Dotnet, formatArgs, paths.Root);
        if (dotnetRc != 0)
            return dotnetRc;

        List<(string Path, string[] Lines)> tracked;
        try
        {
            tracked = await TrackedShellScripts.EnumerateAsync(paths.Root, new GitInvoker());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"format: {ex.Message}");
            return 1;
        }

        if (tracked.Count == 0)
            return 0;

        var shellPaths = tracked.Select(t => t.Path).ToArray();
        return ProcessLauncher.Run("shfmt", ["--write", "--", .. shellPaths], paths.Root);
    }
}
