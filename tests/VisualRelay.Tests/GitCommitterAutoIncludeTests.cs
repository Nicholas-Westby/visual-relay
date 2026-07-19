using VisualRelay.Core.Execution;
using static VisualRelay.Tests.GitCommitterGitSimSetup;

namespace VisualRelay.Tests;

public sealed class GitCommitterAutoIncludeTests
{
    [Fact]
    public async Task CommitAsync_AutoIncludesNewUntrackedFileUnderTests()
    {
        // A new test file authored during stage 5 that the stage-4 manifest never
        // listed must not be silently dropped. The auto-include pass stages any
        // non-ignored untracked file that appeared after the run started.
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "old");
        sim.Commit(repo.Root, "chore: seed");

        // Snapshot before the run: no untracked files exist.
        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, sim, CancellationToken.None);
        Assert.Empty(preRunUntracked);

        // Simulate an agent authoring a new test file and modifying a manifest-listed file.
        Write(repo, "src/app.cs", "updated");
        Write(repo, "tests/new-test.cs", "// new test");

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
            tasksDir: null,
            sim, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var committed = sim.FilesInCommit(repo.Root, sim.Head(repo.Root)!);
        Assert.Contains("src/app.cs", committed);
        Assert.Contains("tests/new-test.cs", committed);
    }

    [Fact]
    public async Task CommitAsync_ExcludesPreExistingUntrackedFile()
    {
        // Pre-existing untracked files (present before the run started and captured
        // in the snapshot) must NOT be auto-included. Only files authored during the
        // run (delta: current \ snapshot) are staged.
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "old");
        sim.Commit(repo.Root, "chore: seed");

        // Pre-existing untracked scratch that must NOT be committed — created
        // after the seed so it is untracked when the snapshot is taken.
        Write(repo, "scratch/notes.txt", "scratch");

        // Snapshot captures the pre-existing scratch file.
        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, sim, CancellationToken.None);
        Assert.Contains("scratch/notes.txt", preRunUntracked);

        // Agent modifies a tracked file and creates a new test file under tests/.
        Write(repo, "src/app.cs", "updated");
        Write(repo, "tests/new-test.cs", "// new test");

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
            tasksDir: null,
            sim, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var committed = sim.FilesInCommit(repo.Root, sim.Head(repo.Root)!);
        Assert.Contains("tests/new-test.cs", committed);
        Assert.DoesNotContain("scratch/notes.txt", committed);
    }

    [Fact]
    public async Task CommitAsync_AutoIncludesNewFileInAnyDirectory_NotJustSourceRoots()
    {
        // Visual Relay runs on arbitrary repo layouts (Python, JS, Go, root-level
        // code), so auto-include must NOT assume a src/tests/tools shape. A new,
        // non-ignored file the run authored anywhere — docs/, lib/, the repo root —
        // must be committed, not silently dropped.
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "app.py", "old");
        sim.Commit(repo.Root, "chore: seed");

        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, sim, CancellationToken.None);
        Assert.Empty(preRunUntracked);

        // Author files outside any conventional .NET source root.
        Write(repo, "app.py", "updated");
        Write(repo, "docs/guide.md", "# Guide");
        Write(repo, "lib/helper.js", "// helper");
        Write(repo, "test_app.py", "# root-level test");

        var manifest = new[] { "app.py" };

        var result = await GitCommitter.CommitAsync(
            repo.Root,
            "my-task",
            "abc123",
            ["feat: add widget"],
            manifest,
            [],
            commitToken: null,
            preRunUntracked,
            tasksDir: null,
            sim, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var committed = sim.FilesInCommit(repo.Root, sim.Head(repo.Root)!);
        Assert.Contains("docs/guide.md", committed);
        Assert.Contains("lib/helper.js", committed);
        Assert.Contains("test_app.py", committed);
    }

    [Fact]
    public async Task CommitAsync_ExcludesVisualRelayInternalArtifacts_EvenWhenNotGitignored()
    {
        // On a consumer repo that does not gitignore .relay/ or .swival/, the run's
        // own artifacts (reports, traces, scratch) surface as untracked. They must
        // never be auto-committed into the user's task commit — only the deliberate
        // proof subset is force-added (via proofFiles), handled separately.
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "app.py", "old");
        sim.Commit(repo.Root, "chore: seed");

        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, sim, CancellationToken.None);
        Assert.Empty(preRunUntracked);

        // The run authors a real test file AND leaves internal artifacts behind.
        Write(repo, "app.py", "updated");
        Write(repo, "tests/new-test.py", "# new test");
        Write(repo, ".relay/my-task/stage1-attempt1.report.json", "{}");
        Write(repo, ".swival/cmd_output.txt", "trace");

        var manifest = new[] { "app.py" };

        var result = await GitCommitter.CommitAsync(
            repo.Root,
            "my-task",
            "abc123",
            ["feat: add widget"],
            manifest,
            [],
            commitToken: null,
            preRunUntracked,
            tasksDir: null,
            sim, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var committed = sim.FilesInCommit(repo.Root, sim.Head(repo.Root)!);
        Assert.Contains("tests/new-test.py", committed);
        Assert.DoesNotContain(committed, p => p.StartsWith(".relay/", StringComparison.Ordinal));
        Assert.DoesNotContain(committed, p => p.StartsWith(".swival/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CommitAsync_ExcludesGitignoredNewFile()
    {
        // Gitignored paths must stay excluded unless force-added as proof files.
        // The auto-include pass must respect .gitignore (--exclude-standard covers it).
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "old");
        sim.Seed(repo.Root, ".gitignore", "*.log\n");
        sim.Commit(repo.Root, "chore: seed");

        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, sim, CancellationToken.None);
        Assert.Empty(preRunUntracked);

        // Agent creates a gitignored log file and a new test file.
        Write(repo, "src/app.cs", "updated");
        Write(repo, "debug.log", "log content");
        Write(repo, "tests/new-test.cs", "// new test");

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
            tasksDir: null,
            sim, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var committed = sim.FilesInCommit(repo.Root, sim.Head(repo.Root)!);
        Assert.Contains("tests/new-test.cs", committed);
        Assert.DoesNotContain("debug.log", committed);
    }

    [Fact]
    public async Task CommitAsync_NullPreRunUntracked_IsNoOp()
    {
        // When preRunUntracked is null (backward-compatible path), no auto-include
        // pass runs. A new untracked file absent from the manifest is NOT committed.
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "old");
        sim.Commit(repo.Root, "chore: seed");

        // Agent creates a new test file that is not in the manifest.
        Write(repo, "src/app.cs", "updated");
        Write(repo, "tests/new-test.cs", "// new test");

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
            tasksDir: null,
            sim, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var committed = sim.FilesInCommit(repo.Root, sim.Head(repo.Root)!);
        Assert.Contains("src/app.cs", committed);
        // The new test file is NOT staged — backward-compatible, no auto-include.
        Assert.DoesNotContain("tests/new-test.cs", committed);
    }
}
