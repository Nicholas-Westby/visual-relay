using System.Diagnostics;

namespace VisualRelay.Tests;

/// <summary>
/// Shared git helpers extracted from the former GitCommitterTests partial class
/// so companion files can be promoted to independent parallel test classes.
/// </summary>
internal static class GitCommitterTestHelpers
{
    // ReSharper disable once AsyncMethodWithoutAwait — async kept so awaiting sites surface sync git failures via the awaited task.
    public static async Task InitGitRepo(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        RunGit(root, "init");
        RunGit(root, "config user.email test@example.test");
        RunGit(root, "config user.name \"Test\"");
    }

    // ReSharper disable once AsyncMethodWithoutAwait — see InitGitRepo above.
    public static async Task StageAndCommitSeed(string root, string message)
    {
        RunGit(root, "add .");
        RunGit(root, $"commit -m \"{message}\"");
    }

    public static string RunGit(string rootPath, string arguments)
    {
        var startInfo = new ProcessStartInfo("/bin/sh", $"-c \"git -C '{rootPath}' {arguments}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // Strip DEVELOPER_DIR/SDKROOT so xcrun shim cannot resurrect a stale nix-store path inherited from the shell.
        startInfo.Environment.Remove("DEVELOPER_DIR");
        startInfo.Environment.Remove("SDKROOT");
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout;
    }

    /// <summary>Installs a commit-msg hook that rejects subjects matching <paramref name="rejectPattern"/>.</summary>
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

    /// <summary>Installs a commit-msg hook that rejects every commit.</summary>
    public static void InstallRejectAllCommitMsgHook(string repoRoot)
    {
        var hooksDir = Path.Combine(repoRoot, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        var hookPath = Path.Combine(hooksDir, "commit-msg");
        File.WriteAllText(hookPath,
            $"#!/usr/bin/env bash{Environment.NewLine}" +
            $"echo \"hook: all commits rejected\" >&2{Environment.NewLine}" +
            $"exit 1{Environment.NewLine}");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// Installs a pre-commit hook mimicking the original Relay's commit-authority
    /// guard: it rejects the commit unless the RELAY_NONCE env var matches the nonce
    /// in .relay/ACTIVE/info.json.
    /// </summary>
    public static void InstallRelayNonceGuardHook(string repoRoot)
    {
        var hooksDir = Path.Combine(repoRoot, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        var hookPath = Path.Combine(hooksDir, "pre-commit");
        File.WriteAllText(hookPath,
            """
            #!/usr/bin/env bash
            set -euo pipefail
            active=".relay/ACTIVE/info.json"
            [ -f "$active" ] || exit 0
            nonce="$(grep -o '"nonce"[[:space:]]*:[[:space:]]*"[^"]*"' "$active" | sed 's/.*"nonce"[[:space:]]*:[[:space:]]*"//; s/".*//' | head -1)"
            [ -z "$nonce" ] && exit 0
            if [ "${RELAY_NONCE:-}" = "$nonce" ]; then exit 0; fi
            echo "guard: RELAY_NONCE does not match active lock nonce" >&2
            exit 1
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public static void WriteActiveInfo(string repoRoot, string nonce)
    {
        var activeDir = Path.Combine(repoRoot, ".relay", "ACTIVE");
        Directory.CreateDirectory(activeDir);
        File.WriteAllText(Path.Combine(activeDir, "info.json"),
            $"{{\"nonce\":\"{nonce}\"}}");
    }
}
