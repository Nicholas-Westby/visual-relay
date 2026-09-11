using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// Where a Windows workspace may live. A Windows drive seen through DrvFs
/// (<c>/mnt/&lt;letter&gt;/...</c>) is refused through one policy constant: the
/// spec's conservative default until the DrvFs confinement probe has been run on
/// a Windows box, and the one-line downgrade point (to a warning) once it has.
/// </summary>
public sealed class WslWorkspacePolicyTests
{
    [Fact]
    public void MntWorkspace_IsRefusedWithTheMoveMessage()
    {
        var (decision, message) = WslWorkspacePolicy.Decide("/mnt/c/Users/x/repo");

        Assert.Equal(WslWorkspaceDecision.Refuse, decision);
        Assert.NotNull(message);
        Assert.Contains("/mnt/c/Users/x/repo", message);
        Assert.Contains("DrvFs", message);
        Assert.Contains("order of magnitude slower", message);
        Assert.Contains("Landlock", message);
        Assert.Contains("/home/<user>/", message);
        Assert.Contains(@"\\wsl.localhost\<distro>\home\<user>\", message);
    }

    [Fact]
    public void DistroWorkspace_IsAllowedSilently()
    {
        Assert.Equal((WslWorkspaceDecision.Allow, (string?)null), WslWorkspacePolicy.Decide("/home/u/repo"));
    }

    [Theory]
    [InlineData("/mnt/wsl/x")]
    [InlineData("/mnt")]
    [InlineData("/mnt/")]
    [InlineData("/mntx/c")]
    [InlineData("/srv/repo")]
    public void MntPrefixWithoutADriveLetter_IsNotADrive(string workspace)
    {
        Assert.Equal(WslWorkspaceDecision.Allow, WslWorkspacePolicy.Decide(workspace).Decision);
    }

    [Fact]
    public void MntPolicy_IsRefuse_UntilTheDrvFsProbeSaysOtherwise()
    {
        Assert.Equal(WslWorkspaceDecision.Refuse, WslWorkspacePolicy.MntPolicy);
    }

    [Fact]
    public void Decide_FollowsThePolicyConstant_SoADowngradeIsOneLine()
    {
        Assert.Equal(WslWorkspacePolicy.MntPolicy, WslWorkspacePolicy.Decide("/mnt/d/work").Decision);
    }
}
