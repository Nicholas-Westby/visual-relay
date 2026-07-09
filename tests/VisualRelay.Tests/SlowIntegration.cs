namespace VisualRelay.Tests;

/// <summary>
/// Opt-in gate for the slow, host-dependent differential parity tests that spawn the
/// real git binary to compare it against GitSim. Mirrors
/// <see cref="NonoIntegration"/> but reads <c>VR_RUN_SLOW_INTEGRATION=1</c>, so these
/// facts are skipped in the default (fast, sandboxed) run and executed only on demand.
///
/// <para><see cref="SkipIfNotOptedIn"/> keeps that exact name because the
/// build-subprocess guard's AST scan recognizes the opt-out by method name — either
/// the bare call via <c>using static</c> or the qualified
/// <c>SlowIntegration.SkipIfNotOptedIn()</c> form.</para>
/// </summary>
internal static class SlowIntegration
{
    /// <summary>The opt-in environment variable; <c>=1</c> runs the parity tests.</summary>
    private const string EnvVar = "VR_RUN_SLOW_INTEGRATION";

    /// <summary>True when <see cref="EnvVar"/> is exactly <c>"1"</c>.</summary>
    private static bool OptedIn =>
        string.Equals(Environment.GetEnvironmentVariable(EnvVar), "1", StringComparison.Ordinal);

    /// <summary><c>Assert.Skip(reason)</c> unless <see cref="OptedIn"/>.</summary>
    public static void SkipIfNotOptedIn(
        string reason = "VR_RUN_SLOW_INTEGRATION=1 required for the real-git differential parity tests.")
    {
        if (!OptedIn)
            Assert.Skip(reason);
    }

    /// <summary>True when <paramref name="name"/> resolves on PATH.</summary>
    public static bool ToolAvailable(string name) => !string.IsNullOrEmpty(FindOnPath(name));

    private static string? FindOnPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        return pathEnv.Split(sep)
            .Select(dir => Path.Combine(dir.Trim(), name))
            .FirstOrDefault(File.Exists);
    }
}
