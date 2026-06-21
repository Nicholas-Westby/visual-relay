using System.Diagnostics;

namespace VisualRelay.Cli.Gates;

/// <summary>
/// Minimal helper to evaluate an overridable shell command string (the env
/// "seams" the launcher exposed: VISUAL_RELAY_SWIVAL_LATEST_CMD / _INSTALLER /
/// _UPGRADER). Each is a shell snippet, so it is run via <c>bash -c</c>.
/// </summary>
public static class Shell
{
    /// <summary>Runs <paramref name="command"/> via <c>bash -c</c> and returns its
    /// captured stdout. Never throws; a spawn failure maps to an empty string.
    /// (The upgrade check treats non-empty stdout as "upgrade available", so the
    /// exit code is intentionally not surfaced.)</summary>
    public static string Capture(string command, string workingDirectory)
    {
        var psi = new ProcessStartInfo("/bin/bash")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return stdout;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Runs <paramref name="command"/> via <c>bash -c</c> with inherited
    /// stdio (for installers/upgraders that may prompt). Returns the exit code.</summary>
    public static int RunInherited(string command, string workingDirectory)
    {
        var psi = new ProcessStartInfo("/bin/bash")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception)
        {
            return 127;
        }
    }
}
