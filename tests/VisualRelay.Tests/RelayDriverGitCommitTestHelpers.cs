using System.Diagnostics;

namespace VisualRelay.Tests;

/// <summary>
/// Shared helpers extracted from the former RelayDriverGitCommitTests partial class
/// so companion files can be promoted to independent parallel test classes.
/// </summary>
internal static class RelayDriverGitCommitTestHelpers
{
    public static string RunGit(string rootPath, string arguments)
    {
        var startInfo = new ProcessStartInfo("/bin/sh", $"-c \"git -C '{rootPath}' {arguments}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // Strip DEVELOPER_DIR/SDKROOT so xcrun cannot resurrect a stale nix-store path.
        startInfo.Environment.Remove("DEVELOPER_DIR");
        startInfo.Environment.Remove("SDKROOT");
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout;
    }

    public static void InstallRejectingCommitMsgHook(string repoRoot, string rejectPattern)
    {
        var hooksDir = Path.Combine(repoRoot, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        var hookPath = Path.Combine(hooksDir, "commit-msg");
        File.WriteAllText(hookPath,
            $"#!/usr/bin/env bash{Environment.NewLine}" +
            $"set -euo pipefail{Environment.NewLine}" +
            $"subject=\"$(head -n 1 \"$1\")\"{Environment.NewLine}" +
            $"if echo \"$subject\" | grep -qE '{rejectPattern}'; then{Environment.NewLine}" +
            $"  echo \"hook: subject matches rejected pattern\" >&2{Environment.NewLine}" +
            $"  exit 1{Environment.NewLine}" +
            $"fi{Environment.NewLine}" +
            $"exit 0{Environment.NewLine}");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
