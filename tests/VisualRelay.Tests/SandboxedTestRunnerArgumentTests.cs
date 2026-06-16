using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="SandboxedTestRunner"/> argument shapes, the shared
/// nono-prefix builder, and new-guard path containment.
/// Every test in this file asserts on argument lists and pure-logic validation,
/// never shelling out to nono.
/// </summary>
public sealed class SandboxedTestRunnerArgumentTests
{
    // ════════════════════════════════════════════════════════════════════
    // SandboxedTestRunner argument shapes
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void ShellMode_SandboxEnabled_TransformsIntoNonoWrappedShell()
    {
        // "bun test" → nono run -p vr-guard --allow-cwd -- /bin/sh -lc "bun test"
        // --rollback / --no-rollback-prompt are ABSENT (verification path).
        var config = TestConfig() with { BypassSandbox = false };
        var sut = new SandboxedTestRunner(new ShellTestRunner(), config);

        var (fileName, args) = sut.ResolveLaunch("bun test");

        Assert.Equal("nono", fileName);
        Assert.Equal(new[] { "run", "-p", "vr-guard", "--allow-cwd", "--" }, args.Take(5));
        Assert.DoesNotContain("--rollback", args);
        Assert.DoesNotContain("--no-rollback-prompt", args);
        Assert.Equal("/bin/sh", args[5]);
        Assert.Equal("-lc \"bun test\"", args[6]);
    }

    [Fact]
    public void ShellMode_BypassEnabled_RunsInnerUnchanged()
    {
        var config = TestConfig() with { BypassSandbox = true };
        var sut = new SandboxedTestRunner(new ShellTestRunner(), config);

        var (fileName, args) = sut.ResolveLaunch("bun test");

        Assert.Equal("/bin/sh", fileName);
        Assert.Contains("bun test", args[0]);
        Assert.DoesNotContain("nono", args);
        Assert.DoesNotContain("run", args);
    }

    [Fact]
    public void DirectExecMode_SandboxEnabled_WrapsScriptDirectly()
    {
        // Script → nono run -p vr-guard --allow-cwd -- <script> <args>
        var config = TestConfig() with { BypassSandbox = false };
        var sut = new SandboxedTestRunner(new DirectExecTestRunner(), config);

        var (fileName, args) = sut.ResolveLaunch("/path/to/guard.sh arg1");

        Assert.Equal("nono", fileName);
        Assert.Equal(new[] { "run", "-p", "vr-guard", "--allow-cwd", "--" }, args.Take(5));
        Assert.DoesNotContain("--rollback", args);
        Assert.DoesNotContain("--no-rollback-prompt", args);
        Assert.Equal("/path/to/guard.sh", args[5]);
        Assert.Equal("arg1", args[6]);
    }

    [Fact]
    public void DirectExecMode_BypassEnabled_RunsInnerUnchanged()
    {
        var config = TestConfig() with { BypassSandbox = true };
        var sut = new SandboxedTestRunner(new DirectExecTestRunner(), config);

        var (fileName, args) = sut.ResolveLaunch("./tools/guards/check.sh");

        Assert.Equal("./tools/guards/check.sh", fileName);
        Assert.DoesNotContain("nono", args);
    }

