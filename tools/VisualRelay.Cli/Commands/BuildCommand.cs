namespace VisualRelay.Cli.Commands;

/// <summary>
/// <c>build</c>: runs the C# source-enumeration guard, then builds the solution
/// single-threaded with shared compilation off (the launcher's <c>build</c> case).
/// </summary>
public static class BuildCommand
{
    public static async Task<int> RunAsync(RepoPaths paths, IReadOnlyList<string> args)
    {
        var guard = await Gates.GuardRunner.SourceEnumerationAsync(paths);
        if (guard != 0)
            return guard;

        var buildArgs = new List<string>
        {
            "build", paths.Solution, "-m:1", "-p:UseSharedCompilation=false",
        };
        buildArgs.AddRange(args);
        return ProcessLauncher.Run(ProcessLauncher.Dotnet, buildArgs, paths.Root);
    }
}
