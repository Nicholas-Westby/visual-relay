using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Proves the shared sandbox environment reaches a real spawned child through
/// ProcessCapture's env-application plumbing — the same plumbing every
/// nono-wrapped invocation uses to hand the env to nono, which (Seatbelt on
/// macOS) inherits it into the sandboxed child. vr-guard.json defines no env
/// allowlist or scrub, so the values survive.
/// <para>
/// This class used to anchor a Python bytecode-suppression fix: a Python run
/// under the sandbox wrote .pyc into its denied stdlib dir and raised a blocking
/// "Review denied paths" prompt. No Python runs inside this sandbox any more, so
/// that variable and its test went with the subprocess. What remains is the
/// forwarding itself, which every target's test command still depends on.
/// </para>
/// </summary>
public sealed class SandboxEnvForwardingTests
{
    [Fact]
    public void BuildSandboxEnvironment_CarriesDotnetLeakReductionVars()
    {
        // dotnet test leaves orphaned MSBuild node-reuse workers behind the
        // finished tests; those keep the nono wrapper alive past completion (the
        // stage-5/9 timeout). Disabling node reuse (and telemetry) lets the inner
        // command leave no orphans so nono can exit on its own — defence in depth
        // alongside SandboxedTestRunner's idle-reap.
        var env = SandboxedStage.BuildSandboxEnvironment(SandboxOn());

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

        var env = SandboxedStage.BuildSandboxEnvironment(SandboxOn());
        Assert.NotNull(env);

        var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
            "/bin/sh",
            new[] { "-c", "printf '%s' \"$MSBUILDDISABLENODEREUSE\"" },
            Path.GetTempPath(),
            TimeSpan.FromSeconds(10),
            CancellationToken.None,
            environment: env);

        Assert.False(timedOut);
        Assert.Equal(0, exitCode);
        Assert.Equal("1", output.Trim());
    }

    [Fact]
    public async Task ProcessCapture_EnvRemove_StripsMarkerFromChild()
    {
        // The envRemove mechanism must actually strip a key from the spawned
        // child's environment. Set a unique marker in the test process, spawn
        // a child with envRemove containing that marker, and assert the child
        // does NOT see it. Also pass a second key via environment: and assert
        // the child DOES see that one — proving the removal is selective.
        if (OperatingSystem.IsWindows())
            return; // /bin/sh-based assertion is POSIX-only.

        const string markerKey = "VR_INTEGRATION_MARKER_REMOVE";
        const string markerValue = "this-should-not-be-visible";
        const string passKey = "VR_INTEGRATION_MARKER_PASS";
        const string passValue = "this-should-be-visible";

        try
        {
            Environment.SetEnvironmentVariable(markerKey, markerValue);

            var envRemove = new HashSet<string> { markerKey };
            var environment = new Dictionary<string, string>
            {
                [passKey] = passValue,
            };

            var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
                "/bin/sh",
                new[] { "-c", $"if [ -z \"${markerKey}\" ]; then printf 'removed'; else printf 'leaked'; fi; printf '|'; printf '%s' \"$VR_INTEGRATION_MARKER_PASS\"" },
                Path.GetTempPath(),
                TimeSpan.FromSeconds(10),
                CancellationToken.None,
                environment: environment,
                envRemove: envRemove);

            Assert.False(timedOut);
            Assert.Equal(0, exitCode);
            Assert.Equal($"removed|{passValue}", output.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable(markerKey, null);
        }
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
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000);
}
