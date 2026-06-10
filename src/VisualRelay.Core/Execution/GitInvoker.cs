using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Centralized git process factory that pins a stable git binary at first use
/// and sanitizes the environment so nix-store churn on macOS cannot rot git
/// invocations mid-run.
/// </summary>
internal static class GitInvoker
{
    private static readonly object Lock = new();
    private static string? _gitBinary;
    private static IReadOnlySet<string>? _envRemove;

    /// <summary>
    /// Test seam: when set, every <see cref="RunAsync"/> call delegates to
    /// this override instead of executing a real process.  Receives the
    /// resolved git binary path, arguments, root path, cancellation token,
    /// timeout, and sanitized environment.
    /// </summary>
    internal static Func<string, IEnumerable<string>, string, CancellationToken, TimeSpan?, IReadOnlyDictionary<string, string>?, Task<(int ExitCode, string Output, bool TimedOut)>>? Override { get; set; }

    /// <summary>
    /// The resolved git binary path.  Null until first resolution.
    /// </summary>
    internal static string? GitBinary
    {
        get
        {
            EnsureResolved();
            return _gitBinary;
        }
    }

    /// <summary>
    /// Test seam: resets all lazy state so tests can control resolution
    /// deterministically.  Must be called before <see cref="SetResolvedBinaryForTests"/>
    /// if the test wants a clean slate.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (Lock)
        {
            _gitBinary = null;
            _envRemove = null;
        }

        Override = null;
    }

    /// <summary>
    /// Test seam: pins the resolved binary path and computes
    /// <see cref="_envRemove"/> as though resolution had completed
    /// normally.  Call <see cref="ResetForTests"/> first to clear any
    /// cached state from a previous resolution.
    /// </summary>
    internal static void SetResolvedBinaryForTests(string binaryPath)
    {
        lock (Lock)
        {
            _gitBinary = binaryPath;
            _envRemove = binaryPath.Contains("/nix/store/", StringComparison.Ordinal)
                ? null
                : new HashSet<string>(StringComparer.Ordinal) { "DEVELOPER_DIR", "SDKROOT" };
        }
    }

    public static async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        string rootPath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken killToken = default,
        Action<string>? onActivity = null)
    {
        EnsureResolved();

        var sanitizedEnv = SanitizeEnvironment(environment);

        if (Override is not null)
        {
            return await Override(_gitBinary!, arguments, rootPath, cancellationToken, timeout, sanitizedEnv);
        }

        return await ProcessCapture.RunAsync(
            _gitBinary!,
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
    private static IReadOnlyDictionary<string, string> SanitizeEnvironment(
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

    private static void EnsureResolved()
    {
        if (_gitBinary is not null)
            return;

        lock (Lock)
        {
            if (_gitBinary is not null)
                return;

            _gitBinary = ResolveGitBinary();

            // When the pinned binary lives outside /nix/store — i.e. the
            // system git at /usr/bin/git — strip DEVELOPER_DIR / SDKROOT
            // so the xcrun shim cannot resurrect a stale nix store path
            // that was inherited from the shell environment.
            if (!_gitBinary.Contains("/nix/store/", StringComparison.Ordinal))
            {
                _envRemove = new HashSet<string>(StringComparer.Ordinal) { "DEVELOPER_DIR", "SDKROOT" };
            }
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
