using VisualRelay.Core.Configuration;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: VisualRelay.GenBackendConfig <template-path>");
    return 2;
}

var templatePath = Path.GetFullPath(args[0]);
if (!File.Exists(templatePath))
{
    Console.Error.WriteLine($"gen-backend-config: template not found: {templatePath}");
    return 1;
}

// Resolve present keys: file values overlayed with process env (which wins).
var present = new HashSet<string>();
var fileKeys = KeyEnvFile.Read();
foreach (var (key, _) in fileKeys)
    present.Add(key);
foreach (var key in BackendConfigGenerator.ProviderKeyNames)
    if (Environment.GetEnvironmentVariable(key) is not null)
        present.Add(key);

try
{
    var (yaml, summary) = BackendConfigGenerator.Generate(present, templatePath);
    Console.Write(yaml);
    Console.Error.WriteLine(summary);
    return 0;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"gen-backend-config: {ex.Message}");
    return 1;
}
