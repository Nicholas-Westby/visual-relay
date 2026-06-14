using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

// Tasks-dir exclusion tests, split out of GitCommitterAutoIncludeTests.cs to
// keep each file under the 300-line guard. Shares the partial class's git helpers.
public sealed partial class GitCommitterAutoIncludeTests
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
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "old");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        // Snapshot before the run: no untracked files.
        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);
        Assert.Empty(preRunUntracked);

        // Simulate: agent authors a new src file and modifies a tracked file,
        // AND the user drops a new task file under the tasks dir mid-run.
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");
        File.WriteAllText(Path.Combine(repo.Root, "src", "new-impl.cs"), "// genuinely authored");
        Directory.CreateDirectory(Path.Combine(repo.Root, "llm-tasks", "new-task"));
        File.WriteAllText(
            Path.Combine(repo.Root, "llm-tasks", "new-task", "new-task.md"),
            "# user-dropped mid-run task");

        var manifest = new[] { "src/app.cs" };

        // (a) & (b): commit with tasksDir guard active.
        var commit = await GitCommitter.CommitAsync(
            repo.Root, "task", "abc", ["feat: x"], manifest, [],
            commitToken: null, preRunUntracked,
            tasksDir: "llm-tasks",
            CancellationToken.None);
        Assert.True(commit.Success, commit.Error);

        var committed = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");
        // (b) genuinely authored file outside tasks dir IS auto-included.
        Assert.Contains("src/new-impl.cs", committed);
        // (a) tasks-dir file dropped mid-run is NOT in the commit.
        Assert.DoesNotContain("llm-tasks/new-task/new-task.md", committed);

        // (c) FindUncommittedAuthoredFilesAsync must NOT report the tasks-dir
        //     file as a missed authored file (no false flag).
        var missed = await GitCommitter.FindUncommittedAuthoredFilesAsync(
            repo.Root, preRunUntracked,
            tasksDir: "llm-tasks",
            CancellationToken.None);
        Assert.DoesNotContain("llm-tasks/new-task/new-task.md", missed);
    }
}
