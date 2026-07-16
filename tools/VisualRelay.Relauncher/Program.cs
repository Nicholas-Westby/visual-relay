using System.Diagnostics;
using VisualRelay.Core.Queue;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: VisualRelay.Relauncher --parent-pid <pid> --root-path <path>");
    return 1;
}

var parentPid = -1;
string? rootPath = null;

for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--parent-pid" && i + 1 < args.Length)
        parentPid = int.Parse(args[++i]);
    else if (args[i] == "--root-path" && i + 1 < args.Length)
        rootPath = args[++i];
}

if (parentPid < 0 || string.IsNullOrWhiteSpace(rootPath))
{
    Console.Error.WriteLine("Missing --parent-pid or --root-path");
    return 1;
}

// Wait for the parent (current app instance) to exit so the new instance
// won't hit a bind-conflict on the control port.
try
{
    using var parent = Process.GetProcessById(parentPid);
    parent.WaitForExit();
}
catch (ArgumentException)
{
    // Parent already exited — safe to proceed.
}

// Read the handoff to discover the relaunch command.
var handoff = RestartHandoff.Read(rootPath);
if (handoff?.RelaunchCommand is not { Length: > 0 } cmd)
{
    // Fallback: restart using the bootstrap script if available.
    var scriptDir = Environment.GetEnvironmentVariable("VISUAL_RELAY_SCRIPT_DIR");
    if (!string.IsNullOrWhiteSpace(scriptDir))
    {
        var appProj = Path.Combine(scriptDir, "src", "VisualRelay.App");
        cmd = ["dotnet", "run", "--project", appProj];
    }
    else
    {
        Console.Error.WriteLine("No relaunch command available in handoff or environment");
        return 1;
    }
}

var startInfo = new ProcessStartInfo
{
    FileName = cmd[0],
    WorkingDirectory = rootPath,
    CreateNoWindow = false,
    UseShellExecute = false,
};

// Build arguments from remaining elements.
if (cmd.Length > 1)
{
    foreach (var a in cmd.Skip(1))
        startInfo.ArgumentList.Add(a);
}

Process.Start(startInfo);
return 0;
