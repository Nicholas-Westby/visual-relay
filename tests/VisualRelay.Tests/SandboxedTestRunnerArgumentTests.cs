using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class SandboxedTestRunnerArgumentTests
{
    // SandboxedTestRunner argument shapes

    [Fact]
    public void ShellMode_SandboxEnabled_TransformsIntoNonoWrappedShell()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix nono wrapper (Windows uses the MXC seam)");
        // "bun test" → nono run --profile <abs> --allow-cwd -- /bin/sh -c "bun test"
        // Non-login shell (-c, not -lc): the sandboxed verify inherits the harness's toolchain
        // rather than re-resolving a different dotnet/PATH from a login profile (which caused
        // runtime-mismatch launch failures under nono). --rollback/--no-rollback-prompt ABSENT.
        var config = TestConfig();
        var sut = new SandboxedTestRunner(new ShellTestRunner(), config);

        var (fileName, args) = sut.ResolveLaunch("bun test");

        Assert.Equal("nono", fileName);
        Assert.Equal(new[] { "run", "--profile", ProfilePath, "--allow-cwd", "--" }, args.Take(5));
        Assert.DoesNotContain("--rollback", args);
        Assert.DoesNotContain("--no-rollback-prompt", args);
        Assert.Equal("/bin/sh", args[5]);
        // -c and the command MUST be separate ArgumentList entries. SandboxedTestRunner
        // uses the IEnumerable<string> ProcessCapture overload, which adds each entry
        // verbatim (no quote-splitting); a merged `-c "bun test"` entry reaches /bin/sh
        // as one unparseable arg → exit 2 (every sandboxed verify falsely red).
        Assert.Equal("-c", args[6]);
        Assert.Equal("bun test", args[7]);
    }

    [Fact]
    public void DirectExecMode_SandboxEnabled_WrapsScriptDirectly()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix nono wrapper (Windows uses the MXC seam)");
        // Script → nono run --profile <abs> --allow-cwd -- <script> <args>
        var config = TestConfig();
        var sut = new SandboxedTestRunner(new DirectExecTestRunner(), config);

        var (fileName, args) = sut.ResolveLaunch("/path/to/guard.sh arg1");

        Assert.Equal("nono", fileName);
        Assert.Equal(new[] { "run", "--profile", ProfilePath, "--allow-cwd", "--" }, args.Take(5));
        Assert.DoesNotContain("--rollback", args);
        Assert.DoesNotContain("--no-rollback-prompt", args);
        Assert.Equal("/path/to/guard.sh", args[5]);
        Assert.Equal("arg1", args[6]);
    }

    // Shared nono-prefix builder (SwivalSubagentRunner.BuildNonoPrefix)

    [Fact]
    public void BuildNonoPrefix_WithRollback_EmitsRollbackFlags()
    {
        var config = TestConfig();
        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true);
        Assert.Equal(
            new[] { "run", "--profile", ProfilePath, "--allow-cwd", "--rollback", "--no-rollback-prompt", "--" },
            prefix);
    }

    [Fact]
    public void BuildNonoPrefix_WithoutRollback_OmitsRollbackFlags()
    {
        var config = TestConfig();
        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false);
        Assert.Equal(new[] { "run", "--profile", ProfilePath, "--allow-cwd", "--" }, prefix);
    }

    [Fact]
    public void BuildNonoPrefix_TwoCallersDifferOnlyInRollbackFlagPair()
    {
        var config = TestConfig();
        var swi = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true);
        var ver = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false);

        Assert.Equal(new[] { "run", "--profile", ProfilePath, "--allow-cwd" }, swi.Take(4));
        Assert.Equal(new[] { "run", "--profile", ProfilePath, "--allow-cwd" }, ver.Take(4));
        Assert.Equal("--rollback", swi[4]);
        Assert.Equal("--no-rollback-prompt", swi[5]);
        Assert.Equal("--", swi[6]);
        Assert.Equal("--", ver[4]);
        Assert.Equal(swi.Count - 2, ver.Count);
    }

    [Fact]
    public void BuildNonoPrefix_DoesNotBlockNetwork()
    {
        var config = TestConfig();
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
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix nono wrapper (Windows uses the MXC seam)");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var extra = Path.Combine(home, ".cache", "exotic-tool");
        var config = TestConfig() with
        {
            SandboxExtraAllowPaths = [extra]
        };
        var sut = new SandboxedTestRunner(new ShellTestRunner(), config);

        var (_, args) = sut.ResolveLaunch("dotnet test");

        Assert.Contains("-a", args);
        Assert.Contains(extra, args);
        Assert.DoesNotContain("--rollback", args);
    }

    // New-guard path containment

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

    // Symlink-resolution containment (IsSymlinkTargetContained)
    [Fact]
    public void NewGuardProbe_SymlinkOutsideGuardsDir_Rejected()
    {
        using var tmp = new TempGuardsDir();
        // Create a real guard script and a symlink that escapes.
        var realScript = Path.Combine(tmp.GuardsRoot, "real.sh");
        File.WriteAllText(realScript, "#!/bin/sh\nexit 0\n");
        var outsideTarget = Path.Combine(tmp.RootPath, "outside.sh");
        File.WriteAllText(outsideTarget, "#!/bin/sh\necho bad\n");
        var symlinkPath = Path.Combine(tmp.GuardsRoot, "escape.sh");
        if (!TryCreateSymbolicLink(symlinkPath, outsideTarget))
            Assert.Skip("Symlink creation not supported on this platform");

        // Lexical check passes (symlink is under guards dir).
        Assert.True(RelayDriver.IsPathWithinGuardRoot(symlinkPath, tmp.GuardsRoot));
        // Symlink-target check must reject — the real target is outside.
        Assert.False(RelayDriver.IsSymlinkTargetContained(symlinkPath, tmp.GuardsRoot));
        // Real file still passes containment.
        Assert.True(RelayDriver.IsSymlinkTargetContained(realScript, tmp.GuardsRoot));
    }

    [Fact]
    public void NewGuardProbe_SymlinkInsideGuardsDir_Accepted()
    {
        using var tmp = new TempGuardsDir();
        var realScript = Path.Combine(tmp.GuardsRoot, "real.sh");
        File.WriteAllText(realScript, "#!/bin/sh\nexit 0\n");
        var symlinkPath = Path.Combine(tmp.GuardsRoot, "link.sh");
        if (!TryCreateSymbolicLink(symlinkPath, realScript))
            Assert.Skip("Symlink creation not supported on this platform");

        Assert.True(RelayDriver.IsPathWithinGuardRoot(symlinkPath, tmp.GuardsRoot));
        Assert.True(RelayDriver.IsSymlinkTargetContained(symlinkPath, tmp.GuardsRoot));
    }

    [Fact]
    public void NewGuardProbe_NonSymlinkFile_Accepted()
    {
        using var tmp = new TempGuardsDir();
        var legitPath = Path.Combine(tmp.GuardsRoot, "check.sh");
        File.WriteAllText(legitPath, "#!/bin/sh\nexit 0\n");

        Assert.True(RelayDriver.IsPathWithinGuardRoot(legitPath, tmp.GuardsRoot));
        Assert.True(RelayDriver.IsSymlinkTargetContained(legitPath, tmp.GuardsRoot));
    }

    // ── Helpers ────────────────────────────────────────────────────────

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

    private static bool TryCreateSymbolicLink(string linkPath, string target)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, target);
            return File.Exists(linkPath) || Directory.Exists(linkPath);
        }
        catch { return false; }
    }

    private sealed class TempGuardsDir : IDisposable
    {
        public string RootPath { get; }
        public string GuardsRoot { get; }

        public TempGuardsDir()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "vr-guard-tests", Guid.NewGuid().ToString("N"));
            GuardsRoot = Path.Combine(RootPath, "tools", "guards");
            Directory.CreateDirectory(GuardsRoot);
        }

        public void Dispose()
        {
            TestFileSystem.DeleteDirectoryResilient(RootPath);
        }
    }
}
