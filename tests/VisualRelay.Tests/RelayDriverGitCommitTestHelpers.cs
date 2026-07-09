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
        // Hermetic + host-independent: no host git config, no credential prompt.
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        startInfo.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout;
    }
}
