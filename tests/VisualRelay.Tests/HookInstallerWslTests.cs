using VisualRelay.Core.Execution.Wsl;
using VisualRelay.Core.Init;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// A pre-commit hook written into a WSL distro through its share gets a default,
/// non-executable mode, so <see cref="HookInstaller"/> marks it executable from
/// INSIDE the distro with a plain wsl.exe exec; a native root takes the exec bit
/// directly as before. The wsl.exe launch goes through an injected runner here,
/// so nothing is spawned.
/// </summary>
public sealed class HookInstallerWslTests
{
    private const string UncHook = @"\\wsl.localhost\Ubuntu\home\alice\repo\.git\hooks\pre-commit";
    private const string LinuxHook = "/home/alice/repo/.git/hooks/pre-commit";

    [Fact]
    public void WslChmodLaunch_UncHookPath_IsChmodInsideTheDistroThePathNames()
    {
        var launch = HookInstaller.WslChmodLaunch(UncHook);

        Assert.NotNull(launch);
        string[] expected = ["-d", "Ubuntu", "--exec", "chmod", "+x", LinuxHook];
        Assert.Equal(expected, launch.Arguments);
        Assert.Equal(WslExeEnvironment.Variables, launch.Environment);
    }

    [Theory]
    [InlineData("/Users/alice/repo/.git/hooks/pre-commit")]
    [InlineData(@"C:\Dev\repo\.git\hooks\pre-commit")]
    public void WslChmodLaunch_NativeHookPath_IsNull(string hookPath) =>
        Assert.Null(HookInstaller.WslChmodLaunch(hookPath));

    [Fact]
    public async Task MarkExecutableAsync_UncHookPath_RunsTheChmodThroughTheRunner()
    {
        var launches = new List<WslLaunch>();

        var warning = await HookInstaller.MarkExecutableAsync(
            UncHook, (launch, _) => { launches.Add(launch); return Task.FromResult((0, "")); }, CancellationToken.None);

        Assert.Null(warning);
        var launch = Assert.Single(launches);
        Assert.Equal(["chmod", "+x", LinuxHook], launch.Arguments.Skip(3));
    }

    [Fact]
    public async Task MarkExecutableAsync_ChmodFailing_SaysWhyEnforcementIsInactive()
    {
        var warning = await HookInstaller.MarkExecutableAsync(
            UncHook, (_, _) => Task.FromResult((1, "chmod: cannot access")), CancellationToken.None);

        Assert.NotNull(warning);
        Assert.Contains("chmod", warning);
        Assert.Contains("cannot access", warning);
        Assert.Contains("Ubuntu", warning);
        Assert.Contains("not be active", warning);
    }

    [Fact]
    public async Task InstallAsync_NativeRoot_NeverAsksWsl()
    {
        using var repo = TestRepository.Create();
        var sim = new GitSimEngine();
        sim.InitRepo(repo.Root);
        var launches = 0;

        var result = await HookInstaller.InstallAsync(
            repo.Root, CancellationToken.None, sim, (_, _) => { launches++; return Task.FromResult((0, "")); });

        Assert.True(result.Installed);
        Assert.Equal(0, launches);
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(Path.Combine(repo.Root, ".git", "hooks", "pre-commit"));
            Assert.True((mode & UnixFileMode.UserExecute) != 0, "the native hook must still take the exec bit directly");
        }
    }
}
