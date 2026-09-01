namespace VisualRelay.Cli.Commands;

/// <summary>
/// <c>launch</c>/<c>run</c> (post-bootstrap half): runs the one prerequisite gate
/// left — nono, the OS sandbox — then starts the app. The published-app fast path
/// is owned by the bash bootstrap (brew users never reach here); this is the
/// source-checkout path, which runs the app via <c>dotnet run</c>.
/// </summary>
public static class LaunchCommand
{
    public static int Run(RepoPaths paths, IReadOnlyList<string> args)
    {
        var nono = Gates.NonoGate.Require(paths.Root);
        if (nono != 0)
            return nono;

        var runArgs = new List<string> { "run", "--project", paths.AppProject, "--" };
        runArgs.AddRange(args);
        return ProcessLauncher.Run(ProcessLauncher.Dotnet, runArgs, paths.Root);
    }
}
