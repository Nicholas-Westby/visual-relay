using static VisualRelay.Tests.GitSimTestHelpers;

namespace VisualRelay.Tests;

/// <summary>
/// Always-on facts for the snapshot machinery (bundle/fetch/cherry-pick), stash,
/// the <c>GIT_INDEX_FILE</c> override, the seeding/inspection API, and the
/// unsupported-argv contract.
/// </summary>
public sealed class GitSimSnapshotTests
{
    [Fact]
    public async Task GitIndexFileOverride_StagesIntoSeparateIndexLeavingDefaultUntouched()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "a.txt", "1");
        sim.Commit(repo.Root, "seed");
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = Path.Combine(repo.Root, ".git", "tmp-index") };

        await sim.GitEnv(repo.Root, env, "read-tree", "HEAD");
        Write(repo, "c.txt", "x");
        await sim.GitEnv(repo.Root, env, "add", "-A");

        var (_, overrideList) = await sim.GitEnv(repo.Root, env, "ls-files");
        var (_, defaultList) = await sim.Git(repo.Root, "ls-files");
        Assert.Equal("a.txt\nc.txt\n", overrideList);
        Assert.Equal("a.txt\n", defaultList); // default index never saw c.txt
    }

    [Fact]
    public async Task BundleFetchCherryPick_RoundTripsSnapshotOntoWorkingTree()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "a.txt", "1");
        var c1 = sim.Commit(repo.Root, "feat: base");

        Write(repo, "b.txt", "2");
        await sim.Git(repo.Root, "add", "-A");
        var (_, tree) = await sim.Git(repo.Root, "write-tree");
        var (_, snap) = await sim.Git(repo.Root, "commit-tree", tree.Trim(), "-p", c1, "-m", "snapshot");
        var snapSha = snap.Trim();
        await sim.Git(repo.Root, "update-ref", "refs/relay-snapshot/t", snapSha);

        var bundlePath = Path.Combine(repo.Root, "flagged-work.bundle");
        await sim.Git(repo.Root, "bundle", "create", bundlePath, "refs/relay-snapshot/t", "^" + c1);
        var (verifyExit, _) = await sim.Git(repo.Root, "bundle", "verify", bundlePath);
        Assert.Equal(0, verifyExit);

        // Reset to base so the snapshot is not already present, then restore via fetch+cherry-pick.
        await sim.Git(repo.Root, "reset", "-q");
        File.Delete(Path.Combine(repo.Root, "b.txt"));
        await sim.Git(repo.Root, "fetch", bundlePath, "+refs/relay-snapshot/t:refs/relay-resume/t");
        Assert.True(sim.RefExists(repo.Root, "refs/relay-resume/t"));

        var (_, resume) = await sim.Git(repo.Root, "rev-parse", "refs/relay-resume/t");
        Assert.Equal(snapSha, resume.Trim());
        var (cpExit, _) = await sim.Git(repo.Root, "cherry-pick", "-n", resume.Trim());
        Assert.Equal(0, cpExit);
        Assert.Equal("2", File.ReadAllText(Path.Combine(repo.Root, "b.txt")));
        var (quitExit, _) = await sim.Git(repo.Root, "cherry-pick", "--quit");
        Assert.Equal(0, quitExit);
    }

    [Fact]
    public async Task StashPushListApply_CapturesThenRestoresWorkingChanges()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "a.txt", "1");
        sim.Commit(repo.Root, "seed");
        Write(repo, "a.txt", "2");
        Write(repo, "u.txt", "untracked");

        var (pushExit, _) = await sim.Git(repo.Root, "stash", "push", "-u", "-m", "vr-reset-abc");
        Assert.Equal(0, pushExit);
        Assert.Equal("1", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Root, "u.txt")));

        var (_, listOut) = await sim.Git(repo.Root, "stash", "list");
        Assert.Contains("vr-reset-abc", listOut);

        var (applyExit, _) = await sim.Git(repo.Root, "stash", "apply", "stash@{0}");
        Assert.Equal(0, applyExit);
        Assert.Equal("2", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.Equal("untracked", File.ReadAllText(Path.Combine(repo.Root, "u.txt")));
        var (dropExit, _) = await sim.Git(repo.Root, "stash", "drop", "stash@{0}");
        Assert.Equal(0, dropExit);
    }

    [Fact]
    public void Api_SeedCommitInspect_ExposesHeadInfoFilesAndStaged()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "a.txt", "1");
        var head1 = sim.Commit(repo.Root, "feat: one", author: ("Alice", "alice@example.test"));

        var info = sim.CommitInfo(repo.Root, head1);
        Assert.NotNull(info);
        Assert.Equal("feat: one", info!.Message);
        Assert.Equal("Alice", info.AuthorName);
        Assert.Equal("alice@example.test", info.AuthorEmail);
        Assert.Equal(head1, sim.Head(repo.Root));
        Assert.Equal(head1, sim.BranchTip(repo.Root, "main"));
        Assert.Contains("a.txt", sim.FilesInCommit(repo.Root, head1));

        sim.Seed(repo.Root, "b.txt", "2");
        var head2 = sim.Commit(repo.Root, "feat: two");
        Assert.Equal(new[] { head2 }, sim.CommitsBetween(repo.Root, head1, head2));
        Assert.Contains("a.txt", sim.StagedPaths(repo.Root));
        Assert.Contains("b.txt", sim.StagedPaths(repo.Root));
    }

    [Fact]
    public void Api_IsIgnored_HonorsRepoRootGitignore()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        Write(repo, ".gitignore", "*.log\nbuild/\n");
        Assert.True(sim.IsIgnored(repo.Root, "x.log"));
        Assert.True(sim.IsIgnored(repo.Root, "build/out.bin"));
        Assert.False(sim.IsIgnored(repo.Root, "src/app.cs"));
    }

    [Fact]
    public async Task UnsupportedArgv_ThrowsInvalidOperationNamingFullArgv()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sim.RunAsync(repo.Root, new[] { "frobnicate", "--wibble" }, CancellationToken.None));
        Assert.Contains("frobnicate --wibble", ex.Message);
    }
}
