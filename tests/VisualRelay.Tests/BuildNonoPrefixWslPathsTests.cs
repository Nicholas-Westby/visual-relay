using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The nono prefix names paths as nono will see them. On the WSL host nono runs
/// inside the distro, so <c>--profile</c> is the profile's Linux placement and a
/// Windows directory VR grants is its DrvFs mount; on the local host every path is
/// its own. The mapping lives in <see cref="SandboxHost"/>, threaded through
/// <see cref="SandboxedStage.BuildNonoPrefix"/>.
/// </summary>
public sealed class BuildNonoPrefixWslPathsTests
{
    // A distro no machine has. Building the launch reads <root>\.git, and a share path on
    // a distro that exists boots it when it is stopped: measured on Windows, 2.7 s idle and
    // 16 to 45 s per test under the suite's load. A missing distro answers in about 25 ms
    // and boots nothing.
    private static readonly WslContext Context =
        new(@"C:\Windows\System32\wsl.exe", "VrNoSuchDistro", "/usr/local/bin/nono", "/home/alice");

    private static readonly SandboxHost Wsl = SandboxHost.Windows(Context);

    [Fact]
    public void ComposeNonoPrefix_OnTheWslHost_NamesTheLinuxProfileAndTheDrvFsTemplatesDir()
    {
        var prefix = SandboxedStage.ComposeNonoPrefix(
            TestConfig(), rollback: false, skipDirs: null, verboseDiagnostics: false,
            templatesDir: @"C:\Users\alice\AppData\Roaming\visual-relay\templates",
            workspaceRoot: @"\\wsl.localhost\VrNoSuchDistro\home\alice\repo", requestDiagnostics: false, host: Wsl);

        Assert.Equal(
            new[]
            {
                "run", "--profile", "/home/alice/.config/visual-relay/vr-guard.json", "--allow-cwd",
                "-a", "/mnt/c/Users/alice/AppData/Roaming/visual-relay/templates", "--silent", "--",
            },
            prefix);
    }

    [Fact]
    public void BuildNonoPrefix_OnTheWslHost_TranslatesEveryGrantAndStillCreatesTheTemplatesDir()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        var templatesDir = Path.Combine(tempDir, "templates");
        try
        {
            var config = TestConfig() with { SandboxExtraAllowPaths = [@"C:\Users\alice\.cache\exotic-tool"] };

            var prefix = SandboxedStage.BuildNonoPrefix(
                config, rollback: false, userTemplatesDirOverride: templatesDir, host: Wsl);

            Assert.Equal("/home/alice/.config/visual-relay/vr-guard.json", prefix[2]);
            Assert.Equal(new[] { "-a", "/mnt/c/Users/alice/.cache/exotic-tool" }, prefix.Skip(4).Take(2));
            // The temp templates dir as the distro sees it: a Linux path as itself, a Windows one as its DrvFs mount.
            Assert.Equal(new[] { "-a", Wsl.MapGrant(templatesDir)! }, prefix.Skip(6).Take(2));
            Assert.True(Directory.Exists(templatesDir));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Theory]
    [InlineData(@"C:\Users\alice\.cache", "/mnt/c/Users/alice/.cache")]
    [InlineData(@"d:\x y\ü", "/mnt/d/x y/ü")]
    [InlineData(@"\\wsl.localhost\VrNoSuchDistro\home\alice\.npm", "/home/alice/.npm")]
    [InlineData(@"\\wsl$\vrnosuchdistro\home\alice\.npm", "/home/alice/.npm")]
    [InlineData("/home/alice/.cargo", "/home/alice/.cargo")]
    public void MapGrant_OnTheWslHost_IsThePathAsNonoSeesItInsideTheDistro(string path, string expected)
    {
        Assert.Equal(expected, Wsl.MapGrant(path));
    }

    [Theory]
    [InlineData(@"\\wsl.localhost\Debian\home\alice\.npm")]
    [InlineData(@"relative\dir")]
    [InlineData("")]
    public void MapGrant_OnTheWslHost_HasNoAnswerForAPathTheDistroCannotReach(string path)
    {
        Assert.Null(Wsl.MapGrant(path));
    }

    [Fact]
    public void LocalHost_KeepsEveryPathAndTheXdgProfile()
    {
        Assert.Equal(@"C:\anything", SandboxHost.Local.MapGrant(@"C:\anything"));
        Assert.Equal("/Volumes/Tera/.TemporaryItems", SandboxHost.Local.MapGrant("/Volumes/Tera/.TemporaryItems"));
        Assert.Equal(Path.Combine(XdgConfig.ResolveConfigDir(), "visual-relay", "vr-guard.json"), SandboxHost.Local.ProfilePath);
        Assert.False(SandboxHost.Local.IsWindows);
    }

    [Fact]
    public void WslHost_ProfilePath_IsTheProfilesLinuxPlacement()
    {
        Assert.Equal("/home/alice/.config/visual-relay/vr-guard.json", Wsl.ProfilePath);
        Assert.True(Wsl.IsWindows);
    }

    [Fact]
    public void Current_OffWindows_IsTheLocalHost()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "On Windows Current probes the real machine once");

        Assert.Same(SandboxHost.Local, SandboxHost.Current);
    }

    private static RelayConfig TestConfig() =>
        new("llm-tasks", "true", "true", [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            true, 1, 1, false, true,
            SubagentTimeoutMilliseconds: 5_000,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int> { ["cheap"] = 90_000 },
            FirstOutputTimeoutMs: 660_000);
}
