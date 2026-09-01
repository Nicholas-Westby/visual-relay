namespace VisualRelay.Cli.Commands;

/// <summary>
/// Commands that just forward to an existing tool project via <c>dotnet run</c>:
/// <c>run-task</c>, <c>gen-sample</c> and <c>guards</c>. The tools hold
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
        return ForwardToTool(paths, "VisualRelay.RunTask", args);
    }

    /// <summary>
    /// Regenerates the sample repository at the given path. This is what the
    /// sample's own <c>scripts/reset-sample.sh</c> calls: the generator writes
    /// the repo from scratch, so generating and resetting are the same act.
    /// </summary>
    /// <param name="paths">Where the checkout lives.</param>
    /// <param name="args">The target directory, forwarded to the tool.</param>
    /// <returns>The tool's exit code.</returns>
    public static int GenSample(RepoPaths paths, IReadOnlyList<string> args) =>
        ForwardToTool(paths, "VisualRelay.SampleTasks", args);

    public static int Guards(RepoPaths paths, IReadOnlyList<string> args) =>
        ForwardToTool(paths, "VisualRelay.Guards", args);

    public static int Audit(RepoPaths paths, IReadOnlyList<string> args) =>
        ForwardToTool(paths, "VisualRelay.Audit", args);

    private static int ForwardToTool(RepoPaths paths, string tool, IReadOnlyList<string> args)
    {
        var runArgs = new List<string> { "run", "--project", paths.ToolProject(tool), "--" };
        runArgs.AddRange(args);
        return ProcessLauncher.Run(ProcessLauncher.Dotnet, runArgs, paths.Root);
    }
}
