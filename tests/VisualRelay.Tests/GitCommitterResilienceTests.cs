using System.Diagnostics;
using VisualRelay.Core.Execution;
using static VisualRelay.Tests.GitCommitterGitSimSetup;

namespace VisualRelay.Tests;

// The retry-after-transient facts of the commit family, migrated onto GitSim via a
// TransientGitShim that injects synthetic failures then delegates to GitSim. Each
// lives in its OWN class so the collections run in PARALLEL: the assertions exercise
// GitCommitter's real retry backoff (250ms + 1s between attempts), an unavoidable
// production delay GitSim cannot remove, so serializing them in one class would make
// the family slow. Separate collections keep the wall time near a single attempt's.

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
        shim.FailNext("rev-parse", failureCount: 2, exitCode: 128, stderr: "fatal: not a git repository");
        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123", ["feat: add widget"], ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null, tasksDir: null,
            CancellationToken.None, shim);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        Assert.False(string.IsNullOrWhiteSpace(result.CommitSha));
    }
}

/// <summary>Persistent rev-parse failure surfaces the git exit code and stderr.</summary>
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
        shim.FailNext("rev-parse", failureCount: 99, exitCode: 128, stderr: "fatal: not a git repository");
        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123", ["feat: add widget"], ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null, tasksDir: null,
            CancellationToken.None, shim);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("git exit 128", result.Error, StringComparison.Ordinal);
        Assert.Contains("fatal: not a git repository", result.Error, StringComparison.Ordinal);
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
        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123", ["feat: add widget"], ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null, tasksDir: null,
            CancellationToken.None, shim);

        Assert.True(result.Success, $"Expected success after transient add failure, got: {result.Error}");
    }
}

/// <summary>A persistent failure still completes within the bounded retry window.</summary>
public sealed class GitCommitterPersistentTimingTests
{
    [Fact]
    public async Task CommitAsync_PersistentFailure_CompletesWithinReasonableTime()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Commit(repo.Root, "chore: seed");

        var shim = new TransientGitShim(sim);
        shim.FailNext("rev-parse", failureCount: 99, exitCode: 128, stderr: "fatal: not a git repository");
        var sw = Stopwatch.StartNew();
        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123", ["feat: test"], [], [],
            commitToken: null, preRunUntracked: null, tasksDir: null,
            CancellationToken.None, shim);
        sw.Stop();

        Assert.False(result.Success);
        Assert.True(sw.Elapsed.TotalSeconds < 10,
            $"Persistent failure took {sw.Elapsed.TotalSeconds:F1}s, expected < 10s");
    }
}
