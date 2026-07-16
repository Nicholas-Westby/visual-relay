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

        var appRestartCmd = BuildAppRestartArgs();

        // Rewrite the handoff with both the relauncher command (for records)
        // and the true app-restart command so the relauncher knows what to
        // spawn — never conflate the two.
        _ = RestartHandoff.Write(
            handoff.RootPath,
            new RelayTaskOutcome(handoff.CommitSha, RelayTaskOutcomeStatus.Committed,
                handoff.CommitSha, handoff.CommitSha, null),
            handoff.DrainId,
            handoff.PendingCount,
            relaunchCommand: [relaunchArgs.Value.Process, .. relaunchArgs.Value.Arguments],
            appRestartCommand: appRestartCmd);

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = relaunchArgs.Value.Process,
                WorkingDirectory = handoff.RootPath,
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            foreach (var a in relaunchArgs.Value.Arguments)
                startInfo.ArgumentList.Add(a);
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

    /// <summary>
    /// Builds the argv to restart THE APP the same way it was started.
    /// Source checkout → <c>dotnet run --project …/VisualRelay.App</c>;
    /// published install → the published binary path.
    /// </summary>
    private static string[]? BuildAppRestartArgs()
    {
        var scriptDir = Environment.GetEnvironmentVariable("VISUAL_RELAY_SCRIPT_DIR");
        if (!string.IsNullOrWhiteSpace(scriptDir))
        {
            var appProj = Path.Combine(scriptDir, "src", "VisualRelay.App",
                "VisualRelay.App.csproj");
            return ["dotnet", "run", "--project", appProj, "--"];
        }

        var appPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(appPath))
            return [appPath];

        return null;
    }

    /// <summary>
    /// Builds the argv to spawn <c>VisualRelay.Relauncher</c>. Returns a
    /// proper <c>string[]</c> for Arguments — never a pre-joined shell
    /// command line — so the caller can feed each element individually into
    /// <c>ProcessStartInfo.ArgumentList</c>.
    /// </summary>
    private static (string Process, string[] Arguments)? BuildRelaunchArgs(
        int parentPid, string rootPath)
    {
        var scriptDir = Environment.GetEnvironmentVariable("VISUAL_RELAY_SCRIPT_DIR");
        if (!string.IsNullOrWhiteSpace(scriptDir))
        {
            var proj = Path.Combine(scriptDir, "tools", "VisualRelay.Relauncher");
            return ("dotnet",
                ["run", "--project", proj, "--",
                 "--parent-pid", parentPid.ToString(),
                 "--root-path", rootPath]);
        }

        var appPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(appPath))
        {
            var appDir = Path.GetDirectoryName(appPath)!;
            var exe = Path.Combine(appDir, "VisualRelay.Relauncher");
            if (OperatingSystem.IsWindows()) exe += ".exe";
            if (File.Exists(exe))
                return (exe,
                    ["--parent-pid", parentPid.ToString(),
                     "--root-path", rootPath]);
        }

        return null;
    }
}
