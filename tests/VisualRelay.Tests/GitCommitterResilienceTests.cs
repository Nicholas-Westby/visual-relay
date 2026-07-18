using VisualRelay.Core.Execution;
using static VisualRelay.Tests.GitCommitterGitSimSetup;

namespace VisualRelay.Tests;

// The retry-after-transient facts of the commit family, migrated onto GitSim via a
// TransientGitShim that injects synthetic failures then delegates to GitSim. Each
// lives in its OWN class so the collections run in PARALLEL. Virtual time via
// ManualTimeProvider removes the wall-clock cost of retry backoff.

/// <summary>Two transient rev-parse failures, then a GitSim-backed success.</summary>
public sealed class GitCommitterProbeRetryTests
{
    [Fact]
    public async Task CommitAsync_ProbeFailsTwiceThenSucceeds_CommitsSuccessfully()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Commit(repo.Root, "chore: seed");
        Write(repo, "src/app.cs", "updated");

        var shim = new TransientGitShim(sim);
        // Use a TRANSIENT message (index.lock) — deterministic signatures fail fast.
        shim.FailNext("rev-parse", failureCount: 2, exitCode: 128, stderr: "fatal: Unable to create '.git/index.lock': File exists.");
        var time = new ManualTimeProvider();
        var task = GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123", ["feat: add widget"], ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null, tasksDir: null,
            CancellationToken.None, shim, timeProvider: time);
        while (!task.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }
        var result = await task;

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        Assert.False(string.IsNullOrWhiteSpace(result.CommitSha));
    }
}

/// <summary>Persistent rev-parse failure: deterministic → fail-fast with exactly one invocation.</summary>
public sealed class GitCommitterProbePersistentTests
{
    [Fact]
    public async Task CommitAsync_ProbeFailsPersistently_ReturnsFailureWithDiagnostics()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Commit(repo.Root, "chore: seed");
        Write(repo, "src/app.cs", "updated");

        var shim = new TransientGitShim(sim);
        // "not a git repository" is a deterministic signature → fail-fast, no sleep.
        shim.FailNext("rev-parse", failureCount: 99, exitCode: 128, stderr: "fatal: not a git repository");
        var time = new ManualTimeProvider();
        var task = GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123", ["feat: add widget"], ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null, tasksDir: null,
            CancellationToken.None, shim, timeProvider: time);
        // Deterministic failures return immediately — no time advance needed.
        Assert.True(task.IsCompleted, "deterministic failure should complete synchronously (no delay)");
        var result = await task;

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("git exit 128", result.Error, StringComparison.Ordinal);
        Assert.Contains("fatal: not a git repository", result.Error, StringComparison.Ordinal);
        // Exactly one invocation — no retries for a deterministic failure.
        Assert.Equal(1, shim.Consumed("rev-parse"));
    }
}

/// <summary>One transient add failure, then a GitSim-backed success.</summary>
public sealed class GitCommitterAddRetryTests
{
    [Fact]
    public async Task CommitAsync_AddFailsTransientlyThenSucceeds_CommitsSuccessfully()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Commit(repo.Root, "chore: seed");
        Write(repo, "src/app.cs", "updated");

        var shim = new TransientGitShim(sim);
        shim.FailNext("add", failureCount: 1, exitCode: 128, stderr: "fatal: index file open failed");
        var time = new ManualTimeProvider();
        var task = GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123", ["feat: add widget"], ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null, tasksDir: null,
            CancellationToken.None, shim, timeProvider: time);
        while (!task.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }
        var result = await task;

        Assert.True(result.Success, $"Expected success after transient add failure, got: {result.Error}");
    }
}

/// <summary>A persistent failure verifies the retry loop runs all 3 attempts.</summary>
public sealed class GitCommitterPersistentTimingTests
{
    [Fact]
    public async Task CommitAsync_PersistentFailure_ExhaustsAllRetryAttempts()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Commit(repo.Root, "chore: seed");

        var shim = new TransientGitShim(sim);
        shim.FailNext("rev-parse", failureCount: 99, exitCode: 128, stderr: "fatal: index file open failed");
        var time = new ManualTimeProvider();
        var task = GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123", ["feat: test"], [], [],
            commitToken: null, preRunUntracked: null, tasksDir: null,
            CancellationToken.None, shim, timeProvider: time);
        while (!task.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }
        var result = await task;

        Assert.False(result.Success);
        // The retry loop exhausted all 3 attempts: 2 backoff delays (250ms + 1s)
        // consumed via virtual time. The shim should have served 3 failures.
        Assert.Equal(3, shim.Consumed("rev-parse"));
        // The Advance loop confirms it completes without wall-clock delay.
    }
}
