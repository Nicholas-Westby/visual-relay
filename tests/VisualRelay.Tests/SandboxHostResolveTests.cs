using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// Resolving where the sandbox runs without blocking the caller. The first Windows
/// resolution is a six-step wsl.exe probe; a UI-thread caller must await it, never
/// wait on it, and every synchronous reader afterwards takes the memoized answer.
/// Each fact states its machine in its own resolution
/// (<see cref="WslContextResolver.IsolateForTests"/>), which no other test can see.
/// </summary>
public sealed class SandboxHostResolveTests
{
    private static readonly WslContext Context =
        new(@"C:\Windows\System32\wsl.exe", "Ubuntu", "/usr/local/bin/nono", "/home/alice");

    [Fact]
    public async Task ResolveAsync_OnWindows_CarriesTheResolvedContext()
    {
        using var wsl = WslContextResolver.IsolateForTests(@override: Context);

        var host = await SandboxHost.ResolveAsync(isWindows: true, TestContext.Current.CancellationToken);

        Assert.True(host.IsWindows);
        Assert.Same(Context, host.Wsl);
    }

    [Fact]
    public async Task ResolveAsync_OffWindows_IsTheLocalHost()
    {
        using var wsl = WslContextResolver.IsolateForTests(@override: Context);

        Assert.Same(
            SandboxHost.Local,
            await SandboxHost.ResolveAsync(isWindows: false, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Windows with no usable distro is still the Windows host: the tool-presence
    /// gate names WSL rather than nono, and nothing is ever run unsandboxed. The
    /// unusable machine is scripted, so a Windows runner answers this from the
    /// fixture instead of starting the real wsl.exe probe.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_OnWindowsWithoutADistro_IsTheWindowsHostWithNoContext()
    {
        using var wsl = WslContextResolver.IsolateForTests(prober: _ => Task.FromResult(WslProbeFixtures.NoWsl()));

        var host = await SandboxHost.ResolveAsync(isWindows: true, TestContext.Current.CancellationToken);

        Assert.True(host.IsWindows);
        Assert.Null(host.Wsl);
    }
}
