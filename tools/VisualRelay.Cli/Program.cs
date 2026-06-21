using VisualRelay.Cli;
using VisualRelay.Cli.Commands;
using VisualRelay.Core.Execution;

// VisualRelay.Cli — the C# home for every `visual-relay` subcommand. The bash
// launcher is now only a pre-dotnet bootstrap (enter nix devshell, exec a
// published app for brew `launch`, else exec this CLI). All command LOGIC lives
// here; commands may shell out to dotnet/nono/swival/guards but hold no big
// bash. Arg dispatch mirrors tools/VisualRelay.RunTask/Program.cs: cmd → handler,
// usage to stderr, numeric exit codes.

var cmd = args.Length > 0 ? args[0] : "launch";
var rest = args.Length > 1 ? args[1..] : [];

if (!CommandRouter.IsKnown(cmd))
{
    Console.Error.WriteLine(CommandRouter.UsageLine);
    return 2;
}

var paths = RepoPaths.Resolve();

return cmd switch
{
    "launch" or "run" => LaunchCommand.Run(paths, rest),
    "build" => await BuildCommand.RunAsync(paths, rest),
    "test" => await TestCommand.RunAsync(paths, rest),
    "format" => FormatCommand.Run(paths, rest),
    "screenshot" => ScreenshotCommand.Run(paths),
    "run-task" => PassthroughCommand.RunTask(paths, rest),
    "init" => InitCommand.Run(paths, rest),
    "check" => await CheckCommand.RunAsync(paths),
    "inspect" => InspectCommand.Run(paths),
    "gen-backend-config" => PassthroughCommand.GenBackendConfig(paths, rest),
    "guards" => PassthroughCommand.Guards(paths, rest),
    "install-hooks" => await InstallHooksCommand.RunAsync(paths, new GitInvoker()),
    _ => Unknown(),
};

int Unknown()
{
    Console.Error.WriteLine(CommandRouter.UsageLine);
    return 2;
}
