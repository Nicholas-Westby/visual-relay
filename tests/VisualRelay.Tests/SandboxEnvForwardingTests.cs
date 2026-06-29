using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Regression anchor for the nono "Review denied paths" launch prompt: a Python
/// invoked under the nono (vr-guard) sandbox imports stdlib modules and CPython
/// writes __pycache__/*.pyc back into its (denied) stdlib dir — e.g. the Homebrew
/// python@3.14 Cellar — which raises an interactive ~50-path prompt that blocks
/// the run. The fix sets PYTHONDONTWRITEBYTECODE=1 in BuildSandboxEnvironment,
/// the single env seam EVERY nono-wrapped invocation shares (the swival stage in
/// ProcessRunners.RunAsync and the verify command in SandboxedTestRunner).
///
/// These tests prove the value (a) is present in that shared env and (b) actually
/// reaches a real spawned child via ProcessCapture's env-application plumbing —
/// the exact plumbing both nono-wrapped seams use to hand the env to nono, which
/// (Seatbelt on macOS) inherits env into its sandboxed child. vr-guard.json
/// defines no env allowlist/scrub, so the value survives into the swival process.
/// </summary>
public sealed class SandboxEnvForwardingTests
{
    [Fact]
    public void BuildSandboxEnvironment_SandboxEnabled_CarriesBytecodeSuppression()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var env = SwivalSubagentRunner.BuildSandboxEnvironment(SandboxOn());

        Assert.NotNull(env);
        Assert.Equal("1", env["PYTHONDONTWRITEBYTECODE"]);
        Assert.Equal(Path.Combine(home, ".config", "swival", "pycache"), env["PYTHONPYCACHEPREFIX"]);
    }

    [Fact]
    public void BuildSandboxEnvironment_CarriesDotnetLeakReductionVars()
    {
        // dotnet test leaves orphaned MSBuild node-reuse workers behind the
        // finished tests; those keep the nono wrapper alive past completion (the
        // stage-5/9 timeout). Disabling node reuse (and telemetry) lets the inner
        // command leave no orphans so nono can exit on its own — defence in depth
        // alongside SandboxedTestRunner's idle-reap.
        var env = SwivalSubagentRunner.BuildSandboxEnvironment(SandboxOn());

        Assert.Equal("1", env["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("1", env["DOTNET_CLI_TELEMETRY_OPTOUT"]);
    }

    [Fact]
    public async Task ProcessCapture_AppliesSandboxEnvironment_ReachesSpawnedChild()
    {
        // The shared env-application path: ProcessCapture sets every entry of the
        // BuildSandboxEnvironment dict on the spawned process (UseShellExecute=false),
        // which is exactly how both nono-wrapped seams hand env to nono. Spawn a
        // real child that echoes the var and assert it sees the suppression flag.
        if (OperatingSystem.IsWindows())
            return; // /bin/sh-based assertion is POSIX-only; the seam is macOS/Linux.

        var env = SwivalSubagentRunner.BuildSandboxEnvironment(SandboxOn());
        Assert.NotNull(env);

        var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
            "/bin/sh",
            new[] { "-c", "printf '%s' \"$PYTHONDONTWRITEBYTECODE\"" },
            Path.GetTempPath(),
            TimeSpan.FromSeconds(10),
            CancellationToken.None,
            environment: env);

        Assert.False(timedOut);
        Assert.Equal(0, exitCode);
        Assert.Equal("1", output.Trim());
    }

    private static RelayConfig SandboxOn() =>
        new(
            "llm-tasks",
            "true",
            "true",
            [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            true,
            1,
            1,
            false,
            true,
            0,
            300_000,
            new Dictionary<string, int> { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            660_000,
            2,
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000);
}
