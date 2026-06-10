using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

// Snapshot-helper tests, split out of GitCommitterAutoIncludeTests.cs to keep
// each file under the 300-line guard. Shares the partial class's git helpers.
public sealed partial class GitCommitterAutoIncludeTests
{
    [Fact]
    public async Task CaptureUntrackedSnapshotAsync_ReturnsEmptySetWhenClean()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        var snapshot = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot);
    }

    [Fact]
    public async Task CaptureUntrackedSnapshotAsync_ExcludesGitignoredFiles()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");
        File.WriteAllText(Path.Combine(repo.Root, ".gitignore"), "*.log\n");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        // Create an untracked file that matches .gitignore.
        File.WriteAllText(Path.Combine(repo.Root, "debug.log"), "ignored");

        var snapshot = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.DoesNotContain("debug.log", snapshot);
    }

    [Fact]
    public async Task CaptureUntrackedSnapshotAsync_ReturnsUntrackedFilesOutsideGitignore()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        Directory.CreateDirectory(Path.Combine(repo.Root, "scratch"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        // Create an untracked scratch file.
        File.WriteAllText(Path.Combine(repo.Root, "scratch", "notes.txt"), "notes");

        var snapshot = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Contains("scratch/notes.txt", snapshot);
    }

    // ── FindUncommittedAuthoredFilesAsync ──────────────────────────────

    [Fact]
    public async Task FindUncommittedAuthoredFilesAsync_ReturnsEmptyWhenCommitIsComplete()
    {
        // After a successful commit that staged everything, the invariant
        // check must return an empty list — no authored file was left behind.
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        Directory.CreateDirectory(Path.Combine(repo.Root, "tests"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "old");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);
        Assert.Empty(preRunUntracked);

        // Author a new file and commit it (simulating a correct commit).
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");
        File.WriteAllText(Path.Combine(repo.Root, "tests", "new-test.cs"), "// new");

        var manifest = new[] { "src/app.cs" };
        var commit = await GitCommitter.CommitAsync(
            repo.Root, "task", "abc", ["feat: x"], manifest, [],
            commitToken: null, preRunUntracked, CancellationToken.None);
        Assert.True(commit.Success, commit.Error);

        // Post-commit: no authored file should remain untracked.
        var missed = await GitCommitter.FindUncommittedAuthoredFilesAsync(
            repo.Root, preRunUntracked, CancellationToken.None);
        Assert.Empty(missed);
    }

    [Fact]
    public async Task FindUncommittedAuthoredFilesAsync_ReturnsMissedAuthoredFiles()
    {
        // When a new file is authored but NOT committed (e.g. auto-include
        // gap), the invariant check must return it so the driver can flag.
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        Directory.CreateDirectory(Path.Combine(repo.Root, "tests"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "old");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);
        Assert.Empty(preRunUntracked);

        // Author a new file but do NOT commit it.
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");
        File.WriteAllText(Path.Combine(repo.Root, "tests", "new-test.cs"), "// new");

        // Commit only the manifest-listed file, skipping auto-include.
        var manifest = new[] { "src/app.cs" };
        var commit = await GitCommitter.CommitAsync(
            repo.Root, "task", "abc", ["feat: x"], manifest, [],
            commitToken: null, preRunUntracked: null, // null = no auto-include
            CancellationToken.None);
        Assert.True(commit.Success, commit.Error);

        // Post-commit: the authored test file is still untracked.
        var missed = await GitCommitter.FindUncommittedAuthoredFilesAsync(
            repo.Root, preRunUntracked, CancellationToken.None);
        Assert.Contains("tests/new-test.cs", missed);
    }

    [Fact]
    public async Task FindUncommittedAuthoredFilesAsync_ExcludesInternalArtifacts()
    {
        // Internal artifacts (.relay/, .swival/) must be ignored even when
        // they appear as new untracked files — same as the auto-include pass.
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "tests"));
        File.WriteAllText(Path.Combine(repo.Root, "app.py"), "old");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);
        Assert.Empty(preRunUntracked);

        // Create an internal artifact that the run produced.
        File.WriteAllText(Path.Combine(repo.Root, "app.py"), "updated");
        File.WriteAllText(Path.Combine(repo.Root, "tests", "new-test.py"), "# new");
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay", "task"));
        File.WriteAllText(Path.Combine(repo.Root, ".relay", "task", "report.json"), "{}");

        // Commit only the manifest-listed file (no auto-include).
        var manifest = new[] { "app.py" };
        var commit = await GitCommitter.CommitAsync(
            repo.Root, "task", "abc", ["feat: x"], manifest, [],
            commitToken: null, preRunUntracked: null,
            CancellationToken.None);
        Assert.True(commit.Success, commit.Error);

        var missed = await GitCommitter.FindUncommittedAuthoredFilesAsync(
            repo.Root, preRunUntracked, CancellationToken.None);
        // The new test file IS missed (authored but not committed).
        Assert.Contains("tests/new-test.py", missed);
        // The internal artifact is NOT reported as missed.
        Assert.DoesNotContain(".relay/task/report.json", missed);
    }
}
