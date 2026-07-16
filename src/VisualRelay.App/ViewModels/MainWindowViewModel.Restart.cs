using VisualRelay.Core.Logging;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>
    /// Spawns a detached relauncher process that waits for this process to
    /// exit (preventing bind-conflict on the control port), then restarts
    /// the app the same way it was started. After spawning, shuts down the
    /// current instance so the relauncher can take over.
    /// </summary>
    internal static async Task TriggerRestartAndShutdownAsync(RestartHandoff handoff)
    {
        var pid = Environment.ProcessId;
        var relaunchArgs = BuildRelaunchArgs(pid, handoff.RootPath);
        if (relaunchArgs is null)
        {
            DrainSummaryLog.Write(handoff.RootPath, handoff.DrainId, "relaunch",
                "restart-handoff", "relauncher-unavailable");
            return;
        }

        // Rewrite the handoff with the RelaunchCommand populated so the
        // detached relauncher can discover the command directly — avoids
        // relying on environment-variable fallbacks alone.
        _ = RestartHandoff.Write(
            handoff.RootPath,
            new RelayTaskOutcome(handoff.CommitSha, RelayTaskOutcomeStatus.Committed,
                handoff.CommitSha, handoff.CommitSha, null),
            handoff.DrainId,
            handoff.PendingCount,
            [relaunchArgs.Value.Process, relaunchArgs.Value.Arguments]);

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = relaunchArgs.Value.Process,
                Arguments = relaunchArgs.Value.Arguments,
                WorkingDirectory = handoff.RootPath,
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            DrainSummaryLog.Write(handoff.RootPath, handoff.DrainId, "relaunch",
                "restart-handoff", $"relauncher-failed: {ex.Message}");
            return;
        }

        await Task.Delay(200);
        Environment.Exit(0);
    }

    private static (string Process, string Arguments)? BuildRelaunchArgs(
        int parentPid, string rootPath)
    {
        var scriptDir = Environment.GetEnvironmentVariable("VISUAL_RELAY_SCRIPT_DIR");
        if (!string.IsNullOrWhiteSpace(scriptDir))
        {
            var proj = Path.Combine(scriptDir, "tools", "VisualRelay.Relauncher");
            return ("dotnet",
                $"run --project \"{proj}\" -- --parent-pid {parentPid} --root-path \"{rootPath}\"");
        }

        var appPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(appPath))
        {
            var appDir = Path.GetDirectoryName(appPath)!;
            var exe = Path.Combine(appDir, "VisualRelay.Relauncher");
            if (OperatingSystem.IsWindows()) exe += ".exe";
            if (File.Exists(exe))
                return (exe, $"--parent-pid {parentPid} --root-path \"{rootPath}\"");
        }

        return null;
    }
}
