using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The fail-fast tool-presence gate on Windows: the requirement is a usable WSL2
/// distro with nono inside it (the resolved <see cref="WslContext"/>), not a
/// binary on the Windows PATH. Off Windows the requirement is still nono on PATH.
/// </summary>
public sealed class SandboxedStageToolPresenceWslTests
{
    private static readonly WslContext Context =
        new(@"C:\Windows\System32\wsl.exe", "Ubuntu", "/usr/local/bin/nono", "/home/alice");

    [Fact]
    public void MissingRequiredTools_WindowsWithoutAUsableDistro_NamesWslAndNonoInsideTheDistro()
    {
        var missing = SandboxedStage.MissingRequiredTools(TestConfig(), pathValue: string.Empty, host: SandboxHost.Windows(null));

        var requirement = Assert.Single(missing);
        Assert.Contains("WSL2", requirement);
        Assert.Contains("nono", requirement);
        Assert.Contains("distro", requirement);
        Assert.Contains("nono", SandboxedStage.MissingToolsMessage(missing));
    }

    [Fact]
    public void MissingRequiredTools_WindowsWithAUsableDistro_IsSatisfiedWhateverTheWindowsPathHolds()
    {
        var missing = SandboxedStage.MissingRequiredTools(TestConfig(), pathValue: string.Empty, host: SandboxHost.Windows(Context));

        Assert.Empty(missing);
    }

    [Fact]
    public void MissingRequiredTools_LocalHost_StillRequiresNonoOnPath()
    {
        var missing = SandboxedStage.MissingRequiredTools(TestConfig(), pathValue: string.Empty, host: SandboxHost.Local);

        Assert.Equal(["nono"], missing);
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
