using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// For a test that runs bootstrap or the run gate against this machine's own sandbox host.
/// On Windows that host is the machine's WSL distro, and where there is no usable one both
/// refuse before the behaviour the test is about: ten tests failed that way on a Windows PC
/// whose WSL had been removed (2026-09-19). There such a test is skipped, with the gate's first
/// line as the reason; with a usable distro, and on every other platform, it runs.
/// </summary>
internal static class MachineSandbox
{
    public static async Task SkipUnlessUsableAsync()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var host = await SandboxHost.CurrentAsync();
        Assert.SkipWhen(host.Wsl is null,
            "this test runs its commands in this machine's WSL distro, and there is no usable one: "
            + WslGate.Decide(WslContextResolver.UnusableProbe).Message?.Split('\n')[0]);
    }
}
