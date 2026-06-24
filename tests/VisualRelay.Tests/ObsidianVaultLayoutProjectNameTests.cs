using VisualRelay.Core.ObsidianBridge;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for <see cref="ObsidianVaultLayout.ResolveProjectFolderNameAsync"/> —
/// the stable vault-folder-name derivation that prefers the git repo-root leaf
/// over the volatile directory leaf, and falls back gracefully when git is
/// unavailable.
/// </summary>
public sealed class ObsidianVaultLayoutProjectNameTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { TestFileSystem.DeleteDirectoryResilient(dir); }
            catch { /* best-effort */ }
        }
    }

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vr-rpfn-tests",
            "rpfn-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public async Task ResolveProjectFolderName_GitRepo_UsesRepoRootLeaf()
    {
        // Given a git repo whose top-level folder is named "my-project",
        // resolve from that root itself returns "my-project".
        var repo = TempDir();
        var projectDir = Path.Combine(repo, "my-project");
        Directory.CreateDirectory(projectDir);
        TestGit.Run(projectDir, "init");

        var name = await ObsidianVaultLayout.ResolveProjectFolderNameAsync(projectDir);

        Assert.Equal("my-project", name);
    }

    [Fact]
    public async Task ResolveProjectFolderName_GitSubdir_ResolvesToRepoRoot()
    {
        // Given a git repo whose top-level is "my-project",
        // resolving from a subdirectory of it should still return "my-project".
        // This is the worktree/temp-checkout scenario: the working directory
        // leaf may differ from the repo root leaf.
        var repo = TempDir();
        var projectDir = Path.Combine(repo, "my-project");
        Directory.CreateDirectory(projectDir);
        TestGit.Run(projectDir, "init");

        var subDir = Path.Combine(projectDir, "sub", "deep");
        Directory.CreateDirectory(subDir);

        var name = await ObsidianVaultLayout.ResolveProjectFolderNameAsync(subDir);

        Assert.Equal("my-project", name);
    }

    [Fact]
    public async Task ResolveProjectFolderName_NonGitDir_FallsBackToDirectoryLeaf()
    {
        // When the directory is not inside a git work tree, fall back to
        // Path.GetFileName of the directory itself.
        var dir = TempDir();
        var leafDir = Path.Combine(dir, "plain-directory");
        Directory.CreateDirectory(leafDir);

        var name = await ObsidianVaultLayout.ResolveProjectFolderNameAsync(leafDir);

        Assert.Equal("plain-directory", name);
    }

    [Fact]
    public async Task ResolveProjectFolderName_GitError_FallsBackToDirectoryLeaf()
    {
        // When git fails (non-git directory causes rev-parse to return
        // non-zero), gracefully fall back to the directory leaf.
        var dir = TempDir();
        var leafDir = Path.Combine(dir, "graceful-fallback");
        Directory.CreateDirectory(leafDir);

        var name = await ObsidianVaultLayout.ResolveProjectFolderNameAsync(leafDir);

        Assert.Equal("graceful-fallback", name);
    }
}
