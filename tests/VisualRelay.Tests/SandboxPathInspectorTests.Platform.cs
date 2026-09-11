using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The inspector's platform seam. Which OS the profile's <c>when</c> and the
/// groups' <c>platform</c> tokens are filtered against is an explicit parameter,
/// not the OS the inspector runs on: on Windows nono runs inside the WSL distro,
/// so the enforced policy is the Linux one and <c>~</c> is the distro user's home.
/// The Windows arm asks nono inside the distro through a plain wsl.exe exec; its
/// launches are recorded here and never run. Partial of <see cref="SandboxPathInspectorTests"/>.
/// </summary>
public sealed partial class SandboxPathInspectorTests
{
    private static readonly WslContext Wsl =
        new(@"C:\Windows\System32\wsl.exe", "Ubuntu", "/usr/local/bin/nono", "/home/alice");

    private const string LinuxProfile = "/home/alice/.config/visual-relay/vr-guard.json";
    private const string UncWorkspace = @"\\wsl.localhost\Ubuntu\home\alice\repo";

    [Theory]
    [InlineData(SandboxPlatform.Linux, "$XDG_CACHE_HOME/NuGet", "~/Library/Caches/NuGet")]
    [InlineData(SandboxPlatform.MacOs, "~/Library/Caches/NuGet", "$XDG_CACHE_HOME/NuGet")]
    public void ParseOwnDirectives_WhenPredicate_FollowsTheGivenPlatform(
        SandboxPlatform platform, string kept, string dropped)
    {
        var entries = SandboxPathInspector.ParseOwnDirectives(SampleVrGuardJson(), platform, Home);

        Assert.Contains(entries, e => e.Raw == kept && e.Access == SandboxAccess.ReadWrite);
        Assert.DoesNotContain(entries, e => e.Raw == dropped);
        // An entry without a predicate is kept on either platform.
        Assert.Contains(entries, e => e.Raw == "~/.npm");
    }

    [Theory]
    [InlineData(SandboxPlatform.Linux, "/linux/specific/path", "/mac/specific/path")]
    [InlineData(SandboxPlatform.MacOs, "/mac/specific/path", "/linux/specific/path")]
    public void ParseGroupJson_PlatformEntries_FollowTheGivenPlatform(
        SandboxPlatform platform, string kept, string dropped)
    {
        var entries = SandboxPathInspector.ParseGroupJson(SampleCrossPlatformGroupJson(), "cross_plat", platform, Home);

        Assert.Contains(entries, e => e.Raw == kept);
        Assert.DoesNotContain(entries, e => e.Raw == dropped);
        Assert.Contains(entries, e => e.Raw == "/usr/share/common");
    }

    [Theory]
    [InlineData(SandboxPlatform.Linux, "deny_keychains_linux", "deny_keychains_macos")]
    [InlineData(SandboxPlatform.MacOs, "deny_keychains_macos", "deny_keychains_linux")]
    public async Task ExpandInheritedGroupsAsync_GroupPlatform_FollowsTheGivenPlatform(
        SandboxPlatform platform, string kept, string dropped)
    {
        var entries = await SandboxPathInspector.ExpandInheritedGroupsAsync(
            SampleResolvedShowJson(), GroupPayloadProvider, platform, Home);

        Assert.NotNull(entries);
        Assert.Contains(entries, e => e.Source == kept);
        Assert.DoesNotContain(entries, e => e.Source == dropped);
    }

    [Fact]
    public void CurrentPlatform_IsMacOSOnlyOnMacOS()
    {
        // Windows counts as Linux: the profile is enforced inside the WSL distro.
        var expected = OperatingSystem.IsMacOS() ? SandboxPlatform.MacOs : SandboxPlatform.Linux;

        Assert.Equal(expected, SandboxPathInspector.CurrentPlatform);
    }

    [Theory]
    [InlineData("~/.ssh", "/home/alice/.ssh")]
    [InlineData("$HOME/.cache", "/home/alice/.cache")]
    [InlineData("/usr/bin", "/usr/bin")]
    public void ExpandPath_ExpandsAgainstTheGivenHome(string raw, string expected) =>
        Assert.Equal(expected, SandboxPathInspector.ExpandPath(raw, "/home/alice"));

    [Fact]
    public void ExpandPath_WithoutAHome_ReturnsTheRawPath() =>
        Assert.Equal("~/.ssh", SandboxPathInspector.ExpandPath("~/.ssh", null));

