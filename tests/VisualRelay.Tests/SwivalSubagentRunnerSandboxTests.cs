using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class SwivalSubagentRunnerSandboxTests
{
    [Fact]
    public void BuildArguments_NeverInjectsSandboxFlagsIntoSwival()
    {
        // swival 1.0.25+ has --sandbox/--nono-* flags, but VR doesn't use them:
        // it drives `nono run` itself (see BuildLaunchTarget). This test pins that
        // swival's own args stay sandbox-flag-free; the sandbox is the nono wrapper.
        var config = TestConfig();
        var runner = new SwivalSubagentRunner(config, backendProbe: SwivalTestHelpers.AlwaysReady);

        var args = runner.BuildArguments(SwivalTestHelpers.Invocation(Path.GetTempPath()));

        Assert.DoesNotContain("--sandbox", args);
        Assert.DoesNotContain("--nono-profile", args);
        Assert.DoesNotContain("--nono-rollback", args);
        Assert.DoesNotContain("nono", args);
    }

    [Fact]
    public void BuildLaunchTarget_SandboxEnabled_WrapsSwivalInNono()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix nono wrapper (Windows uses the MXC seam)");
        var config = TestConfig(); // sandbox is always on
        var runner = new SwivalSubagentRunner(config, backendProbe: SwivalTestHelpers.AlwaysReady);
        var swivalArgs = runner.BuildArguments(SwivalTestHelpers.Invocation(Path.GetTempPath()));

        var (fileName, args) = runner.BuildLaunchTarget(swivalArgs);

        // Launched process is nono, not swival.
        Assert.Equal("nono", fileName);

        // Exact nono prefix: run --profile <abs> --allow-cwd --rollback --no-rollback-prompt -- swival ...
        Assert.Equal(
            new[] { "run", "--profile", ProfilePath, "--allow-cwd", "--rollback", "--no-rollback-prompt", "--", "swival" },
            args.Take(8));

        // Everything after `-- swival` is the swival arg list, unchanged.
        var separatorIdx = ((IList<string>)args).IndexOf("--");
        Assert.Equal("swival", args[separatorIdx + 1]);
        Assert.Equal(swivalArgs, args.Skip(separatorIdx + 2));

        // nono never blocks the network (the relay must reach the model backend).
        Assert.DoesNotContain("--block-net", args);
    }

    [Fact]
    public void BuildSandboxEnvironment_SandboxEnabled_ReturnsCacheRedirects()
    {
        // The sandbox is always on, so swival runs under nono. Transitive deps
        // (huggingface_hub via litellm, uv) try
        // to write to ~/.cache/… which nono's vr-guard profile denies.
        // We redirect those cache writes into ~/.config/swival (already in
        // the swival profile write-allow list) via env vars.
        var config = TestConfig();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var env = SwivalSubagentRunner.BuildSandboxEnvironment(config);

        Assert.NotNull(env);
        // Assert the specific cache redirects rather than an exact count: the env
        // is the single seam EVERY nono-wrapped invocation shares (the swival
        // stage in ProcessRunners.RunAsync and the verify command in
        // SandboxedTestRunner), so it grows as more redirects are added; pinning
        // an exact count makes every such addition a spurious test break.
        Assert.True(env.Count >= 3);
        Assert.Equal(Path.Combine(home, ".config", "swival", "huggingface"), env["HF_HOME"]);
        Assert.Equal(Path.Combine(home, ".config", "swival", "cache"), env["XDG_CACHE_HOME"]);
        Assert.Equal(Path.Combine(home, ".config", "swival", "uv-cache"), env["UV_CACHE_DIR"]);
    }

    [Fact]
    public void BuildSandboxEnvironment_SandboxEnabled_StopsPythonWritingBytecodeIntoSystemStdlib()
    {
        // Regression: a Python invoked under nono (swival's own tooling, a
        // python-based verify command, or uv/litellm) imports stdlib modules and
        // CPython writes __pycache__/*.pyc back into the interpreter's stdlib dir
        // — e.g. the Homebrew python@3.14 Cellar — which the vr-guard profile
        // denies, triggering an interactive 50-path "Review denied paths" prompt
        // that blocks launch. Setting PYTHONDONTWRITEBYTECODE=1 makes CPython
        // never emit .pyc, and PYTHONPYCACHEPREFIX redirects any bytecode that a
        // tool re-enables into ~/.config/swival (already in the write-allow list).
        var config = TestConfig();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var env = SwivalSubagentRunner.BuildSandboxEnvironment(config);

        Assert.NotNull(env);
        Assert.Equal("1", env["PYTHONDONTWRITEBYTECODE"]);
        Assert.Equal(Path.Combine(home, ".config", "swival", "pycache"), env["PYTHONPYCACHEPREFIX"]);
    }

    // ════════════════════════════════════════════════════════════════════
    // Shared nono-prefix builder (BuildNonoPrefix)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildNonoPrefix_WithRollback_EmitsRollbackFlags()
    {
        // Swival path: rollback=true → run --profile <abs> --allow-cwd --rollback --no-rollback-prompt --
        var config = TestConfig();

        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true);

        Assert.Equal(
            new[] { "run", "--profile", ProfilePath, "--allow-cwd", "--rollback", "--no-rollback-prompt", "--" },
            prefix);
    }

    [Fact]
    public void BuildNonoPrefix_WithoutRollback_OmitsRollbackFlags()
    {
        // Verification path: rollback=false → run --profile <abs> --allow-cwd --
        var config = TestConfig();

        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false);

        Assert.Equal(
            new[] { "run", "--profile", ProfilePath, "--allow-cwd", "--" },
            prefix);
    }

    [Fact]
    public void BuildNonoPrefix_TwoCallerPreamblesDifferOnlyInRollbackFlags()
    {
        // Pin that the Swival and verification prefixes differ in exactly one
        // flag pair (--rollback --no-rollback-prompt) and nothing else.
        var config = TestConfig();

        var swivalPrefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true);
        var verifyPrefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false);

        // Both start with: run --profile <abs> --allow-cwd
        Assert.Equal(new[] { "run", "--profile", ProfilePath, "--allow-cwd" },
            swivalPrefix.Take(4));
        Assert.Equal(new[] { "run", "--profile", ProfilePath, "--allow-cwd" },
            verifyPrefix.Take(4));

        // Swival has --rollback --no-rollback-prompt at positions 4,5 then -- at 6.
        Assert.Equal("--rollback", swivalPrefix[4]);
        Assert.Equal("--no-rollback-prompt", swivalPrefix[5]);
        Assert.Equal("--", swivalPrefix[6]);

        // Verify jumps straight to -- at position 4.
        Assert.Equal("--", verifyPrefix[4]);

        // Length difference is exactly 2 (the rollback flags).
        Assert.Equal(swivalPrefix.Count - 2, verifyPrefix.Count);
    }

    [Fact]
    public void BuildNonoPrefix_WithExtraAllowPaths_AppendsAFlagsBeforeSeparator()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var extraPath = Path.Combine(home, ".cache", "exotic-tool");
        var config = TestConfig() with
        {
            SandboxExtraAllowPaths = [extraPath]
        };

        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false);

        // Prefix: run -p vr-guard --allow-cwd -a <path> --
        Assert.Equal("run", prefix[0]);
        Assert.Equal("-a", prefix[4]);
        Assert.Equal(extraPath, prefix[5]);
        Assert.Equal("--", prefix[6]);
    }

    [Fact]
    public void BuildNonoPrefix_WithExtraAllowPathsAndRollback_FlagsAppearBeforeRollback()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var extraPath = Path.Combine(home, ".cache", "exotic-tool");
        var config = TestConfig() with
        {
            SandboxExtraAllowPaths = [extraPath]
        };

        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true);

        // Prefix: run -p vr-guard --allow-cwd -a <path> --rollback --no-rollback-prompt --
        // The -a flags come BEFORE --rollback.
        Assert.Equal("run", prefix[0]);
        Assert.Equal("-a", prefix[4]);
        Assert.Equal(extraPath, prefix[5]);
        Assert.Equal("--rollback", prefix[6]);
        Assert.Equal("--no-rollback-prompt", prefix[7]);
        Assert.Equal("--", prefix[8]);
    }

    [Fact]
    public void BuildNonoPrefix_DoesNotBlockNetwork()
    {
        // The nono wrapper must never add --block-net — the relay must reach
        // the model backend (and package managers need network for restore).
        var config = TestConfig();

        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true);
        Assert.DoesNotContain("--block-net", prefix);

        prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false);
        Assert.DoesNotContain("--block-net", prefix);
    }

    // ════════════════════════════════════════════════════════════════════
    // BuildLaunchTarget still produces identical shape after refactor
    // (internally uses BuildNonoPrefix with rollback:true)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildLaunchTarget_UsesBuildNonoPrefixWithRollback()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix nono wrapper (Windows uses the MXC seam)");
        // After the refactor, BuildLaunchTarget must produce the same args it
        // always did — the shared builder just means the prefix isn't inlined.
        var config = TestConfig();
        var runner = new SwivalSubagentRunner(config, backendProbe: SwivalTestHelpers.AlwaysReady);
        var swivalArgs = runner.BuildArguments(SwivalTestHelpers.Invocation(Path.GetTempPath()));

        var (fileName, args) = runner.BuildLaunchTarget(swivalArgs);

        Assert.Equal("nono", fileName);
        Assert.Equal(
            new[] { "run", "--profile", ProfilePath, "--allow-cwd", "--rollback", "--no-rollback-prompt", "--", "swival" },
            args.Take(8));

        // Swival args follow after -- swival
        var separatorIdx = ((IList<string>)args).IndexOf("--");
        Assert.Equal("swival", args[separatorIdx + 1]);
        Assert.Equal(swivalArgs, args.Skip(separatorIdx + 2));
    }

    // The VR-owned profile abs path the prefix now carries (--profile <abs>),
    // resolved from the real process env exactly as production does.
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
