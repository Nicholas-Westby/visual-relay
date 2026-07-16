using VisualRelay.Core.Execution;
using static VisualRelay.Tests.GitCommitterGitSimSetup;

namespace VisualRelay.Tests;

public sealed class GitCommitterAutoIncludeSnapshotTests
{
    [Fact]
    public async Task CaptureUntrackedSnapshotAsync_ReturnsEmptySetWhenClean()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Commit(repo.Root, "chore: seed");

        var snapshot = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None, sim);

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot);
    }

    [Fact]
    public async Task CaptureUntrackedSnapshotAsync_ExcludesGitignoredFiles()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Seed(repo.Root, ".gitignore", "*.log\n");
        sim.Commit(repo.Root, "chore: seed");

        // Create an untracked file that matches .gitignore.
        Write(repo, "debug.log", "ignored");

        var snapshot = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None, sim);

        Assert.NotNull(snapshot);
        Assert.DoesNotContain("debug.log", snapshot);
    }

    [Fact]
    public async Task CaptureUntrackedSnapshotAsync_ReturnsUntrackedFilesOutsideGitignore()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Commit(repo.Root, "chore: seed");

        // Create an untracked scratch file.
        Write(repo, "scratch/notes.txt", "notes");

        var snapshot = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None, sim);

        Assert.NotNull(snapshot);
        Assert.Contains("scratch/notes.txt", snapshot);
    }

    // ── FindUncommittedAuthoredFilesAsync ──────────────────────────────

    [Fact]
    public async Task FindUncommittedAuthoredFilesAsync_ReturnsEmptyWhenCommitIsComplete()
    {
        // After a successful commit that staged everything, the invariant
        // check must return an empty list — no authored file was left behind.
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "old");
        sim.Commit(repo.Root, "chore: seed");

        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None, sim);
        Assert.Empty(preRunUntracked);

        // Author a new file and commit it (simulating a correct commit).
        Write(repo, "src/app.cs", "updated");
        Write(repo, "tests/new-test.cs", "// new");

        var manifest = new[] { "src/app.cs" };
        var commit = await GitCommitter.CommitAsync(
            repo.Root, "task", "abc", ["feat: x"], manifest, [],
            commitToken: null, preRunUntracked,
            tasksDir: null,
            CancellationToken.None, sim);
        Assert.True(commit.Success, commit.Error);

        // Post-commit: no authored file should remain untracked.
        var missed = await GitCommitter.FindUncommittedAuthoredFilesAsync(
            repo.Root, preRunUntracked,
            tasksDir: null,
            CancellationToken.None, sim);
        Assert.Empty(missed);
    }

    [Fact]
    public async Task FindUncommittedAuthoredFilesAsync_ReturnsMissedAuthoredFiles()
    {
        // When a new file is authored but NOT committed (e.g. auto-include
        // gap), the invariant check must return it so the driver can flag.
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "old");
        sim.Commit(repo.Root, "chore: seed");

        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None, sim);
        Assert.Empty(preRunUntracked);

        // Author a new file but do NOT commit it.
        Write(repo, "src/app.cs", "updated");
        Write(repo, "tests/new-test.cs", "// new");

        // Commit only the manifest-listed file, skipping auto-include.
        var manifest = new[] { "src/app.cs" };
        var commit = await GitCommitter.CommitAsync(
            repo.Root, "task", "abc", ["feat: x"], manifest, [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            CancellationToken.None, sim);
        Assert.True(commit.Success, commit.Error);

        // Post-commit: the authored test file is still untracked.
        var missed = await GitCommitter.FindUncommittedAuthoredFilesAsync(
            repo.Root, preRunUntracked,
            tasksDir: null,
            CancellationToken.None, sim);
        Assert.Contains("tests/new-test.cs", missed);
    }

    [Fact]
    public async Task FindUncommittedAuthoredFilesAsync_ExcludesInternalArtifacts()
    {
        // Internal artifacts (.relay/, .swival/) must be ignored even when
        // they appear as new untracked files — same as the auto-include pass.
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "app.py", "old");
        sim.Commit(repo.Root, "chore: seed");

        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None, sim);
        Assert.Empty(preRunUntracked);

        // Create an internal artifact that the run produced.
        Write(repo, "app.py", "updated");
        Write(repo, "tests/new-test.py", "# new");
        Write(repo, ".relay/task/report.json", "{}");

        // Commit only the manifest-listed file (no auto-include).
        var manifest = new[] { "app.py" };
        var commit = await GitCommitter.CommitAsync(
            repo.Root, "task", "abc", ["feat: x"], manifest, [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            CancellationToken.None, sim);
        Assert.True(commit.Success, commit.Error);

        var missed = await GitCommitter.FindUncommittedAuthoredFilesAsync(
            repo.Root, preRunUntracked,
            tasksDir: null,
            CancellationToken.None, sim);
        // The new test file IS missed (authored but not committed).
        Assert.Contains("tests/new-test.py", missed);
        // The internal artifact is NOT reported as missed.
        Assert.DoesNotContain(".relay/task/report.json", missed);
    }

    // ── Round-trip stability ────────────────────────────────────────

    [Fact]
    public async Task CaptureUntrackedSnapshotAsync_RoundTripIsStable()
    {
        // Snapshot → write to file → read back → capture fresh → set
        // difference must be empty when nothing changed on disk.
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "content");
        // Add .gitignore so our snapshot temp file doesn't pollute the second capture.
        sim.Seed(repo.Root, ".gitignore", ".relay/\n");
        sim.Commit(repo.Root, "chore: seed");

        // Create a mix of untracked files.
        Write(repo, "scratch/notes.txt", "notes");
        Write(repo, "logs/debug.log", "debug");

        var first = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None, sim);
        Assert.NotEmpty(first);

        // Write snapshot to file (mimics pre-run-untracked.txt persistence).
        var snapshotPath = Path.Combine(repo.Root, "pre-run-untracked.txt");
        await File.WriteAllTextAsync(snapshotPath,
            string.Join(Environment.NewLine, first.OrderBy(p => p, StringComparer.Ordinal)));

        // Read back.
        var readBack = new HashSet<string>(
            (await File.ReadAllLinesAsync(snapshotPath))
                .Select(l => l.Trim())
                .Where(l => l.Length > 0),
            StringComparer.Ordinal);

        // Clean up the snapshot file so it doesn't pollute the second capture.
        File.Delete(snapshotPath);

        // Capture again — nothing changed on disk.
        var second = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None, sim);

        // The two snapshots must be identical.
        Assert.Equal(first.Count, second.Count);
        Assert.Empty(first.Except(second));
        Assert.Empty(second.Except(first));

        // The persisted snapshot must match.
        Assert.Equal(first.Count, readBack.Count);
        Assert.Empty(first.Except(readBack));
        Assert.Empty(readBack.Except(first));
    }
}
