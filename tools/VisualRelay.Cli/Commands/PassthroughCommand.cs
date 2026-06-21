namespace VisualRelay.Cli.Commands;

/// <summary>
/// Commands that just forward to an existing tool project via <c>dotnet run</c>:
/// <c>run-task</c>, <c>gen-backend-config</c>, and <c>guards</c>. The tools hold
/// the real logic; the CLI only wires the project path and forwards args.
/// </summary>
public static class PassthroughCommand
{
    public static int RunTask(RepoPaths paths, IReadOnlyList<string> args)
    {
        // run-task additionally requires the sandbox + swival (it drives a real
        // pipeline stage), matching the launcher's run-task gates.
        var nono = Gates.NonoGate.Require(paths.Root);
        if (nono != 0)
            return nono;
        var swival = Gates.SwivalGate.Require(paths.Root);
        if (swival != 0)
            return swival;
        return ForwardToTool(paths, "VisualRelay.RunTask", args);
    }

    public static int GenBackendConfig(RepoPaths paths, IReadOnlyList<string> args) =>
        ForwardToTool(paths, "VisualRelay.GenBackendConfig", args);

    public static int Guards(RepoPaths paths, IReadOnlyList<string> args) =>
        ForwardToTool(paths, "VisualRelay.Guards", args);

    private static int ForwardToTool(RepoPaths paths, string tool, IReadOnlyList<string> args)
    {
        var runArgs = new List<string> { "run", "--project", paths.ToolProject(tool), "--" };
        runArgs.AddRange(args);
        return ProcessLauncher.Run(ProcessLauncher.Dotnet, runArgs, paths.Root);
    }
}
