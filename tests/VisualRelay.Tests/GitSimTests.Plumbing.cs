using static VisualRelay.Tests.GitSimTestHelpers;

namespace VisualRelay.Tests;

/// <summary>
/// Always-on facts for the plumbing / refs / config / worktree / log command groups:
/// write-tree + update-ref (incl. compare-and-swap), rm --cached, cat-file, merge-base,
/// rev-list, ls-tree, diff-tree, check-ignore, symbolic-ref, var, tag, config, worktree
/// materialization, and the log formats.
/// </summary>
public sealed class GitSimPlumbingTests
{
    private static async Task<(string C1, string C2)> TwoCommits(VisualRelay.GitSim.GitSim sim, TestRepository repo)
    {
        sim.Seed(repo.Root, "a.txt", "1");
        var c1 = sim.Commit(repo.Root, "feat: one");
        sim.Seed(repo.Root, "b.txt", "2");
        var c2 = sim.Commit(repo.Root, "feat: two");
        await Task.CompletedTask;
        return (c1, c2);
    }

    [Fact]
    public async Task WriteTree_PrintsFortyHexTreeSha()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        Write(repo, "a.txt", "1");
        await sim.Git(repo.Root, "add", "-A");
        var (exit, output) = await sim.Git(repo.Root, "write-tree");
        Assert.Equal(0, exit);
        Assert.True(IsFortyHex(output.Trim()));
    }

    [Fact]
    public async Task UpdateRef_SetsThenCompareAndSwapRejectsStaleExpected()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        var (c1, c2) = await TwoCommits(sim, repo);
        await sim.Git(repo.Root, "update-ref", "refs/relay-snapshot/t", c2);
        Assert.True(sim.RefExists(repo.Root, "refs/relay-snapshot/t"));

        var (casFail, _) = await sim.Git(repo.Root, "update-ref", "refs/relay-snapshot/t", c1, c1);
        Assert.NotEqual(0, casFail); // current is c2, expected c1 → rejected
        var (delExit, _) = await sim.Git(repo.Root, "update-ref", "-d", "refs/relay-snapshot/t");
        Assert.Equal(0, delExit);
        Assert.False(sim.RefExists(repo.Root, "refs/relay-snapshot/t"));
    }

    [Fact]
    public async Task RmCached_DropsPathFromIndexKeepsWorkingFile()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "a.txt", "1");
        sim.Commit(repo.Root, "seed");
        var (exit, _) = await sim.Git(repo.Root, "rm", "--cached", "-q", "--", "a.txt");
        Assert.Equal(0, exit);
        Assert.DoesNotContain("a.txt", sim.StagedPaths(repo.Root));
        Assert.True(File.Exists(Path.Combine(repo.Root, "a.txt")));
    }

    [Fact]
    public async Task CatFileExists_ZeroWhenPathInHeadNonZeroOtherwise()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "a.txt", "1");
        sim.Commit(repo.Root, "seed");
        var (present, _) = await sim.Git(repo.Root, "cat-file", "-e", "HEAD:a.txt");
        var (absent, _) = await sim.Git(repo.Root, "cat-file", "-e", "HEAD:missing.txt");
        Assert.Equal(0, present);
        Assert.NotEqual(0, absent);
    }

    [Fact]
    public async Task MergeBaseIsAncestor_ZeroForAncestorOneForNonAncestor()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        var (c1, c2) = await TwoCommits(sim, repo);
        var (isAnc, _) = await sim.Git(repo.Root, "merge-base", "--is-ancestor", c1, c2);
        var (notAnc, _) = await sim.Git(repo.Root, "merge-base", "--is-ancestor", c2, c1);
        Assert.Equal(0, isAnc);
        Assert.Equal(1, notAnc);
    }

    [Fact]
    public async Task RevList_ExcludesBaseListsOnlyRangeCommits()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        var (c1, c2) = await TwoCommits(sim, repo);
        var (exit, output) = await sim.Git(repo.Root, "rev-list", $"{c1}..{c2}");
        Assert.Equal(0, exit);
        Assert.Equal(c2 + "\n", output);
    }

    [Fact]
    public async Task LsTreeHead_NonEmptyForTrackedPath()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "a.txt", "1");
        sim.Commit(repo.Root, "seed");
        var (exit, output) = await sim.Git(repo.Root, "ls-tree", "HEAD", "--", "a.txt");
        Assert.Equal(0, exit);
        Assert.Contains("\ta.txt", output);
    }

    [Fact]
    public async Task DiffTree_ListsFilesChangedByCommit()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        var (_, c2) = await TwoCommits(sim, repo);
        var (exit, output) = await sim.Git(repo.Root, "diff-tree", "--no-commit-id", "--name-only", "-r", c2);
        Assert.Equal(0, exit);
        Assert.Equal("b.txt\n", output);
    }

    [Fact]
    public async Task CheckIgnore_PrintsIgnoredSubsetExitOneWhenNone()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        Write(repo, ".gitignore", "swival.toml\n");
        var (hit, hitOut) = await sim.Git(repo.Root, "check-ignore", "--", "swival.toml", "src/app.cs");
        Assert.Equal(0, hit);
        Assert.Equal("swival.toml\n", hitOut);
        var (miss, _) = await sim.Git(repo.Root, "check-ignore", "--", "src/app.cs");
        Assert.Equal(1, miss);
    }

    [Fact]
    public async Task SymbolicRefShort_PrintsBranchName()
    {
        var (sim, repo) = NewRepo("trunk");
        using var _ = repo;
        var (exit, output) = await sim.Git(repo.Root, "symbolic-ref", "--short", "--quiet", "HEAD");
        Assert.Equal(0, exit);
        Assert.Equal("trunk\n", output);
    }

    [Fact]
    public async Task Var_GitAuthorIdent_PrintsNameEmailTimestampZone()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        var (exit, output) = await sim.Git(repo.Root, "var", "GIT_AUTHOR_IDENT");
        Assert.Equal(0, exit);
        Assert.Matches(@"^.+ <.+@.+> \d+ [+-]\d{4}\n$", output);
    }

    [Fact]
    public async Task ConfigHooksPathDefault_ReturnsFallbackThenSetValue()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        var (_, fallback) = await sim.Git(repo.Root, "config", "--default", ".git/hooks", "core.hooksPath");
        Assert.Equal(".git/hooks\n", fallback);
        await sim.Git(repo.Root, "config", "core.hooksPath", ".husky");
        var (_, set) = await sim.Git(repo.Root, "config", "--default", ".git/hooks", "core.hooksPath");
        Assert.Equal(".husky\n", set);
    }

    [Fact]
    public async Task Tag_PointsRefAtCommitDiscoverableViaRefExists()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        var (_, c2) = await TwoCommits(sim, repo);
        var (exit, _) = await sim.Git(repo.Root, "tag", "-f", "backup/pre-conform", c2);
        Assert.Equal(0, exit);
        Assert.True(sim.RefExists(repo.Root, "backup/pre-conform"));
    }

    [Fact]
    public async Task WorktreeAddDetach_MaterializesHeadTreeAtLinkedPath()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "a.txt", "content");
        sim.Commit(repo.Root, "seed");
        var linked = Path.Combine(repo.Root, "..", $"wt-{Guid.NewGuid():N}");
        var (exit, _) = await sim.Git(repo.Root, "worktree", "add", "--detach", "--quiet", linked, "HEAD");
        Assert.Equal(0, exit);
        Assert.Equal("content", File.ReadAllText(Path.Combine(linked, "a.txt")));
        await sim.Git(repo.Root, "worktree", "remove", "--force", linked);
        Directory.Delete(linked, recursive: true);
    }

    [Fact]
    public async Task Log_FormatsSingleBodyAndReverseRange()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        var (_, c2) = await TwoCommits(sim, repo);
        var (_, body) = await sim.Git(repo.Root, "log", "-1", "--format=%B", c2);
        Assert.Equal("feat: two\n", body);
        var (_, reversed) = await sim.Git(repo.Root, "log", "--reverse", "--format=%s", "HEAD");
        Assert.Equal("feat: one\nfeat: two\n", reversed);
    }
}