    // ════════════════════════════════════════════════════════════════════
    // Shared nono-prefix builder (SwivalSubagentRunner.BuildNonoPrefix)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildNonoPrefix_WithRollback_EmitsRollbackFlags()
    {
        var config = TestConfig() with { BypassSandbox = false };
        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true);
        Assert.Equal(
            new[] { "run", "-p", "vr-guard", "--allow-cwd", "--rollback", "--no-rollback-prompt", "--" },
            prefix);
    }

    [Fact]
    public void BuildNonoPrefix_WithoutRollback_OmitsRollbackFlags()
    {
        var config = TestConfig() with { BypassSandbox = false };
        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false);
        Assert.Equal(new[] { "run", "-p", "vr-guard", "--allow-cwd", "--" }, prefix);
    }

    [Fact]
    public void BuildNonoPrefix_TwoCallersDifferOnlyInRollbackFlagPair()
    {
        var config = TestConfig() with { BypassSandbox = false };
        var swi = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true);
        var ver = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false);

        Assert.Equal(new[] { "run", "-p", "vr-guard", "--allow-cwd" }, swi.Take(4));
        Assert.Equal(new[] { "run", "-p", "vr-guard", "--allow-cwd" }, ver.Take(4));
        Assert.Equal("--rollback", swi[4]);
        Assert.Equal("--no-rollback-prompt", swi[5]);
        Assert.Equal("--", swi[6]);
        Assert.Equal("--", ver[4]);
        Assert.Equal(swi.Count - 2, ver.Count);
    }

    [Fact]
    public void BuildNonoPrefix_BypassEnabled_ReturnsEmptyPrefix()
    {
        var config = TestConfig() with { BypassSandbox = true };
        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false);
        Assert.Empty(prefix);
    }

    [Fact]
    public void BuildNonoPrefix_DoesNotBlockNetwork()
    {
        var config = TestConfig() with { BypassSandbox = false };
        Assert.DoesNotContain("--block-net",
            SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true));
        Assert.DoesNotContain("--block-net",
            SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false));
    }

    [Fact]
    public void BuildNonoPrefix_ExtraAllowPaths_AppendedAsAFlags()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var extra = Path.Combine(home, ".cache", "exotic-tool");
        var config = TestConfig() with
        {
            BypassSandbox = false,
            SandboxExtraAllowPaths = [extra]
        };

        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false);

        Assert.Equal("-a", prefix[4]);
        Assert.Equal(extra, prefix[5]);
        Assert.Equal("--", prefix[6]);
    }

    [Fact]
    public void SandboxedTestRunner_ResolveLaunch_AppendsExtraAllowPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var extra = Path.Combine(home, ".cache", "exotic-tool");
        var config = TestConfig() with
        {
            BypassSandbox = false,
            SandboxExtraAllowPaths = [extra]
        };
        var sut = new SandboxedTestRunner(new ShellTestRunner(), config);

        var (_, args) = sut.ResolveLaunch("dotnet test");

        Assert.Contains("-a", args);
        Assert.Contains(extra, args);
        Assert.DoesNotContain("--rollback", args);
    }

    // ════════════════════════════════════════════════════════════════════
    // New-guard path containment
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void NewGuardProbe_RejectsPathTraversalOutsideGuardsDir()
    {
        var rootPath = Path.GetTempPath();
        var guardsRoot = Path.GetFullPath(Path.Combine(rootPath, "tools", "guards"));
        var evilPath = Path.GetFullPath(Path.Combine(rootPath, "tools/guards/../../tmp/evil.sh"));

        Assert.False(evilPath.StartsWith(guardsRoot + Path.DirectorySeparatorChar)
                     && evilPath != guardsRoot);
        Assert.False(RelayDriver.IsPathWithinGuardRoot(evilPath, guardsRoot));
    }

    [Fact]
    public void NewGuardProbe_AcceptsLegitimateGuardScript()
    {
        var rootPath = Path.GetTempPath();
        var guardsRoot = Path.GetFullPath(Path.Combine(rootPath, "tools", "guards"));
        var legitPath = Path.GetFullPath(Path.Combine(rootPath, "tools", "guards", "check.sh"));

        Assert.True(legitPath.StartsWith(guardsRoot + Path.DirectorySeparatorChar)
                    || legitPath == guardsRoot);
        Assert.True(RelayDriver.IsPathWithinGuardRoot(legitPath, guardsRoot));
    }

    [Fact]
    public void NewGuardProbe_RejectsEntryOutsideGuardsDir()
    {
        var rootPath = Path.GetTempPath();
        var guardsRoot = Path.GetFullPath(Path.Combine(rootPath, "tools", "guards"));
        var otherPath = Path.GetFullPath(Path.Combine(rootPath, "scripts", "hack.sh"));

        Assert.StartsWith(rootPath, otherPath);
        Assert.False(RelayDriver.IsPathWithinGuardRoot(otherPath, guardsRoot));
    }

    // ── Helpers ────────────────────────────────────────────────────────

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
