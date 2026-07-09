using VisualRelay.Core.Execution;
using VisualRelay.GitSim;
using static VisualRelay.Tests.GitCommitterGitSimSetup;

namespace VisualRelay.Tests;

// The hook-rejection facts of the commit family, migrated onto GitSim's PreCommitHook.
// Each lives in its OWN class so the collections run in PARALLEL: a rejected candidate
// makes GitCommitter retry the commit through its real backoff (250ms + 1s per
// attempt), an unavoidable production delay GitSim cannot remove, so serializing these
// with the fast facts would slow the measured class.

/// <summary>First candidate rejected by the hook, second accepted.</summary>
public sealed class GitCommitterHookRejectsFirstTests
{
    [Fact]
    public async Task CommitAsync_FirstCandidateRejectedByCommitMsgHook_UsesSecond()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Commit(repo.Root, "chore: seed");
        Write(repo, "src/app.cs", "updated");

        // Reject subjects containing a file-name pattern (mirrors the former
        // commit-msg hook rejecting the "\.cs" regex), now via PreCommitHook.
        sim.PreCommitHook = req =>
            req.Message.Split('\n')[0].Contains(".cs", StringComparison.Ordinal)
                ? GitSimHookVerdict.Reject("hook: subject matches rejected pattern")
                : GitSimHookVerdict.Accept;

        var candidates = new[] { "fix(src): update app.cs logic", "fix: correct update logic" };
        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123", candidates, ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null, tasksDir: null,
            CancellationToken.None, sim);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        Assert.Equal("fix: correct update logic", Subject(sim, repo, result.CommitSha!));
    }
}

/// <summary>Every candidate rejected by the hook yields a commit-rejected failure.</summary>
public sealed class GitCommitterHookRejectsAllTests
{
    [Fact]
    public async Task CommitAsync_AllCandidatesRejected_ReturnsFailure()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Commit(repo.Root, "chore: seed");
        Write(repo, "src/app.cs", "updated");

        sim.PreCommitHook = _ => GitSimHookVerdict.Reject("hook: all commits rejected");

        var candidates = new[] { "feat: first", "fix: second" };
        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123", candidates, ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null, tasksDir: null,
            CancellationToken.None, sim);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("commit rejected", result.Error, StringComparison.Ordinal);
    }
}
