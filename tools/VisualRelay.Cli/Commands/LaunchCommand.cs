namespace VisualRelay.Cli.Commands;

/// <summary>
/// <c>launch</c>/<c>run</c> (post-bootstrap half): runs the prerequisite gates in
/// the launcher's order — nono (when the sandbox is enabled) + provisioning,
/// then swival (hard-required), then the weekly upgrade check — starts the local
/// model backend best-effort, then starts the app. The published-app fast path
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
        Gates.NonoGate.Provision(paths.Root);

        var swival = Gates.SwivalGate.Require(paths.Root);
        if (swival != 0)
            return swival;

        Gates.SwivalUpgradeCheck.Run(paths.Root);

        // Best-effort backend start: a fresh launch can run a task without a
        // manual proxy step. If it fails the launch still proceeds and the
        // in-app pre-flight probe surfaces the down backend.
        if (ProcessLauncher.Run("bash", [paths.BackendScript, "start"], paths.Root) != 0)
            Console.Error.WriteLine(
                "visual-relay: backend start failed; launching anyway (in-app probe will flag a down backend)");

        var runArgs = new List<string> { "run", "--project", paths.AppProject, "--" };
        runArgs.AddRange(args);
        return ProcessLauncher.Run(ProcessLauncher.Dotnet, runArgs, paths.Root);
    }
}
