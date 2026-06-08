using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class GitCommitterAutoIncludeTests
{
    [Fact]
    public async Task CommitAsync_AutoIncludesNewUntrackedFileUnderTests()
    {
        // A new test file authored during stage 5 that the stage-4 manifest never
        // listed must not be silently dropped. The auto-include pass stages any
        // untracked file under a source root that appeared after the run started.
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        Directory.CreateDirectory(Path.Combine(repo.Root, "tests"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "old");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        // Snapshot before the run: no untracked files exist.
        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);
        Assert.Empty(preRunUntracked);

        // Simulate an agent authoring a new test file and modifying a manifest-listed file.
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");
        File.WriteAllText(Path.Combine(repo.Root, "tests", "new-test.cs"), "// new test");

        // Manifest only lists src/app.cs — the new test is absent.
        var manifest = new[] { "src/app.cs" };

        var result = await GitCommitter.CommitAsync(
            repo.Root,
            "my-task",
            "abc123",
            ["feat: add widget"],
            manifest,
            [],
            commitToken: null,
            preRunUntracked,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var committed = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");
        Assert.Contains("src/app.cs", committed);
        Assert.Contains("tests/new-test.cs", committed);
    }

    [Fact]
    public async Task CommitAsync_ExcludesPreExistingUntrackedFile()
    {
        // Pre-existing untracked files (present before the run started and captured
        // in the snapshot) must NOT be auto-included. Only files authored during the
        // run (delta: current \ snapshot) under source roots are staged.
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        Directory.CreateDirectory(Path.Combine(repo.Root, "tests"));
        Directory.CreateDirectory(Path.Combine(repo.Root, "scratch"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "old");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        // Pre-existing untracked scratch that must NOT be committed — created
        // after the seed so it is untracked when the snapshot is taken.
        File.WriteAllText(Path.Combine(repo.Root, "scratch", "notes.txt"), "scratch");

        // Snapshot captures the pre-existing scratch file.
        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);
        Assert.Contains("scratch/notes.txt", preRunUntracked);

        // Agent modifies a tracked file and creates a new test file under tests/.
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");
        File.WriteAllText(Path.Combine(repo.Root, "tests", "new-test.cs"), "// new test");

        var manifest = new[] { "src/app.cs" };

        var result = await GitCommitter.CommitAsync(
            repo.Root,
            "my-task",
            "abc123",
            ["feat: add widget"],
            manifest,
            [],
            commitToken: null,
            preRunUntracked,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var committed = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");
        Assert.Contains("tests/new-test.cs", committed);
        Assert.DoesNotContain("scratch/notes.txt", committed);
    }

    [Fact]
    public async Task CommitAsync_ExcludesNewFileOutsideSourceRoots()
    {
        // Only files under src/, tests/, or tools/ are auto-included. A new file
        // under docs/ or any other non-source directory must NOT be staged.
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        Directory.CreateDirectory(Path.Combine(repo.Root, "tests"));
        Directory.CreateDirectory(Path.Combine(repo.Root, "docs"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "old");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);
        Assert.Empty(preRunUntracked);

        // Agent creates a doc (outside source roots) and a test (inside source root).
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");
        File.WriteAllText(Path.Combine(repo.Root, "docs", "guide.md"), "# Guide");
        File.WriteAllText(Path.Combine(repo.Root, "tests", "new-test.cs"), "// new test");

        var manifest = new[] { "src/app.cs" };

        var result = await GitCommitter.CommitAsync(
            repo.Root,
            "my-task",
            "abc123",
            ["feat: add widget"],
            manifest,
            [],
            commitToken: null,
            preRunUntracked,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var committed = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");
        Assert.Contains("tests/new-test.cs", committed);
        Assert.DoesNotContain("docs/guide.md", committed);
    }

    [Fact]
    public async Task CommitAsync_ExcludesGitignoredNewFile()
    {
        // Gitignored paths must stay excluded unless force-added as proof files.
        // The auto-include pass must respect .gitignore (--exclude-standard covers it).
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        Directory.CreateDirectory(Path.Combine(repo.Root, "tests"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "old");
        File.WriteAllText(Path.Combine(repo.Root, ".gitignore"), "*.log\n");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);
        Assert.Empty(preRunUntracked);

        // Agent creates a gitignored log file and a new test file.
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");
        File.WriteAllText(Path.Combine(repo.Root, "debug.log"), "log content");
        File.WriteAllText(Path.Combine(repo.Root, "tests", "new-test.cs"), "// new test");

        var manifest = new[] { "src/app.cs" };

        var result = await GitCommitter.CommitAsync(
            repo.Root,
            "my-task",
            "abc123",
            ["feat: add widget"],
            manifest,
            [],
            commitToken: null,
            preRunUntracked,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var committed = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");
        Assert.Contains("tests/new-test.cs", committed);
        Assert.DoesNotContain("debug.log", committed);
    }

    [Fact]
    public async Task CommitAsync_NullPreRunUntracked_IsNoOp()
    {
        // When preRunUntracked is null (backward-compatible path), no auto-include
        // pass runs. A new untracked file absent from the manifest is NOT committed.
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        Directory.CreateDirectory(Path.Combine(repo.Root, "tests"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "old");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        // Agent creates a new test file that is not in the manifest.
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");
        File.WriteAllText(Path.Combine(repo.Root, "tests", "new-test.cs"), "// new test");

        var manifest = new[] { "src/app.cs" };

        var result = await GitCommitter.CommitAsync(
            repo.Root,
            "my-task",
            "abc123",
            ["feat: add widget"],
            manifest,
            [],
            commitToken: null,
            preRunUntracked: null,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var committed = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");
        Assert.Contains("src/app.cs", committed);
        // The new test file is NOT staged — backward-compatible, no auto-include.
        Assert.DoesNotContain("tests/new-test.cs", committed);
    }

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

    // ── helpers ──────────────────────────────────────────────────────

    private static async Task InitGitRepo(string root)
    {
        Directory.CreateDirectory(root);
        TestGit.Run(root, "init");
        TestGit.Run(root, "config", "user.email", "test@example.test");
        TestGit.Run(root, "config", "user.name", "Test");
    }

    private static async Task StageAndCommitSeed(string root, string message)
    {
        TestGit.Run(root, "add", ".");
        TestGit.Run(root, "commit", "-m", message);
    }
}
