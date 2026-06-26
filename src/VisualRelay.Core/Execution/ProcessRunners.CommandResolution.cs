using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// PATH-aware command whitelist resolution: intersects a --commands option with
// PATH, dropping missing tools gracefully. Kept separate from Helpers so each
// partial stays under the file-size guard.
public sealed partial class SwivalSubagentRunner
{
    // Intersect a --commands whitelist with PATH so missing optional tools degrade
    // gracefully instead of crashing swival's startup preflight. "all"/"none" pass through.
    internal static string ResolveCommandsOnPath(
        string commands,
        IRelayEventSink? eventSink,
        StageInvocation invocation)
    {
        if (commands is "all" or "none")
            return commands;

        var names = commands.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0)
            return string.Empty;

        // PATHEXT-aware on Windows so a bare `ls`/`git` resolves the `ls.exe`/`git.exe`
        // bundled with Git for Windows; plain PATH match on Unix.
        var path = Environment.GetEnvironmentVariable("PATH");
        var pathext = Environment.GetEnvironmentVariable("PATHEXT");
        var isWindows = OperatingSystem.IsWindows();

        var resolved = new List<string>(names.Length);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (PathExecutables.Resolve(name, path, pathext, isWindows, File.Exists) is not null)
            {
                resolved.Add(name);
            }
            else
            {
                eventSink?.PublishAsync(new RelayEvent(
                    DateTimeOffset.UtcNow,
                    "warn",
                    "command_dropped",
                    invocation.RunId,
                    invocation.TargetRoot,
                    invocation.TaskName,
                    invocation.Stage.Number,
                    invocation.Tier,
                    Data: new Dictionary<string, string>
                    {
                        ["name"] = name,
                        ["reason"] = "not found on PATH"
                    }));
            }
        }

        return string.Join(',', resolved);
    }
}
