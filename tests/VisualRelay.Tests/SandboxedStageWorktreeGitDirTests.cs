using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// A linked worktree keeps only a <c>.git</c> file; its objects and refs live in the main
/// repository's git dir, outside the workspace nono grants. Measured on WSL with the Linux
/// profile: in VR's planning worktree under <c>~/.cache/visual-relay/wt</c> the research
/// agent's <c>git log</c> and <c>git status</c> exited 128 ("not a git repository"), and
/// both worked once that git dir was granted read. macOS's whole-filesystem read hid it.
/// </summary>
public sealed partial class SandboxedStageSandboxTests
{
    [Fact]
    public void BuildNonoPrefix_InALinkedWorktree_ReadsTheMainRepositorysGitDir()
    {
        var root = Directory.CreateTempSubdirectory("vr-wt-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: /home/u/repo/.git/worktrees/t1\n");

            var prefix = ComposeFor(root);

            Assert.Equal("/home/u/repo/.git", ReadGrantOf(prefix));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildNonoPrefix_WithARelativeGitDir_ReadsItResolvedAgainstTheWorktree()
    {
        var parent = Directory.CreateTempSubdirectory("vr-wt-rel-").FullName;
        var root = Directory.CreateDirectory(Path.Combine(parent, "wt")).FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ../repo/.git/worktrees/wt\r\n");

            var prefix = ComposeFor(root);

            Assert.Equal(Path.Combine(parent, "repo", ".git"), ReadGrantOf(prefix));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void BuildNonoPrefix_InAnOrdinaryRepository_AddsNoReadGrant()
    {
        var root = Directory.CreateTempSubdirectory("vr-repo-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));

            Assert.DoesNotContain("--read", ComposeFor(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildNonoPrefix_InASubmodule_ReadsItsOwnGitDir()
    {
        var root = Directory.CreateTempSubdirectory("vr-sub-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: /home/u/super/.git/modules/lib\n");

            Assert.Equal("/home/u/super/.git/modules/lib", ReadGrantOf(ComposeFor(root)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IReadOnlyList<string> ComposeFor(string workspaceRoot) =>
        SandboxedStage.ComposeNonoPrefix(
            TestConfig(), rollback: false, skipDirs: null, verboseDiagnostics: false,
            templatesDir: Path.Combine(workspaceRoot, "templates"), workspaceRoot, requestDiagnostics: false,
            SandboxHost.Local);

    private static string? ReadGrantOf(IReadOnlyList<string> prefix)
    {
        var index = prefix.ToList().IndexOf("--read");
        return index >= 0 && index + 1 < prefix.Count ? prefix[index + 1] : null;
    }
}
