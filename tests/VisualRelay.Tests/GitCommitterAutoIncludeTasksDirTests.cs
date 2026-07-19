using VisualRelay.Core.Execution;
using static VisualRelay.Tests.GitCommitterGitSimSetup;

namespace VisualRelay.Tests;

public sealed class GitCommitterAutoIncludeTasksDirTests
{
    // ── tasks-dir exclusion ───────────────────────────────────────────

    [Fact]
    public async Task CommitAsync_ExcludesTasksDirFileFromAutoInclude_WhenCreatedMidRun()
    {
        // Regression: a file dropped under the tasks dir after the run started
        // (e.g. llm-tasks/<x>/<x>.md) is untracked, not in preRunUntracked, and
        // not an internal artifact — so without a tasks-dir guard it passes the
        // auto-include filter and contaminates the running task's commit.
        //
        // This test validates:
        //   (a) the tasks-dir file is excluded from the commit,
        //   (b) a genuinely authored file outside the tasks dir IS auto-included,
        //   (c) FindUncommittedAuthoredFilesAsync does NOT false-flag the tasks-dir
        //       file as a missed authored file.
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "old");
        sim.Commit(repo.Root, "chore: seed");

        // Snapshot before the run: no untracked files.
        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, sim, CancellationToken.None);
        Assert.Empty(preRunUntracked);

        // Simulate: agent authors a new src file and modifies a tracked file,
        // AND the user drops a new task file under the tasks dir mid-run.
        Write(repo, "src/app.cs", "updated");
        Write(repo, "src/new-impl.cs", "// genuinely authored");
        Write(repo, "llm-tasks/new-task/new-task.md", "# user-dropped mid-run task");

        var manifest = new[] { "src/app.cs" };

        // (a) & (b): commit with tasksDir guard active.
        var commit = await GitCommitter.CommitAsync(
            repo.Root, "task", "abc", ["feat: x"], manifest, [],
            commitToken: null, preRunUntracked,
            tasksDir: "llm-tasks",
            sim, CancellationToken.None, timeProvider: TimeProvider.System);
        Assert.True(commit.Success, commit.Error);

        var committed = sim.FilesInCommit(repo.Root, sim.Head(repo.Root)!);
        // (b) genuinely authored file outside tasks dir IS auto-included.
        Assert.Contains("src/new-impl.cs", committed);
        // (a) tasks-dir file dropped mid-run is NOT in the commit.
        Assert.DoesNotContain("llm-tasks/new-task/new-task.md", committed);

        // (c) FindUncommittedAuthoredFilesAsync must NOT report the tasks-dir
        //     file as a missed authored file (no false flag).
        var missed = await GitCommitter.FindUncommittedAuthoredFilesAsync(
            repo.Root, preRunUntracked,
            tasksDir: "llm-tasks",
            sim, CancellationToken.None);
        Assert.DoesNotContain("llm-tasks/new-task/new-task.md", missed);
    }
}
