using System.Security.Cryptography;
using System.Text;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// Where a run's throwaway worktrees, verify snapshots and temporary git index
/// files live. On Windows the workspace is inside a WSL distro and the git that
/// serves it runs there, to which a Windows temp path is a relative name — so the
/// namespace moves into the distro, while .NET still needs the same directory
/// named as the UNC share.
/// </summary>
public sealed class WorktreeNamespaceTests
{
    private const string UncRoot = @"\\wsl.localhost\VrNoSuchDistro\home\alice\repo";

    private static readonly SandboxHost WindowsHost = SandboxHost.Windows(
        new WslContext(@"C:\Windows\System32\wsl.exe", "VrNoSuchDistro", "/usr/local/bin/nono", "/home/alice"));

    [Fact]
    public void WindowsHost_UncRoot_PutsTheWorktreeInsideTheDistro()
    {
        var worktree = WorktreeNamespace.For(UncRoot, WindowsHost)
            .Worktrees(UncRoot, isRewrite: false)
            .Child("run-1", "task-a");

        Assert.StartsWith("/home/alice/.cache/visual-relay/wt/", worktree.Git, StringComparison.Ordinal);
        Assert.EndsWith("/run-1/task-a", worktree.Git, StringComparison.Ordinal);
        // The same directory, named as .NET opens it.
        Assert.Equal(WslPath.ToUnc("VrNoSuchDistro", worktree.Git), worktree.Io);
        Assert.StartsWith(@"\\wsl.localhost\VrNoSuchDistro\home\alice\.cache\", worktree.Io, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsHost_UncRoot_PutsTheTempIndexUnderTheDistrosTmp()
    {
        var index = WorktreeNamespace.TempIndexFor(UncRoot, WindowsHost).Child("git-index-abc");

        Assert.Equal("/tmp/visual-relay/git-index-abc", index.Git);
        Assert.Equal(@"\\wsl.localhost\VrNoSuchDistro\tmp\visual-relay\git-index-abc", index.Io);
    }

    /// <summary>
    /// A rewrite keeps the disjoint top-level segment that stops a drain's prune
    /// from deleting a live rewrite worktree, inside the distro as on this machine.
    /// </summary>
    [Fact]
    public void WindowsHost_RewriteWorktrees_KeepTheirOwnSegment()
    {
        var rewrite = WorktreeNamespace.For(UncRoot, WindowsHost).Worktrees(UncRoot, isRewrite: true);

        Assert.StartsWith("/home/alice/.cache/visual-relay/wt-rewrite/", rewrite.Git, StringComparison.Ordinal);
    }

    /// <summary>
    /// The local host keeps today's paths exactly: every existing worktree test
    /// pins them, and nothing about macOS or Linux changed.
    /// </summary>
    [Fact]
    public void LocalHost_IsTodaysTempPath_InBothForms()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-ns-" + Guid.NewGuid().ToString("N"));
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root))))[..12].ToLowerInvariant();
        var expected = Path.Combine(Path.GetTempPath(), "visual-relay", "wt", hash, "run-1", "task-a");

        var worktree = WorktreeNamespace.For(root, SandboxHost.Local)
            .Worktrees(root, isRewrite: false)
            .Child("run-1", "task-a");

        Assert.Equal(expected, worktree.Io);
        Assert.Equal(expected, worktree.Git);
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "git-index-abc"),
            WorktreeNamespace.TempIndexFor(root, SandboxHost.Local).Child("git-index-abc").Git);
    }

    /// <summary>
    /// A throwaway file VR writes and the serving git then reads — the rewritten
    /// commit message <c>commit-tree -F</c> is handed. It has to be openable by that
    /// git, which is inside the distro when the workspace is, and by .NET, which is
    /// not. Locally it stays the temp path it has always been, in both forms.
    /// </summary>
    [Fact]
    public void TempFile_IsInsideTheDistroOnWindows_AndTodaysTempPathLocally()
    {
        var windows = WorktreeNamespace.TempFileFor(UncRoot, WindowsHost, "conform-msg-abc.txt");

        Assert.Equal("/tmp/visual-relay/conform-msg-abc.txt", windows.Git);
        Assert.Equal(@"\\wsl.localhost\VrNoSuchDistro\tmp\visual-relay\conform-msg-abc.txt", windows.Io);

        var local = WorktreeNamespace.TempFileFor(
            Path.Combine(Path.GetTempPath(), "vr-repo"), SandboxHost.Local, "conform-msg-abc.txt");
        var expected = Path.Combine(Path.GetTempPath(), "conform-msg-abc.txt");

        Assert.Equal(expected, local.Git);
        Assert.Equal(expected, local.Io);
    }

    /// <summary>
    /// A Windows drive workspace is refused by the workspace policy, never served
    /// by the distro's git, so its namespace stays where it is today.
    /// </summary>
    [Fact]
    public void WindowsHost_DriveRoot_StaysOnTheWindowsTempPath()
    {
        var worktrees = WorktreeNamespace.For(@"C:\src\repo", WindowsHost);

        Assert.Null(worktrees.Distro);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "visual-relay"), worktrees.Io);
    }

    /// <summary>
    /// The path handed to <c>git worktree remove</c>, which runs inside the distro
    /// when the workspace does: VR holds the UNC form and git needs the Linux one.
    /// </summary>
    [Fact]
    public void ForGit_TranslatesAUncWorktreePath_OnlyOnTheWindowsHost()
    {
        const string unc = @"\\wsl.localhost\VrNoSuchDistro\home\alice\.cache\visual-relay\wt\abc123\run\task";

        Assert.Equal(
            "/home/alice/.cache/visual-relay/wt/abc123/run/task", WorktreeNamespace.ForGit(unc, WindowsHost));
        Assert.Equal(unc, WorktreeNamespace.ForGit(unc, SandboxHost.Local));
    }
}
