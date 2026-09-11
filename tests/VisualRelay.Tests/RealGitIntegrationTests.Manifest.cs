using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Companion to <see cref="RealGitIntegrationTests"/> — the staging shapes that only
/// the real git binary can falsify. GitSim stages by pathspec match and never refuses
/// an ignored path, so the exit-1 the target repositories hit is invisible in-memory.
/// </summary>
public sealed partial class RealGitIntegrationTests
{
    /// <summary>
    /// Regression: with <c>.relay/</c> ignored and a manifest spanning two top-level
    /// directories, <c>add -A -- &lt;files&gt; :(exclude).relay</c> exits 1 ("The
    /// following paths are ignored by one of your .gitignore files: .relay") and the
    /// whole commit stage fails. The manifest add must carry no exclude term.
    /// </summary>
    [Fact]
    public async Task GitCommitter_RealGit_CommitsAManifestSpanningTwoDirectoriesWhenRelayIsIgnored()
    {
        if (!Ready()) return;
        using var repo = TestRepository.Create();
        SeedRepo(repo.Root);
        // The evaluation recipe keeps .relay out of git privately, and the target's
        // sources sit at the repository root: the exact shape that failed six times.
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".git", "info", "exclude"), ".relay/\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "route.go"), "package mux\n");
        Git(repo.Root, "add", "route.go");
        Git(repo.Root, "commit", "-q", "-m", "chore: add the route");

        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay", "my-task"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "my-task", "run.log"), "log\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "route.go"), "package mux // fixed\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "src", "app.cs"), "implemented");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123", ["feat: add widget"], ["route.go", "src/app.cs"], [],
            commitToken: null, preRunUntracked: null, tasksDir: null,
            new GitInvoker(), CancellationToken.None, timeProvider: TimeProvider.System);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        var committed = Git(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");
        Assert.Contains("route.go", committed, StringComparison.Ordinal);
        Assert.Contains("src/app.cs", committed, StringComparison.Ordinal);
        Assert.DoesNotContain(".relay", committed, StringComparison.Ordinal);
    }
}
