using VisualRelay.Core.Execution;
using VisualRelay.GitSim;
using static VisualRelay.Tests.GitCommitterGitSimSetup;

namespace VisualRelay.Tests;

// The hook-rejection facts of the commit family, migrated onto GitSim's PreCommitHook.
// Each lives in its OWN class so the collections run in PARALLEL. Virtual time via
// ManualTimeProvider removes the wall-clock cost of retry backoff.

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
        var time = new ManualTimeProvider();
        var task = GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123", candidates, ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null, tasksDir: null,
            sim, CancellationToken.None, timeProvider: time);
        while (!task.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }
        var result = await task;

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
        var time = new ManualTimeProvider();
        var task = GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123", candidates, ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null, tasksDir: null,
            sim, CancellationToken.None, timeProvider: time);
        while (!task.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }
        var result = await task;

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("commit rejected", result.Error, StringComparison.Ordinal);
    }
}
