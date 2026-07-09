namespace VisualRelay.Tests;

/// <summary>
/// Differential parity for the plumbing / history / worktree groups (opt-in via
/// <c>VR_RUN_SLOW_INTEGRATION=1</c>). Commands whose output is a sha are compared by
/// shape and reachability rather than value, since GitSim shas are deliberately not
/// byte-identical to git's.
/// </summary>
public sealed class GitSimParityPlumbingTests
{
    private static bool Ready()
    {
        SlowIntegration.SkipIfNotOptedIn();
        if (!SlowIntegration.ToolAvailable("git"))
        {
            Assert.Skip("git binary not on PATH.");
            return false;
        }

        return true;
    }

    [Fact]
    public void WriteTreeAndCommitTree_Parity()
    {
        if (!Ready()) return;
        using var h = new ParityHarness();
        h.SeedCommit("a.txt", "1", "seed");
        h.AssertShaShapeParity("write-tree");

        // commit-tree needs each repo's own tree sha (they differ), so drive separately.
        var realTree = h.RealGit("write-tree").Stdout.Trim();
        var simTree = h.SimGit("write-tree").Output.Trim();
        var real = h.RealGit("commit-tree", realTree, "-m", "snapshot");
        var sim = h.SimGit("commit-tree", simTree, "-m", "snapshot");
        Assert.Equal(real.Exit, sim.Exit);
        Assert.Equal(40, real.Stdout.Trim().Length);
        Assert.Equal(40, sim.Output.Trim().Length);
    }

    [Fact]
    public void ResetAndCheckout_Parity()
    {
        if (!Ready()) return;
        using var h = new ParityHarness();
        h.SeedCommit("a.txt", "orig", "seed");
        h.WriteBoth("a.txt", "changed");
        h.AssertExitParity("checkout", "HEAD", "--", "a.txt");
        Assert.Equal("orig", File.ReadAllText(Path.Combine(h.RealRoot, "a.txt")));
        Assert.Equal("orig", File.ReadAllText(Path.Combine(h.SimRoot, "a.txt")));
        h.AssertExitParity("reset", "-q");
    }

    [Fact]
    public void MergeBaseIsAncestor_Parity()
    {
        if (!Ready()) return;
        using var h = new ParityHarness();
        h.SeedCommit("a.txt", "1", "one");
        h.SeedCommit("b.txt", "2", "two");
        h.AssertExitParity("merge-base", "--is-ancestor", "HEAD~1", "HEAD"); // ancestor → 0 both
        h.AssertExitParity("merge-base", "--is-ancestor", "HEAD", "HEAD~1"); // not → 1 both
    }

    [Fact]
    public void RevList_Parity()
    {
        if (!Ready()) return;
        using var h = new ParityHarness();
        h.SeedCommit("a.txt", "1", "one");
        h.SeedCommit("b.txt", "2", "two");
        var real = h.RealGit("rev-list", "HEAD~1..HEAD");
        var sim = h.SimGit("rev-list", "HEAD~1..HEAD");
        Assert.Equal(real.Exit, sim.Exit);
        Assert.Equal(
            real.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length,
            sim.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void LogAndSymbolicRef_Parity()
    {
        if (!Ready()) return;
        using var h = new ParityHarness();
        h.SeedCommit("a.txt", "1", "feat: subject line");
        h.AssertExactParity("log", "-1", "--format=%s");
        h.AssertExactParity("symbolic-ref", "--short", "--quiet", "HEAD");
    }

    [Fact]
    public void DiffTree_Parity()
    {
        if (!Ready()) return;
        using var h = new ParityHarness();
        h.SeedCommit("a.txt", "1", "one");
        h.SeedCommit("nested/b.txt", "2", "two");
        h.AssertPathSetParity('\n', "diff-tree", "--no-commit-id", "--name-only", "-r", "HEAD");
    }

    [Fact]
    public void WorktreeAdd_Parity()
    {
        if (!Ready()) return;
        using var h = new ParityHarness();
        h.SeedCommit("a.txt", "content", "seed");
        var realWt = Path.Combine(Path.GetTempPath(), "gitsim-parity", Guid.NewGuid().ToString("N"));
        var simWt = Path.Combine(Path.GetTempPath(), "gitsim-parity", Guid.NewGuid().ToString("N"));
        try
        {
            var real = h.RealGit("worktree", "add", "--detach", "--quiet", realWt, "HEAD");
            var sim = h.SimGit("worktree", "add", "--detach", "--quiet", simWt, "HEAD");
            Assert.Equal(real.Exit, sim.Exit);
            Assert.Equal("content", File.ReadAllText(Path.Combine(realWt, "a.txt")));
            Assert.Equal("content", File.ReadAllText(Path.Combine(simWt, "a.txt")));
        }
        finally
        {
            h.RealGit("worktree", "remove", "--force", realWt);
            TestFileSystem.DeleteDirectoryResilient(realWt);
            TestFileSystem.DeleteDirectoryResilient(simWt);
        }
    }
}
