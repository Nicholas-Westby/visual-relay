using System.Diagnostics;

namespace VisualRelay.Tests;

internal static class TestGit
{
    public static string Run(string rootPath, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // On macOS, git may resolve through xcrun, which picks up stale
        // DEVELOPER_DIR / SDKROOT from the nix-shell environment.  Strip
        // them so the xcrun shim falls back to the Xcode-selected default
        // (matching what GitInvoker.ResolveGitBinary does).
        process.StartInfo.Environment.Remove("DEVELOPER_DIR");
        process.StartInfo.Environment.Remove("SDKROOT");
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(rootPath);
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout;
    }
}
