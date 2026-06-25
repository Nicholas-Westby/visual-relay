using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests the Windows arm of <see cref="SwivalSubagentRunner.BuildLaunchTarget"/>:
/// the mode dispatch wraps swival in MXC, appends the builtin flag, or blocks —
/// asserted on any OS by driving the dispatch method with an explicit mode.
/// </summary>
public sealed class WindowsLaunchTargetTests
{
    private static SwivalSubagentRunner Runner() =>
        new(TestConfig(), backendProbe: SwivalTestHelpers.AlwaysReady);

    [Fact]
    public void Windows_Mxc_WrapsSwivalInWxcExecWithPolicy()
    {
        var swivalArgs = new List<string> { "-q", "--base-dir", @"C:\repo" };

        var (fileName, args) = Runner().BuildWindowsLaunchTarget(
            swivalArgs, WindowsSandboxMode.Mxc, @"C:\mxc\wxc-exec.exe", @"C:\cfg\policy.json");

        Assert.Equal(@"C:\mxc\wxc-exec.exe", fileName);
        Assert.Equal(
            new[] { @"C:\cfg\policy.json", "swival", "-q", "--base-dir", @"C:\repo" }, args);
    }

    [Fact]
    public void Windows_Builtin_AppendsSandboxFlag_LaunchesSwivalDirectly()
    {
        var (fileName, args) = Runner().BuildWindowsLaunchTarget(
            new List<string> { "-q", "--report", "r.json" }, WindowsSandboxMode.Builtin, null, null);

        Assert.Equal("swival", fileName);
        Assert.Equal(new[] { "-q", "--report", "r.json", "--sandbox", "builtin" }, args);
    }

    [Fact]
    public void Windows_Blocked_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Runner().BuildWindowsLaunchTarget(
                new List<string> { "-q" }, WindowsSandboxMode.Blocked, null, null));

        Assert.Contains("blocked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

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
