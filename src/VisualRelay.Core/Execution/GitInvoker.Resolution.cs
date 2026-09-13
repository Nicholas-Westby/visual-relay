using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VisualRelay.Core.Execution;

/// <summary>
/// The native git resolution behind <see cref="GitInvoker"/>: which binary the
/// process pins the first time a native root is served. Kept in a partial file
/// to respect the per-file line budget.
/// </summary>
public sealed partial class GitInvoker
{
    // ── Resolution order ──────────────────────────────────────────────

    private static string ResolveGitBinary()
    {
        // 0. On Windows resolve git.exe through the shared PATHEXT helper (Git for
        //    Windows on PATH). There is no xcrun and no POSIX shell to fall back to.
        if (OperatingSystem.IsWindows())
        {
            var windowsGit = PathExecutables.Find("git");
            if (windowsGit is not null && ProbeGit(windowsGit))
                return windowsGit;
            throw new InvalidOperationException(
                "git: not found on PATH. Install Git for Windows (winget install Git.Git) "
                + "and ensure it is on PATH.");
        }

        // 1. On macOS, prefer /usr/bin/git — it is immune to nix-shell rot
        //    and does not go through the xcrun shim.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var systemGit = "/usr/bin/git";
            if (File.Exists(systemGit) && ProbeGit(systemGit))
                return systemGit;
        }

        // 2. Try xcrun --find git (macOS Xcode toolchain); clear
        //    DEVELOPER_DIR so xcrun uses the Xcode-selected default
        //    instead of a potentially stale nix store path.
        var xcrunPath = ResolveViaXcrun();
        if (xcrunPath is not null)
            return xcrunPath;

        // 3. Try command -v git (shell PATH lookup).
        var pathGit = ResolveViaCommandV();
        if (pathGit is not null)
            return pathGit;

        // 4. Fallback: /usr/bin/git on non-macOS.
        var usrBinGit = "/usr/bin/git";
        if (File.Exists(usrBinGit) && ProbeGit(usrBinGit))
            return usrBinGit;

        throw new InvalidOperationException(
            "git: no working git binary found — tried xcrun --find git, " +
            "command -v git, and /usr/bin/git. Install git or ensure it is on PATH.");
    }

    private static string? ResolveViaXcrun()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo("xcrun")
            {
                Arguments = "--find git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            process.StartInfo.Environment.Remove("DEVELOPER_DIR");
            process.StartInfo.Environment.Remove("SDKROOT");

            process.Start();
            var stdout = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5_000);

            if (process.ExitCode == 0 && !string.IsNullOrEmpty(stdout))
            {
                try { stdout = Path.GetFullPath(stdout); } catch { /* not a valid path */ }
                if (File.Exists(stdout) && ProbeGit(stdout))
                    return stdout;
            }
        }
        catch
        {
            // xcrun not available — fall through.
        }

        return null;
    }

    private static string? ResolveViaCommandV()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo("/bin/sh")
            {
                Arguments = "-lc \"command -v git\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            process.Start();
            var stdout = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5_000);

            if (process.ExitCode == 0 && !string.IsNullOrEmpty(stdout))
            {
                try { stdout = Path.GetFullPath(stdout); } catch { /* not a valid path */ }
                if (File.Exists(stdout) && ProbeGit(stdout))
                    return stdout;
            }
        }
        catch
        {
            // Shell not available — fall through.
        }

        return null;
    }

    private static bool ProbeGit(string gitPath)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo(gitPath)
            {
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                // git.exe is a console program; from the desktop app it would flash a window.
                CreateNoWindow = true,
            };
            // When probing a non-nix binary, neutralise any inherited
            // DEVELOPER_DIR / SDKROOT so the probe matches run-time
            // conditions.
            if (!gitPath.Contains("/nix/store/", StringComparison.Ordinal))
            {
                process.StartInfo.Environment.Remove("DEVELOPER_DIR");
                process.StartInfo.Environment.Remove("SDKROOT");
            }

            process.Start();
            process.WaitForExit(5_000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
