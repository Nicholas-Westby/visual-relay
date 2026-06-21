namespace VisualRelay.Cli.Commands;

/// <summary><c>inspect</c>: runs the InspectCode zero-findings gate script.</summary>
public static class InspectCommand
{
    public static int Run(RepoPaths paths) =>
        ProcessLauncher.Run("bash", [paths.Guard("inspect-code.sh")], paths.Root);
}
