using VisualRelay.Core.Execution;
using VisualRelay.GitSim;
using static VisualRelay.Tests.GitCommitterGitSimSetup;

namespace VisualRelay.Tests;

/// <summary>
/// Verifies that <c>git commit</c> exit 1 (hook rejection) is treated as a
/// final per-candidate verdict — no retry backoff — so the candidate loop
/// moves to the next message immediately. Virtual time via
/// <see cref="ManualTimeProvider"/> proves zero delay calls.
/// </summary>
public sealed class GitCommitterCommitExit1FailFastTests
{
    /// <summary>
    /// Two candidates, both hook-rejected (exit 1). The second candidate is
    /// attempted with zero delay calls: virtual time never advances because
    /// exit 1 is a final answer, not a transient failure.
    /// </summary>
    [Fact]
    public async Task CommitAsync_CommitExit1_MovesToNextCandidateWithZeroDelay()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Commit(repo.Root, "chore: seed");
        Write(repo, "src/app.cs", "updated");

        // Hook rejects every commit message.
        sim.PreCommitHook = _ => GitSimHookVerdict.Reject("hook: all commits rejected");

        var candidates = new[] { "feat: first candidate", "fix: second candidate" };
        var time = new ManualTimeProvider();

        // Use the advance-pump loop so the test completes even before the fix
        // (exit 1 currently retries, which awaits Task.Delay on virtual time).
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

        // Both candidates rejected → overall failure.
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("commit rejected", result.Error, StringComparison.Ordinal);

        // Zero delay: virtual time never advanced. Exit 1 was treated as a
        // final per-candidate verdict — no retry, no backoff, no timer fired.
        Assert.Equal(new DateTimeOffset(0, TimeSpan.Zero), time.GetUtcNow());
    }
}
