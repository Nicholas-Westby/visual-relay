using VisualRelay.GitSim;
using static VisualRelay.Tests.GitSimTestHelpers;

namespace VisualRelay.Tests;

/// <summary>
/// Always-on unit facts for the core GitSim command groups — rev-parse, add, commit
/// (incl. the pre-commit hook), commit-tree, reset/checkout, status, diff, ls-files —
/// each asserting the exact output shape production parses.
/// </summary>
public sealed class GitSimTests
{
    [Fact]
    public async Task RevParse_IsInsideWorkTree_PrintsTrue()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        var (exit, output) = await sim.Git(repo.Root, "rev-parse", "--is-inside-work-tree");
        Assert.Equal(0, exit);
        Assert.Equal("true\n", output);
    }

    [Fact]
    public async Task RevParse_Head_PrintsFortyHexSha()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "a.txt", "1");
        sim.Commit(repo.Root, "seed");
        var (exit, output) = await sim.Git(repo.Root, "rev-parse", "HEAD");
        Assert.Equal(0, exit);
        Assert.True(IsFortyHex(output.Trim()));
    }

    [Fact]
    public async Task RevParse_VerifyQuietHead_UnbornExitsOneWithNoOutput()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        var (exit, output) = await sim.Git(repo.Root, "rev-parse", "--verify", "--quiet", "HEAD");
        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public async Task Add_ThenCommit_AdvancesHeadAndRecordsMessage()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        Write(repo, "src/app.cs", "content");
        var (addExit, _) = await sim.Git(repo.Root, "add", "-A", "--", "src/app.cs");
        var (commitExit, _) = await sim.Git(repo.Root, "commit", "-m", "feat: add app");
        Assert.Equal(0, addExit);
        Assert.Equal(0, commitExit);
        var head = sim.Head(repo.Root);
        Assert.NotNull(head);
        Assert.Equal("feat: add app", sim.CommitInfo(repo.Root, head!)!.Message);
        Assert.Contains("src/app.cs", sim.FilesInCommit(repo.Root, head!));
    }

    [Fact]
    public async Task FilesChangedInCommit_ReportsThisCommitsDiff_NotTheWholeTree()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        // Seed two files, then a second commit that only touches one of them.
        sim.Seed(repo.Root, "src/a.cs", "a0");
        sim.Seed(repo.Root, "src/b.cs", "b0");
        sim.Commit(repo.Root, "seed");
        sim.Seed(repo.Root, "src/b.cs", "b1");
        var head = sim.Commit(repo.Root, "edit b");

        // FilesChangedInCommit is the per-commit diff (git show --name-only): only b.
        var changed = sim.FilesChangedInCommit(repo.Root, head);
        Assert.Equal(new[] { "src/b.cs" }, changed);
        // FilesInCommit is the whole tree: still carries the untouched a.cs.
        Assert.Contains("src/a.cs", sim.FilesInCommit(repo.Root, head));

        // Parity with `diff-tree --no-commit-id --name-only -r <sha>`.
        var (exit, output) = await sim.Git(repo.Root, "diff-tree", "--no-commit-id", "--name-only", "-r", head);
        Assert.Equal(0, exit);
        Assert.Equal(changed, output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Commit_PreCommitHookRejects_NonZeroExitWithMessageInOutput()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.PreCommitHook = _ => GitSimHookVerdict.Reject("hook: subject matches rejected pattern");
        Write(repo, "a.txt", "x");
        await sim.Git(repo.Root, "add", "-A");
        var (exit, output) = await sim.Git(repo.Root, "commit", "-m", "bad subject");
        Assert.NotEqual(0, exit);
        Assert.Contains("hook: subject matches rejected pattern", output);
        Assert.Null(sim.Head(repo.Root)); // rejected commit never advanced HEAD
    }

    [Fact]
    public async Task Commit_PreCommitHook_SeesStagedPathsMessageAndEnv()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        GitSimCommitRequest? seen = null;
        sim.PreCommitHook = req => { seen = req; return GitSimHookVerdict.Accept; };
        Write(repo, "staged.txt", "x");
        await sim.Git(repo.Root, "add", "-A");
        var env = new Dictionary<string, string> { ["RELAY_COMMIT_TOKEN"] = "tok-42" };
        await sim.GitEnv(repo.Root, env, "commit", "-m", "feat: subject");
        Assert.NotNull(seen);
        Assert.Contains("staged.txt", seen!.StagedPaths);
        Assert.Equal("feat: subject", seen.Message);
        Assert.Equal("tok-42", seen.Environment["RELAY_COMMIT_TOKEN"]);
    }

    [Fact]
    public async Task CommitTree_BypassesHook_PrintsShaEvenWhenHookWouldReject()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.PreCommitHook = _ => GitSimHookVerdict.Reject("should never be consulted");
        Write(repo, "a.txt", "x");
        await sim.Git(repo.Root, "add", "-A");
        var (_, treeOut) = await sim.Git(repo.Root, "write-tree");
        var (exit, output) = await sim.Git(repo.Root, "commit-tree", treeOut.Trim(), "-m", "snapshot");
        Assert.Equal(0, exit);
        Assert.True(IsFortyHex(output.Trim()));
    }

    [Fact]
    public async Task Reset_MixedResetToHead_DropsStagedNewFile()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "a.txt", "1");
        sim.Commit(repo.Root, "seed");
        Write(repo, "b.txt", "2");
        await sim.Git(repo.Root, "add", "-A");
        Assert.Contains("b.txt", sim.StagedPaths(repo.Root));
        await sim.Git(repo.Root, "reset", "-q");
        Assert.DoesNotContain("b.txt", sim.StagedPaths(repo.Root));
    }

    [Fact]
    public async Task Checkout_HeadPath_RestoresWorkingTreeFile()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "a.txt", "orig");
        sim.Commit(repo.Root, "seed");
        Write(repo, "a.txt", "changed");
        var (exit, _) = await sim.Git(repo.Root, "checkout", "HEAD", "--", "a.txt");
        Assert.Equal(0, exit);
        Assert.Equal("orig", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
    }

    [Fact]
    public async Task Status_Porcelain_EmptyWhenCleanNonEmptyWhenUntracked()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "a.txt", "1");
        sim.Commit(repo.Root, "seed");
        var (cleanExit, cleanOut) = await sim.Git(repo.Root, "status", "--porcelain");
        Assert.Equal(0, cleanExit);
        Assert.Equal(string.Empty, cleanOut);

        Write(repo, "u.txt", "x");
        var (_, dirtyOut) = await sim.Git(repo.Root, "status", "--porcelain");
        Assert.Contains("?? u.txt", dirtyOut);
    }

    [Fact]
    public async Task Diff_HeadNameOnlyZ_EmitsNulTerminatedChangedPath()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "a.txt", "1");
        sim.Commit(repo.Root, "seed");
        Write(repo, "a.txt", "2");
        var (exit, output) = await sim.Git(repo.Root, "diff", "HEAD", "--name-only", "-z");
        Assert.Equal(0, exit);
        Assert.Equal("a.txt\0", output);
    }

    [Fact]
    public async Task Diff_QuietHead_ExitsOneOnDifferenceZeroWhenRestored()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "a.txt", "1");
        sim.Commit(repo.Root, "seed");
        Write(repo, "a.txt", "2");
        var (differs, _) = await sim.Git(repo.Root, "diff", "--quiet", "HEAD", "--", "a.txt");
        Assert.Equal(1, differs);
        await sim.Git(repo.Root, "checkout", "HEAD", "--", "a.txt");
        var (same, _) = await sim.Git(repo.Root, "diff", "--quiet", "HEAD", "--", "a.txt");
        Assert.Equal(0, same);
    }

    [Fact]
    public async Task LsFiles_OthersExcludeStandard_ListsOnlyUntrackedNonIgnored()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "a.txt", "1");
        sim.Commit(repo.Root, "seed");
        Write(repo, "u.txt", "x");
        var (exit, output) = await sim.Git(repo.Root, "ls-files", "--others", "--exclude-standard");
        Assert.Equal(0, exit);
        Assert.Equal("u.txt\n", output);
    }
}
