using System.Diagnostics;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Provisions (and self-heals) the litellm virtualenv — the port of the bash
/// <c>ensure_litellm</c>. An execution probe runs the venv's <c>python -V</c> to
/// catch a dangling/foreign interpreter shebang from a host/VM mismatch; a failed
/// probe with an existing venv dir means it is broken, so it is removed and uv
/// rebuilds it from scratch (pinned Python <see cref="BackendPaths.PinnedPythonVersion"/>).
/// Falls back to a <c>litellm</c> already on PATH only when uv is unavailable.
/// </summary>
public static class BackendVenv
{
    /// <summary>The resolved litellm executable, or <c>null</c> when none could be made available.</summary>
    public readonly record struct Result(string? LitellmBin)
    {
        public bool Ok => LitellmBin is not null;
    }

    /// <summary>
    /// Ensures a usable litellm and returns its path. The process spawners are
    /// injectable so unit tests can stub the execution probe and uv without a
    /// real Python toolchain.
    /// </summary>
    public static Result Ensure(
        BackendPaths paths,
        Action<string> log,
        Func<string, string, bool>? probe = null,
        Func<string, IReadOnlyList<string>, bool>? run = null,
        Func<string, string?>? onPath = null)
    {
        probe ??= RunSucceeds;
        run ??= RunSucceeds;
        onPath ??= ResolveOnPath;

        // Probe: venv python must actually run AND litellm must be executable.
        if (probe(paths.VenvPython, "-V") && PathExecutables.IsExecutableFile(paths.VenvLitellm))
            return new Result(paths.VenvLitellm);

        // Probe failed but the dir exists => broken; remove so uv rebuilds.
        if (Directory.Exists(paths.VenvDir))
        {
            log($"removing broken venv at {paths.VenvDir}");
            TryDeleteDir(paths.VenvDir);
        }

        if (onPath("uv") is { } uv)
        {
            log($"provisioning litellm into {paths.VenvDir} (one-time; Python {BackendPaths.PinnedPythonVersion})");
            var made = run(uv, ["venv", paths.VenvDir, "--python", BackendPaths.PinnedPythonVersion])
                && run(uv, ["pip", "install", "--python", paths.VenvPython, "litellm[proxy]"])
                && PathExecutables.IsExecutableFile(paths.VenvLitellm);
            if (made)
                return new Result(paths.VenvLitellm);
            log("uv could not provision litellm (see output above)");
            return new Result(null);
        }

        // Fallback: a litellm already on PATH (may run on a uvloop-crashing Python).
        if (onPath("litellm") is { } litellm)
        {
            log($"using PATH litellm at {litellm} (install uv for a pinned Python {BackendPaths.PinnedPythonVersion} venv)");
            return new Result(litellm);
        }

        return new Result(null);
    }

    private static bool RunSucceeds(string file, string arg) => RunSucceeds(file, [arg]);

    private static bool RunSucceeds(string file, IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            // Missing binary, dangling shebang, permission — all "probe failed".
            return false;
        }
    }

    // Resolve uv/litellm on PATH through the shared PATHEXT-aware resolver so a
    // bare `uv`/`litellm` finds `uv.exe`/`litellm.exe` on Windows.
    private static string? ResolveOnPath(string name) => PathExecutables.Find(name);

    private static void TryDeleteDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort; a later uv venv may still succeed or surface its own error.
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort.
        }
    }
}
