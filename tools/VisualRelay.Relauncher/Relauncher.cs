using System.Diagnostics;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Queue;

namespace VisualRelay.Relauncher;

/// <summary>
/// Core relaunch logic — spawns the app, verifies arrival, and reports
/// outcomes to the drain log. Testable via <see cref="RunAsync"/> with
/// a virtual <see cref="TimeProvider"/>.
/// </summary>
internal static class Relauncher
{
    /// <summary>
    /// Reads the restart handoff, spawns the app using
    /// <see cref="RestartHandoff.AppRestartCommand"/> (falling back to
    /// <see cref="RestartHandoff.RelaunchCommand"/> then environment),
    /// and polls for arrival (handoff consumed) within a bounded window.
    /// Returns 0 on confirmed arrival, non-zero on any failure.
    /// </summary>
    public static async Task<int> RunAsync(
        string rootPath,
        TimeProvider? timeProvider = null)
    {
        timeProvider ??= TimeProvider.System;
        var handoff = RestartHandoff.Read(rootPath);

        // Prefer AppRestartCommand (the true app restart argv), then fall
        // back to environment vars. Never use RelaunchCommand — it records
        // how the relauncher itself was spawned and would cause self-respawn.
        string[]? cmd = handoff?.AppRestartCommand is { Length: > 0 } ac ? ac
            : null;

        if (cmd is null)
        {
            var scriptDir = Environment.GetEnvironmentVariable(
                "VISUAL_RELAY_SCRIPT_DIR");
            if (!string.IsNullOrWhiteSpace(scriptDir))
            {
                cmd = ["dotnet", "run", "--project",
                    Path.Combine(scriptDir, "src", "VisualRelay.App")];
            }
            else
            {
                Console.Error.WriteLine(
                    "No app restart command available in handoff or environment");
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

        if (cmd.Length > 1)
        {
            foreach (var a in cmd.Skip(1))
                startInfo.ArgumentList.Add(a);
        }

        Process? child;
        try
        {
            child = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            if (handoff is not null)
            {
                DrainSummaryLog.Write(rootPath, handoff.DrainId, "relaunch",
                    "restart-resume", "spawn-failed", ex.Message);
            }
            return 1;
        }

        if (child is null)
        {
            if (handoff is not null)
            {
                DrainSummaryLog.Write(rootPath, handoff.DrainId, "relaunch",
                    "restart-resume", "spawn-failed",
                    "Process.Start returned null");
            }
            return 1;
        }

        // Poll for arrival: the new instance consumes the handoff sidecar
        // (renames → .consumed or deletes it). When the file is gone the
        // app has arrived and acknowledged the handoff.
        var handoffPath = Path.Combine(rootPath, ".relay",
            "restart-handoff.json");
        var deadline = timeProvider.GetUtcNow() + TimeSpan.FromSeconds(30);
        var arrived = false;

        while (timeProvider.GetUtcNow() < deadline)
        {
            // Check arrival FIRST — a fast child may delete the handoff and
            // exit before the next poll, so we must see the handoff consumed
            // regardless of the child's exit state.
            if (!File.Exists(handoffPath))
            {
                arrived = true;
                break;
            }

            if (child.HasExited)
            {
                // Child exited without consuming the handoff — failure.
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), timeProvider);
        }

        if (arrived)
        {
            if (handoff is not null)
            {
                DrainSummaryLog.Write(rootPath, handoff.DrainId, "relaunch",
                    "restart-resume", $"pid={child.Id}");
            }
            return 0;
        }

        // Failure: child died or arrival timed out. Leave the sidecar
        // in place for diagnosis and write a loud drain-log event.
        if (handoff is not null)
        {
            var detail = child.HasExited
                ? $"child-exited-code={child.ExitCode}"
                : "arrival-timeout";
            DrainSummaryLog.Write(rootPath, handoff.DrainId, "relaunch",
                "restart-resume", "spawn-failed", detail);
        }

        return 1;
    }
}
