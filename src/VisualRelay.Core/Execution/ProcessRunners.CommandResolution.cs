using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// PATH-aware command whitelist resolution: intersects a --commands option with
// PATH, dropping missing tools gracefully. Kept separate from Helpers so each
// partial stays under the file-size guard.
public sealed partial class SwivalSubagentRunner
{
    // Pre-flight before the RunAsync retry/escalation loop: backend readiness, then
    // required-launch-tool presence, then PATH-resolution of the stage's command
    // whitelist. Returns a failing SubagentResult on any gate (HardAbort left false —
    // these are pre-run conditions, not infra wedges), else the resolved commands.
    // Extracted so RunAsync.cs stays under the file-size guard.
    private async Task<(SubagentResult? Failure, string ResolvedCommands)> PreflightAsync(
        StageInvocation invocation, CancellationToken cancellationToken)
    {
        var readiness = await _probe(cancellationToken);
        if (!readiness.IsReady)
            return (new SubagentResult(string.Empty, null, false, readiness.Message), string.Empty);

        // Fail fast when a required launch tool isn't on PATH — avoids the doomed
        // launch and nono's advisory WARN dump, and names the real cause.
        var missingTools = MissingRequiredTools(_config, swivalBinary: _swivalBinary);
        if (missingTools.Count > 0)
            return (new SubagentResult(string.Empty, null, false,
                ErrorHintClassifier.WithHint(MissingToolsMessage(missingTools))), string.Empty);

        // Resolve the whitelist against PATH so missing optional tools degrade instead
        // of crashing swival's startup preflight (a command_dropped event per drop).
        var resolved = ResolveCommandsOnPath(invocation.Stage.Commands, _eventSink, invocation);
        if (string.IsNullOrWhiteSpace(resolved))
            return (new SubagentResult(string.Empty, null, false,
                $"All whitelisted commands are missing from PATH. " +
                $"Commands: [{invocation.Stage.Commands}]. " +
                $"After dropping unresolvable names, no commands remain — refusing to run " +
                $"because swival treats an empty whitelist as unrestricted."), string.Empty);

        return (null, resolved);
    }

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
