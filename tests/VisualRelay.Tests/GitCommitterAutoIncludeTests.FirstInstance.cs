using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

// First-instance snapshot tests, split out of GitCommitterAutoIncludeTests.cs to
// keep each file under the 300-line guard. Uses helpers from the main partial class.
public sealed partial class GitCommitterAutoIncludeTests
{
    // ── resume: first-instance snapshot tests ───────────────────────

    [Fact]
    public async Task CommitAsync_UsesFirstInstanceSnapshot_IncludesPriorInstanceFiles()
    {
        // Simulates the fix for the resume-commit-omits-prior-authored-files
        // bug: the sealed commit uses the FIRST instance's preRunUntracked
        // snapshot (persisted to .relay/<taskId>/), NOT the resumed instance's
        // re-snapshot. Files authored by the interrupted instance are absent
        // from the first snapshot → auto-included. With the old behaviour
        // (re-snapshot on resume) those files would be classified as
        // pre-existing and silently dropped.
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        Directory.CreateDirectory(Path.Combine(repo.Root, "tests"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "old");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        // S1 — snapshot at FIRST instance start (no untracked files).
        var firstInstanceSnapshot = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);
        Assert.Empty(firstInstanceSnapshot);

        // Interrupted instance authors new files (stages 5–10).
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");
        File.WriteAllText(Path.Combine(repo.Root, "tests", "new-test.cs"), "// new test");

        // S2 — what a resumed instance would capture (includes authored files).
        var resumeSnapshot = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);
        Assert.Contains("tests/new-test.cs", resumeSnapshot);

        // Commit with S1 (the persisted first-instance snapshot).
        var manifest = new[] { "src/app.cs" };
        var result = await GitCommitter.CommitAsync(
            repo.Root,
            "my-task",
            "abc123",
            ["feat: add widget"],
            manifest,
            [],
            commitToken: null,
            firstInstanceSnapshot,
            tasksDir: null,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var committed = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");
        Assert.Contains("src/app.cs", committed);
        // KEY: the interrupted-instance file IS committed when using S1.
        Assert.Contains("tests/new-test.cs", committed);
    }

    [Fact]
    public async Task CommitAsync_ExcludesPreExistingOperatorFile_WithFirstInstanceSnapshot()
    {
        // Operator scratch file that existed before the FIRST instance must
        // remain excluded across resume, even when the first-instance snapshot
        // is persisted and reused. Only files authored by the run — absent
        // from the first snapshot — are auto-included.
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        Directory.CreateDirectory(Path.Combine(repo.Root, "tests"));
        Directory.CreateDirectory(Path.Combine(repo.Root, "scratch"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "old");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        // Operator scratch file created before the first instance starts.
        File.WriteAllText(Path.Combine(repo.Root, "scratch", "notes.txt"), "scratch");

        // S1 — first-instance snapshot captures the operator scratch file.
        var firstInstanceSnapshot = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);
        Assert.Contains("scratch/notes.txt", firstInstanceSnapshot);

        // Interrupted instance authors new files.
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");
        File.WriteAllText(Path.Combine(repo.Root, "tests", "new-test.cs"), "// new test");

        // Commit with S1.
        var manifest = new[] { "src/app.cs" };
        var result = await GitCommitter.CommitAsync(
            repo.Root,
            "my-task",
            "abc123",
            ["feat: add widget"],
            manifest,
            [],
            commitToken: null,
            firstInstanceSnapshot,
            tasksDir: null,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var committed = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");
        // Newly authored file IS included (absent from first snapshot).
        Assert.Contains("tests/new-test.cs", committed);
        // Operator scratch file IS excluded (present in first snapshot).
        Assert.DoesNotContain("scratch/notes.txt", committed);
    }
}
