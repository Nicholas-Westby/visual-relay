namespace VisualRelay.Cli.Commands;

/// <summary><c>format</c>: <c>dotnet format</c> over the solution.</summary>
public static class FormatCommand
{
    public static int Run(RepoPaths paths, IReadOnlyList<string> args)
    {
        var formatArgs = new List<string> { "format", paths.Solution };
        formatArgs.AddRange(args);
        return ProcessLauncher.Run(ProcessLauncher.Dotnet, formatArgs, paths.Root);
    }
}
