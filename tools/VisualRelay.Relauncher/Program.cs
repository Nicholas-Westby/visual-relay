using VisualRelay.Relauncher;

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
    using var parent = System.Diagnostics.Process.GetProcessById(parentPid);
    parent.WaitForExit();
}
catch (ArgumentException)
{
    // Parent already exited — safe to proceed.
}

return await Relauncher.RunAsync(rootPath);
