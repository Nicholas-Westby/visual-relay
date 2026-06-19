using VisualRelay.Core.Init;

var rootPath = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
if (!Directory.Exists(rootPath))
{
    Console.Error.WriteLine($"visual-relay init: directory not found: {rootPath}");
    return 2;
}

// Bootstrap makes the folder runnable: detect (or placeholder) a test command, write
// .relay/config.json, initialize a git repo with a HEAD commit when missing, and install
// the pre-commit authority hook. An empty/greenfield folder becomes runnable immediately.
var result = await ProjectBootstrapper.BootstrapAsync(rootPath);

if (result.HookWarning is not null)
{
    Console.Error.WriteLine(result.HookWarning);
}

var gitNote = result.GitInitialized ? " initialized git repository;" : string.Empty;
var commandNote = result.UsedPlaceholderTestCommand
    ? "no toolchain detected yet — wrote a placeholder test command. Add a task that scaffolds " +
      "the project; Visual Relay adopts the real test command automatically once a toolchain appears."
    : $"testCmd validated: {result.TestCommand}.";

Console.WriteLine($"Wrote {result.ConfigPath};{gitNote} {commandNote}");

return 0;
