using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The workspace and the git that serves it must agree about which distro they are in.
/// <c>GitRouting</c> sends every call into whatever distro the UNC path names, while the
/// worktree namespace used to fall back to a Windows temp path when they diverged — and
/// the distro's git does NOT refuse that path. Measured on a real Windows box: it is one
/// long directory NAME on ext4, so git created it INSIDE the operator's repository,
/// registered it as a real worktree and exited 0 in 45 ms. Nothing threw, nothing
/// retried, nothing was logged, and verify then ran the suite against the real checkout.
/// </summary>
public sealed class WorktreeNamespaceMismatchTests
{
    private const string UbuntuRoot = @"\\wsl.localhost\Ubuntu\home\alice\repo";

    private static SandboxHost HostOf(string distro) => SandboxHost.Windows(
        new WslContext(@"C:\Windows\System32\wsl.exe", distro, "/usr/local/bin/nono", "/home/alice"));

    [Fact]
    public void ADivergentDistro_IsAMismatchNamingBoth()
    {
        var reason = WorktreeNamespace.Mismatch(UbuntuRoot, HostOf("Debian"));

        Assert.NotNull(reason);
        Assert.Contains("'Ubuntu'", reason, StringComparison.Ordinal);
        Assert.Contains("'Debian'", reason, StringComparison.Ordinal);
        Assert.Contains("VR_WSL_DISTRO=Ubuntu", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AUncRootWithNoResolvedDistro_IsAMismatch()
    {
        var reason = WorktreeNamespace.Mismatch(UbuntuRoot, SandboxHost.Windows(null));

        Assert.NotNull(reason);
        Assert.Contains("'Ubuntu'", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMatchingDistro_IsNoMismatch()
    {
        Assert.Null(WorktreeNamespace.Mismatch(UbuntuRoot, HostOf("Ubuntu")));
    }

    /// <summary>A drive root is served by Git for Windows and refused by the workspace policy.</summary>
    [Fact]
    public void ADriveRoot_IsNoMismatch()
    {
        Assert.Null(WorktreeNamespace.Mismatch(@"C:\dev\repo", HostOf("Ubuntu")));
    }

    [Fact]
    public void TheLocalHost_IsNoMismatch()
    {
        Assert.Null(WorktreeNamespace.Mismatch("/home/alice/repo", SandboxHost.Local));
    }

    /// <summary>
    /// The refusal is what stops the silent corruption, so it must come from the
    /// namespace itself and not only from the gate that calls it first.
    /// </summary>
    [Fact]
    public void ADivergentDistro_RefusesToNameAWorktree()
    {
        var refusal = Assert.Throws<InvalidOperationException>(
            () => WorktreeNamespace.For(UbuntuRoot, HostOf("Debian")));

        Assert.Contains("'Ubuntu'", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath(), refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADivergentDistro_RefusesToNameATempIndex()
    {
        Assert.Throws<InvalidOperationException>(
            () => WorktreeNamespace.TempIndexFor(UbuntuRoot, HostOf("Debian")));
    }

    [Fact]
    public void AUncRootWithNoResolvedDistro_RefusesToNameAWorktree()
    {
        Assert.Throws<InvalidOperationException>(
            () => WorktreeNamespace.For(UbuntuRoot, SandboxHost.Windows(null)));
    }

    /// <summary>The matching case keeps working: the refusal must not become a blanket one.</summary>
    [Fact]
    public void TheMatchingDistro_StillNamesTheWorktreeInsideIt()
    {
        var worktree = WorktreeNamespace.For(UbuntuRoot, HostOf("Ubuntu"));

        Assert.Equal("Ubuntu", worktree.Distro);
        Assert.StartsWith("/home/alice/.cache/visual-relay", worktree.Git, StringComparison.Ordinal);
    }
}
