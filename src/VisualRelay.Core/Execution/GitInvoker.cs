using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Centralized git process factory that pins a stable git binary at first use
/// and sanitizes the environment so nix-store churn on macOS cannot rot git
/// invocations mid-run. Each instance caches resolution independently;
/// production creates one instance and injects it everywhere git is called.
/// </summary>
public sealed class GitInvoker : IGitInvoker
{
    private readonly object _lock = new();
    private string? _gitBinary;
    private IReadOnlySet<string>? _envRemove;

    /// <summary>
    /// Creates an invoker that will auto-resolve the git binary on first call.
    /// </summary>
    public GitInvoker() { }

    /// <summary>
    /// Creates an invoker pre-pinned to <paramref name="binaryPath"/>
    /// (test constructor). The env-sanitize set is computed from the binary path.
    /// </summary>
    public GitInvoker(string binaryPath)
    {
        lock (_lock)
        {
            _gitBinary = binaryPath;
            _envRemove = binaryPath.Contains("/nix/store/", StringComparison.Ordinal)
                ? null
                : new HashSet<string>(StringComparer.Ordinal) { "DEVELOPER_DIR", "SDKROOT" };
        }
    }

    public async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        string rootPath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken killToken = default,
        Action<string>? onActivity = null)
    {
        var gitBinary = EnsureResolved();
        var sanitizedEnv = SanitizeEnvironment(environment);

        return await ProcessCapture.RunAsync(
            gitBinary,
            ["-C", rootPath, .. arguments],
            rootPath,
            timeout ?? TimeSpan.FromSeconds(30),
            cancellationToken,
            sanitizedEnv,
            killToken,
            onActivity,
            _envRemove);
    }

    /// <summary>
    /// Build a sanitized environment dictionary for the git process.
    /// When the pinned binary is outside /nix/store, DEVELOPER_DIR and
    /// SDKROOT are stripped so the xcrun shim cannot resurrect a stale
    /// nix store path.  Caller-supplied vars override inherited ones.
    /// </summary>
    private IReadOnlyDictionary<string, string> SanitizeEnvironment(
        IReadOnlyDictionary<string, string>? callerEnv)
    {
        var sanitized = new Dictionary<string, string>(StringComparer.Ordinal);

        // Inherit the current process environment.
        foreach (var key in Environment.GetEnvironmentVariables().Keys)
        {
            var keyStr = key.ToString()!;
            var val = Environment.GetEnvironmentVariable(keyStr);
            if (val is not null)
                sanitized[keyStr] = val;
        }

        // Overlay caller-supplied vars.
        if (callerEnv is not null)
        {
            foreach (var kvp in callerEnv)
            {
                sanitized[kvp.Key] = kvp.Value;
            }
        }

        // Strip sanitized keys last — they must never reach the git
        // process regardless of inheritance or caller intent.
        if (_envRemove is not null)
        {
            foreach (var key in _envRemove)
            {
                sanitized.Remove(key);
            }
        }

        return sanitized;
    }

    private string EnsureResolved()
    {
        var binary = _gitBinary;
        if (binary is not null)
            return binary;

        lock (_lock)
        {
            if (_gitBinary is not null)
                return _gitBinary;

            _gitBinary = ResolveGitBinary();

            // When the pinned binary lives outside /nix/store — i.e. the
            // system git at /usr/bin/git — strip DEVELOPER_DIR / SDKROOT
            // so the xcrun shim cannot resurrect a stale nix store path
            // that was inherited from the shell environment.
            if (!_gitBinary.Contains("/nix/store/", StringComparison.Ordinal))
            {
                _envRemove = new HashSet<string>(StringComparer.Ordinal) { "DEVELOPER_DIR", "SDKROOT" };
            }

            return _gitBinary;
        }
    }

    // ── Resolution order ──────────────────────────────────────────────

    private static string ResolveGitBinary()
    {
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
