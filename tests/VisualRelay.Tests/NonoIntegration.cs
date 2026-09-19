using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// Shared opt-in gate for sandbox-incompatible self-tests (the one well-known
/// <c>VR_RUN_NONO_INTEGRATION</c> marker). VR's per-task verify runs the whole
/// suite inside the strict nono (<c>vr-guard</c>) sandbox, where some VR-on-itself
/// tests cannot run: they spawn a real build child that can wedge, or shell out to
/// an external tool the sandbox denies a host write / keychain lookup (e.g. the
/// InspectCode gate-as-test). Such tests gate on <c>VR_RUN_NONO_INTEGRATION=1</c> —
/// skipped in the default sandboxed run, still runnable on demand. Centralising the
/// check keeps the idiom in one place and lets the build-subprocess and gate-as-test
/// guards key on the single marker.
///
/// <para><see cref="SkipIfNotOptedIn"/> is recognised by the guards' AST scan by its
/// method name, so EITHER call form works: bare via
/// <c>using static VisualRelay.Tests.NonoIntegration;</c>, or qualified
/// <c>NonoIntegration.SkipIfNotOptedIn()</c>.</para>
/// </summary>
internal static class NonoIntegration
{
    /// <summary>The opt-in environment variable; <c>=1</c> runs the sandbox-incompatible tests.</summary>
    private const string EnvVar = "VR_RUN_NONO_INTEGRATION";

    /// <summary>True when <see cref="EnvVar"/> is exactly <c>"1"</c>.</summary>
    private static bool OptedIn =>
        string.Equals(Environment.GetEnvironmentVariable(EnvVar), "1", StringComparison.Ordinal);

    /// <summary><c>Assert.Skip(reason)</c> unless <see cref="OptedIn"/>.</summary>
    public static void SkipIfNotOptedIn(
        string reason = "VR_RUN_NONO_INTEGRATION=1 required for sandbox-incompatible self-tests.")
    {
        if (!OptedIn)
            Assert.Skip(reason);
    }

    /// <summary>True when <paramref name="name"/> resolves on PATH.</summary>
    public static bool ToolAvailable(string name) => PathExecutables.OnPath(name);

    /// <summary>
    /// This machine's real WSL context, or null when it has no usable distro. The test
    /// process otherwise answers as a machine with no WSL, so the real-WSL tests ask for the
    /// live probe here: once per process, in a resolution of its own.
    /// </summary>
    public static WslContext? ThisMachinesWsl() => Machine.Value;

    private static readonly Lazy<WslContext?> Machine = new(() =>
    {
        using var real = WslContextResolver.IsolateForTests(prober: WslContextResolver.ProbeAsync);
        return WslContextResolver.TryGetCurrent();
    });
}
