namespace VisualRelay.Cli.Commands;

/// <summary><c>inspect</c>: runs the C# InspectCode zero-findings gate.</summary>
public static class InspectCommand
{
    public static int Run(RepoPaths paths) => Gates.InspectCodeGate.Run(paths);
}