    [Fact]
    public async Task InspectThroughWsl_AsksNonoInsideTheDistroThroughAPlainExec()
    {
        var launches = new List<WslLaunch>();

        var result = await SandboxPathInspector.InspectThroughWslAsync(
            Wsl, (launch, _) => { launches.Add(launch); return AnswerNono(launch); },
            _ => Task.FromResult(LinuxProfile),
            UncWorkspace, ["~/.local/share/acme"], CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.All(launches, launch => Assert.Equal(Wsl.WslExePath, launch.FileName));
        Assert.All(launches, launch => Assert.Equal(WslExeEnvironment.Variables, launch.Environment));
        string[] show = ["-d", "Ubuntu", "--exec", "/usr/local/bin/nono", "profile", "show", LinuxProfile, "--json"];
        Assert.Equal(show, launches[0].Arguments);
        string[] group = ["-d", "Ubuntu", "--exec", "/usr/local/bin/nono", "profile", "groups", "deny_credentials", "--json"];
        Assert.Contains(launches, launch => launch.Arguments.SequenceEqual(group));
        // The enforced platform is Linux, and ~ is the distro user's home.
        Assert.Contains(result.BlockedPaths, e => e.Source == "deny_keychains_linux");
        Assert.DoesNotContain(result.BlockedPaths, e => e.Source == "deny_keychains_macos");
        Assert.Contains(result.WritablePaths, e => e.Raw == "~/.cache/pip");
        Assert.DoesNotContain(result.WritablePaths, e => e.Raw == "~/Library/Caches/pip");
        Assert.Contains(result.BlockedPaths, e => e.Raw == "~/Documents" && e.Expanded == "/home/alice/Documents");
        Assert.Contains(result.WritablePaths, e => e.Raw == "~/.local/share/acme" && e.Expanded == "/home/alice/.local/share/acme");
        // The workspace stays the UNC root the app holds; its Linux view is the tooltip.
        Assert.Contains(result.WritablePaths,
            e => e.Raw == UncWorkspace && e.Expanded == "/home/alice/repo" && e.Source == "current workspace");
    }

    [Fact]
    public async Task InspectThroughWsl_ProfileUnreachable_IsUnavailableAndAsksNothing()
    {
        var launches = 0;

        var result = await SandboxPathInspector.InspectThroughWslAsync(
            Wsl, (_, _) => { launches++; return Task.FromResult<string?>("{}"); },
            _ => throw new InvalidOperationException("The network path was not found."),
            null, null, CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task InspectThroughWsl_ShowFailing_IsUnavailable()
    {
        var result = await SandboxPathInspector.InspectThroughWslAsync(
            Wsl, (_, _) => Task.FromResult<string?>(null), _ => Task.FromResult(LinuxProfile),
            null, null, CancellationToken.None);

        Assert.False(result.IsAvailable);
    }

    [Fact]
    public async Task InspectThroughWsl_Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SandboxPathInspector.InspectThroughWslAsync(
            Wsl, (_, _) => Task.FromResult<string?>("{}"),
            ct => { ct.ThrowIfCancellationRequested(); return Task.FromResult(LinuxProfile); },
            null, null, cts.Token));
    }

    [Fact]
    public void BuildResult_SummarySaysReadsAreEverythingButTheBlockedPaths()
    {
        var result = SandboxPathInspector.BuildResult(
            [new SandboxPathEntry("/", "/", SandboxAccess.ReadOnly, "vr-guard")]);

        Assert.Contains("except the blocked paths", result.ReadsSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoCaveatSurvivesAnywhereInTheResultOrTheViewModel()
    {
        // The denials are enforced by the kernel on every platform now, so nothing
        // may carry a "may be readable" caveat any more.
        Assert.DoesNotContain(typeof(SandboxInspectionResult).GetProperties(),
            p => p.Name.Contains("Caveat", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(MainWindowViewModel).GetProperties(),
            p => p.Name.Contains("Caveat", StringComparison.Ordinal));
    }

    /// <summary>Answers a recorded nono query from the fixtures: the resolved chain for show, the group payload for groups.</summary>
    private static Task<string?> AnswerNono(WslLaunch launch)
    {
        var args = launch.Arguments;
        return args[5] == "show" ? Task.FromResult<string?>(SampleResolvedShowJson()) : GroupPayloadProvider(args[6]);
    }
}
