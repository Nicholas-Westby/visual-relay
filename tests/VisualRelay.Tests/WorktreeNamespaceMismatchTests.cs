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
    private const string VrNoSuchDistroRoot = @"\\wsl.localhost\VrNoSuchDistro\home\alice\repo";

    private static SandboxHost HostOf(string distro) => SandboxHost.Windows(
        new WslContext(@"C:\Windows\System32\wsl.exe", distro, "/usr/local/bin/nono", "/home/alice"));

    [Fact]
    public void ADivergentDistro_IsAMismatchNamingBoth()
    {
        var reason = WorktreeNamespace.Mismatch(VrNoSuchDistroRoot, HostOf("VrNoSuchOtherDistro"));

        Assert.NotNull(reason);
        Assert.Contains("'VrNoSuchDistro'", reason, StringComparison.Ordinal);
        Assert.Contains("'VrNoSuchOtherDistro'", reason, StringComparison.Ordinal);
        Assert.Contains("VR_WSL_DISTRO=VrNoSuchDistro", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AUncRootWithNoResolvedDistro_IsAMismatch()
    {
        var reason = WorktreeNamespace.Mismatch(VrNoSuchDistroRoot, SandboxHost.Windows(null));

        Assert.NotNull(reason);
        Assert.Contains("'VrNoSuchDistro'", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMatchingDistro_IsNoMismatch()
    {
        Assert.Null(WorktreeNamespace.Mismatch(VrNoSuchDistroRoot, HostOf("VrNoSuchDistro")));
    }

    /// <summary>A drive root is served by Git for Windows and refused by the workspace policy.</summary>
    [Fact]
    public void ADriveRoot_IsNoMismatch()
    {
        Assert.Null(WorktreeNamespace.Mismatch(@"C:\dev\repo", HostOf("VrNoSuchDistro")));
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
            () => WorktreeNamespace.For(VrNoSuchDistroRoot, HostOf("VrNoSuchOtherDistro")));

        Assert.Contains("'VrNoSuchDistro'", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath(), refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADivergentDistro_RefusesToNameATempIndex()
    {
        Assert.Throws<InvalidOperationException>(
            () => WorktreeNamespace.TempIndexFor(VrNoSuchDistroRoot, HostOf("VrNoSuchOtherDistro")));
    }

    [Fact]
    public void AUncRootWithNoResolvedDistro_RefusesToNameAWorktree()
    {
        Assert.Throws<InvalidOperationException>(
            () => WorktreeNamespace.For(VrNoSuchDistroRoot, SandboxHost.Windows(null)));
    }

    /// <summary>The matching case keeps working: the refusal must not become a blanket one.</summary>
    [Fact]
    public void TheMatchingDistro_StillNamesTheWorktreeInsideIt()
    {
        var worktree = WorktreeNamespace.For(VrNoSuchDistroRoot, HostOf("VrNoSuchDistro"));

        Assert.Equal("VrNoSuchDistro", worktree.Distro);
        Assert.StartsWith("/home/alice/.cache/visual-relay", worktree.Git, StringComparison.Ordinal);
    }
}
