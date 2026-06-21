using VisualRelay.Core.Configuration;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Tunable knobs for the backend lifecycle, each overridable by the same
/// environment variable the retired <c>backend.sh</c> honored so CI and
/// operators keep their existing levers.
/// </summary>
public sealed record BackendStartOptions
{
    /// <summary>Seconds to wait for readiness after a (re)start. Env: <c>VISUAL_RELAY_BACKEND_TIMEOUT</c> (30).</summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Grace before SIGKILL on stop. Env: <c>VISUAL_RELAY_BACKEND_STOP_GRACE</c> (10).</summary>
    public TimeSpan StopGrace { get; private init; } = TimeSpan.FromSeconds(10);

    /// <summary>Bound on config generation before falling back to the static template. Env: <c>VISUAL_RELAY_GEN_CONFIG_TIMEOUT</c> (15).</summary>
    public TimeSpan GenConfigTimeout { get; private init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Repo root used to locate the static litellm template and to run config
    /// generation in-process. <c>null</c> in tests that never reach generation.
    /// </summary>
    public string? RepoRoot { get; init; }

    /// <summary>
    /// Builds options from the process environment, applying the same defaults
    /// and override variables as <c>backend.sh</c>. <c>RepoRoot</c> resolves from
    /// <c>VISUAL_RELAY_SCRIPT_DIR</c> (set by the launcher) when present.
    /// </summary>
    public static BackendStartOptions FromEnvironment(IEnvironmentAccessor? accessor = null)
    {
        return new BackendStartOptions
        {
            ReadyTimeout = Seconds("VISUAL_RELAY_BACKEND_TIMEOUT", 30, accessor),
            StopGrace = Seconds("VISUAL_RELAY_BACKEND_STOP_GRACE", 10, accessor),
            GenConfigTimeout = Seconds("VISUAL_RELAY_GEN_CONFIG_TIMEOUT", 15, accessor),
            RepoRoot = KeyEnvFile.GetEnv("VISUAL_RELAY_SCRIPT_DIR", accessor),
        };
    }

    private static TimeSpan Seconds(string name, int fallback, IEnvironmentAccessor? accessor)
    {
        var raw = KeyEnvFile.GetEnv(name, accessor);
        var value = int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;
        return TimeSpan.FromSeconds(value);
    }
}
