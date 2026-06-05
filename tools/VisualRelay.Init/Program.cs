using VisualRelay.Core.Init;

var rootPath = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
if (!Directory.Exists(rootPath))
{
    Console.Error.WriteLine($"visual-relay init: directory not found: {rootPath}");
    return 2;
}

var detected = TestCommandDetector.Detect(rootPath);
var path = RelayConfigWriter.Write(rootPath, detected);

if (string.IsNullOrEmpty(detected))
{
    Console.WriteLine($"Wrote {path} with an empty testCmd — project type was not recognized.");
    Console.WriteLine("Edit testCmd to your project's test command (e.g. \"dotnet test\", \"pytest\", \"npm test\"), then relaunch.");
}
else
{
    Console.WriteLine($"Wrote {path} with testCmd = \"{detected}\".");
}

return 0;
