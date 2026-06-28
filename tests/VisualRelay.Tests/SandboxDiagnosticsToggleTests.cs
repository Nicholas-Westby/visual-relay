using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Part A: the "verbose diagnostics" preference selects whether the nono sandbox
/// wrapper is invoked with <c>--silent</c>. DEFAULT = quiet (--silent present),
/// which suppresses nono's banner/summary/status/WARN-preflight and the failure
/// footer; toggling verbose ON omits --silent so nono's full diagnostics show.
/// The flag is OUTPUT-ONLY — it must never weaken the sandbox (profile, --allow-cwd,
/// network, child exit code unchanged). Applies to BOTH the verify path
/// (SandboxedTestRunner) and the swival-stage launch (BuildLaunchTarget).
/// </summary>
public sealed class SandboxDiagnosticsToggleTests
{
    // ── BuildNonoPrefix ─────────────────────────────────────────────────

    [Fact]
    public void BuildNonoPrefix_QuietByDefault_IncludesSilentBeforeSeparator()
    {
        var config = TestConfig();

        var verify = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false);
        var swival = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true);

        Assert.Contains("--silent", verify);
        Assert.Contains("--silent", swival);
        // --silent is a nono flag, so it must precede the `--` child separator.
        Assert.True(verify.ToList().IndexOf("--silent") < verify.ToList().IndexOf("--"));
        Assert.True(swival.ToList().IndexOf("--silent") < swival.ToList().IndexOf("--"));
    }

    [Fact]
    public void BuildNonoPrefix_Verbose_OmitsSilent()
    {
        var config = TestConfig();

        var verify = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false, verboseDiagnostics: true);
        var swival = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true, verboseDiagnostics: true);

        Assert.DoesNotContain("--silent", verify);
        Assert.DoesNotContain("--silent", swival);
    }

    [Fact]
    public void BuildNonoPrefix_Silent_IsOutputOnly_SandboxUnchanged()
    {
        // --silent must not touch enforcement: the profile load, --allow-cwd, and the
        // never-block-network invariant are identical with or without --silent.
        var config = TestConfig();

        var quiet = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false);
        var verbose = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false, verboseDiagnostics: true);

        foreach (var prefix in new[] { quiet, verbose })
        {
            Assert.Equal(new[] { "run", "--profile", ProfilePath, "--allow-cwd" }, prefix.Take(4));
            Assert.DoesNotContain("--block-net", prefix);
            Assert.DoesNotContain("--bypass-protection", prefix);
        }

        // The ONLY difference between quiet and verbose is the single --silent token.
        Assert.Equal(verbose, quiet.Where(a => a != "--silent"));
    }

    // ── SandboxedTestRunner (verify path) ───────────────────────────────

    [Fact]
    public void SandboxedTestRunner_QuietByDefault_IncludesSilent()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix nono wrapper (Windows uses the MXC seam)");
        var sut = new SandboxedTestRunner(new ShellTestRunner(), TestConfig());

        var (_, args) = sut.ResolveLaunch("bun test");

        Assert.Contains("--silent", args);
    }

    [Fact]
    public void SandboxedTestRunner_Verbose_OmitsSilent()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix nono wrapper (Windows uses the MXC seam)");
        var sut = new SandboxedTestRunner(new ShellTestRunner(), TestConfig(), verboseDiagnostics: true);

        var (_, args) = sut.ResolveLaunch("bun test");

        Assert.DoesNotContain("--silent", args);
    }

    // ── SwivalSubagentRunner.BuildLaunchTarget (swival stage path) ──────

    [Fact]
    public void SwivalLaunch_QuietByDefault_IncludesSilent()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix nono wrapper (Windows uses the MXC seam)");
        var runner = new SwivalSubagentRunner(TestConfig(), backendProbe: SwivalTestHelpers.AlwaysReady);
        var swivalArgs = runner.BuildArguments(SwivalTestHelpers.Invocation(Path.GetTempPath()));

        var (_, args) = runner.BuildLaunchTarget(swivalArgs);

        Assert.Contains("--silent", args);
    }

    [Fact]
    public void SwivalLaunch_Verbose_OmitsSilent()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix nono wrapper (Windows uses the MXC seam)");
        var runner = new SwivalSubagentRunner(
            TestConfig(), backendProbe: SwivalTestHelpers.AlwaysReady, verboseDiagnostics: true);
        var swivalArgs = runner.BuildArguments(SwivalTestHelpers.Invocation(Path.GetTempPath()));

        var (_, args) = runner.BuildLaunchTarget(swivalArgs);

        Assert.DoesNotContain("--silent", args);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static string ProfilePath => NonoProfileEnsurer.ResolveProfilePath();

    private static RelayConfig TestConfig() =>
        new("llm-tasks", "true", "true", [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            1, 1, 1, false, true,
            SubagentTimeoutMilliseconds: 5_000,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int>
            { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            FirstOutputTimeoutMs: 660_000,
            MaxStallRetries: 2);
}
