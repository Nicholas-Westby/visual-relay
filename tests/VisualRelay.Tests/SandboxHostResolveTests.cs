using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// Resolving where the sandbox runs without blocking the caller. The first Windows
/// resolution is a six-step wsl.exe probe; a UI-thread caller must await it, never
/// wait on it, and every synchronous reader afterwards takes the memoized answer.
/// The resolved context is process-wide state, so these facts share the collection
/// that owns it and always clear the override again.
/// </summary>
[Collection("WslContext")]
public sealed class SandboxHostResolveTests
{
    private static readonly WslContext Context =
        new(@"C:\Windows\System32\wsl.exe", "Ubuntu", "/usr/local/bin/nono", "/home/alice");

    [Fact]
    public async Task ResolveAsync_OnWindows_CarriesTheResolvedContext()
    {
        try
        {
            WslContextResolver.Override(Context);

            var host = await SandboxHost.ResolveAsync(isWindows: true, TestContext.Current.CancellationToken);

            Assert.True(host.IsWindows);
            Assert.Same(Context, host.Wsl);
        }
        finally
        {
            WslContextResolver.Override(null);
        }
    }

    [Fact]
    public async Task ResolveAsync_OffWindows_IsTheLocalHost()
    {
        try
        {
            WslContextResolver.Override(Context);

            Assert.Same(
                SandboxHost.Local,
                await SandboxHost.ResolveAsync(isWindows: false, TestContext.Current.CancellationToken));
        }
        finally
        {
            WslContextResolver.Override(null);
        }
    }

    /// <summary>
    /// Windows with no usable distro is still the Windows host: the tool-presence
    /// gate names WSL rather than nono, and nothing is ever run unsandboxed.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_OnWindowsWithoutADistro_IsTheWindowsHostWithNoContext()
    {
        var host = await SandboxHost.ResolveAsync(isWindows: true, TestContext.Current.CancellationToken);

        Assert.True(host.IsWindows);
        // Off Windows there is nothing to probe; on Windows the real machine answers.
        if (!OperatingSystem.IsWindows())
            Assert.Null(host.Wsl);
    }
}
