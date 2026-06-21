using VisualRelay.Core.Configuration;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Resolves the per-machine filesystem locations for the local model backend
/// (the LiteLLM proxy), always under the user's XDG data directory — never the
/// repo tree — so host and VM each own their own venv and scratch. This is the
/// C# counterpart of the path block at the top of the retired
/// <c>tools/backend/backend.sh</c>; the directory layout is byte-for-byte
/// identical (<c>$XDG_DATA_HOME/visual-relay/{backend-venv,scratch}</c>) so a
/// venv provisioned by either surface is reused by the other.
/// </summary>
public sealed class BackendPaths
{
    // litellm's uvloop crashes on Python 3.14+, so the venv pins 3.13 — the same
    // version the bash script requested from uv.
    public const string PinnedPythonVersion = "3.13";

    private BackendPaths(string dataHome)
    {
        DataHome = dataHome;
        VenvDir = Path.Combine(dataHome, "backend-venv");
        Scratch = Path.Combine(dataHome, "scratch");
    }

    /// <summary>Root per-machine data directory: <c>$XDG_DATA_HOME/visual-relay</c>.</summary>
    public string DataHome { get; }

    /// <summary>The provisioned litellm virtualenv: <c>{DataHome}/backend-venv</c>.</summary>
    public string VenvDir { get; }

    /// <summary>Scratch directory for the pidfile, log, and generated config.</summary>
    public string Scratch { get; }

    public string VenvPython => Path.Combine(VenvDir, "bin", "python");
    public string VenvLitellm => Path.Combine(VenvDir, "bin", "litellm");
    public string PidFile => Path.Combine(Scratch, "litellm.pid");
    public string LogFile => Path.Combine(Scratch, "litellm.log");
    public string GeneratedConfig => Path.Combine(Scratch, "litellm-config.generated.yaml");

    /// <summary>
    /// Resolves the data home from <c>XDG_DATA_HOME</c> (falling back to
    /// <c>$HOME/.local/share</c>) read through <paramref name="accessor"/> or the
    /// real process environment. Mirrors the bash
    /// <c>${XDG_DATA_HOME:-$HOME/.local/share}/visual-relay</c> default exactly.
    /// </summary>
    public static BackendPaths Resolve(IEnvironmentAccessor? accessor = null)
    {
        var xdg = KeyEnvFile.GetEnv("XDG_DATA_HOME", accessor);
        var home = KeyEnvFile.GetEnv("HOME", accessor);
        return new BackendPaths(Combine(xdg, home));
    }

    private static string Combine(string? xdgDataHome, string? home)
    {
        string baseDir;
        if (!string.IsNullOrWhiteSpace(xdgDataHome))
            baseDir = xdgDataHome;
        else if (!string.IsNullOrWhiteSpace(home))
            baseDir = Path.Combine(home, ".local", "share");
        else
            throw new InvalidOperationException(
                "Cannot resolve data directory: neither XDG_DATA_HOME nor HOME is set.");

        return Path.Combine(baseDir, "visual-relay");
    }
}
