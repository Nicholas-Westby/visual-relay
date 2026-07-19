using VisualRelay.Core.Execution;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the six data-loss / silent-failure defects fixed in
/// <see cref="WorktreeFilter.DiscardNonTestEditsAsync"/>:
/// +-prefix stripping, path normalization, case-insensitive matching,
/// staged-rename resilience, symmetric artifact/tasks-dir guards,
/// and failure-signal propagation.
/// </summary>
public sealed partial class WorktreeFilterTests
{
    // ═══════════════════════════════════════════════════════════════
    // Non-git repo — clean no-op (nothing to discard)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_NonGitRepo_ReturnsEmptyNoError()
    {
        using var repo = TestRepository.Create();
        // No git init — just a plain temp directory.

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, new GitSimEngine(), CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Empty(result.TrackedDiscarded);
        Assert.Empty(result.UntrackedDeleted);
    }

    // ═══════════════════════════════════════════════════════════════
    // Defect 1: "+"-prefixed new test file is preserved (not deleted)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_PlusPrefixedTestFile_IsPreserved()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");

        // Agent declares a brand-new test file with "+" prefix
        // (the stage-4 manifest convention for new files).
        var testFile = Path.Combine(repo.Root, "tests", "NewTests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(testFile)!);
        await File.WriteAllTextAsync(testFile, "// new test");

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, ["+tests/NewTests.cs"], tasksDir: null, new GitSimEngine(), CancellationToken.None);

        Assert.Null(result.Error);
        Assert.True(File.Exists(testFile),
            "new test file declared with '+' prefix must be preserved");
        Assert.DoesNotContain("tests/NewTests.cs", result.UntrackedDeleted, StringComparer.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════
    // Defect 5: backslash in testFiles preserves the on-disk test
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_BackslashTestFile_IsPreserved()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");

        var testFile = Path.Combine(repo.Root, "tests", "FooTests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(testFile)!);
        await File.WriteAllTextAsync(testFile, "// test");

        // Agent may emit Windows-style backslash paths.
        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [@"tests\FooTests.cs"], tasksDir: null, new GitSimEngine(), CancellationToken.None);

        Assert.True(File.Exists(testFile),
            "test file with backslash path must be preserved");
        Assert.DoesNotContain("tests/FooTests.cs", result.UntrackedDeleted, StringComparer.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════
    // Defect 5: "./" prefix in testFiles preserves the on-disk test
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_DotSlashTestFile_IsPreserved()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");

        var testFile = Path.Combine(repo.Root, "tests", "BarTests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(testFile)!);
        await File.WriteAllTextAsync(testFile, "// test");

        // Agent may emit paths with "./" prefix.
        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, ["./tests/BarTests.cs"], tasksDir: null, new GitSimEngine(), CancellationToken.None);

        Assert.True(File.Exists(testFile),
            "test file with './' prefix must be preserved");
        Assert.DoesNotContain("tests/BarTests.cs", result.UntrackedDeleted, StringComparer.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════
    // Defect 6: case-divergent testFiles entry preserves the test
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_CaseDivergentTestFile_IsPreserved()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");

        var testFile = Path.Combine(repo.Root, "tests", "FooTests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(testFile)!);
        await File.WriteAllTextAsync(testFile, "// test");

        // Agent may emit the path with different case on a
        // case-insensitive host (default macOS volume).
        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, ["tests/footests.cs"], tasksDir: null, new GitSimEngine(), CancellationToken.None);

        Assert.True(File.Exists(testFile),
            "case-divergent test file entry must preserve the on-disk file");
        Assert.DoesNotContain("tests/FooTests.cs", result.UntrackedDeleted, StringComparer.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════
    // Defect 2: staged rename does not abort other reverts
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_StagedRename_DoesNotAbortReverts()
    {
        using var repo = TestRepository.Create();
        var aPath = await InitRepoWithTrackedFile(repo.Root, "a.txt", "A");
        var bPath = Path.Combine(repo.Root, "b.txt");
        var sim = new GitSimEngine();
        sim.Seed(repo.Root, "b.txt", "B");
        sim.Commit(repo.Root, "add b");

        // Modify a.txt in the working tree.
        await File.WriteAllTextAsync(aPath, "A-modified");
        // Stage a rename: b.txt → c.txt. GitSim has no `mv` verb, so the rename
        // is reproduced at the index level: move the file for real, then `add -A`
        // stages the old path's disappearance (a delete) and the new path's
        // appearance (an add) exactly as git's index would after a real `git mv`.
        // (Neither endpoint here is a declared testFile, so this fact does not
        // depend on GitSim's diff reporting R100 — see ResidualDataLoss.cs /
        // RevertHardening.cs for the facts that inject a synthetic rename record.)
        File.Move(bPath, Path.Combine(repo.Root, "c.txt"));
        await sim.Git(repo.Root, "add", "-A");

        // c.txt is staged but never committed (absent from HEAD), so the git
        // invoker must be HeadCheckoutAwareGitInvoker-corrected (see
        // WorktreeFilterTests.cs) for `checkout HEAD -- c.txt` to fail the way
        // real git does, letting the cat-file probe + rm --cached + delete run.
        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, new HeadCheckoutAwareGitInvoker(sim), CancellationToken.None);

        Assert.Null(result.Error);
        // a.txt must be reverted — proves the batch did not abort on c.txt.
        Assert.Equal("A", await File.ReadAllTextAsync(aPath));
        // b.txt must be restored from HEAD by the revert.
        Assert.True(File.Exists(bPath), "b.txt should be restored by checkout HEAD");
        Assert.Equal("B", await File.ReadAllTextAsync(bPath));
        // c.txt (the rename destination) must not survive the discard.
        var cPath = Path.Combine(repo.Root, "c.txt");
        Assert.False(File.Exists(cPath), "c.txt (rename destination) should be deleted");
    }

    // ═══════════════════════════════════════════════════════════════
    // Defect 4: tracked artifact and tasks-dir files are preserved
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_TrackedArtifactAndTaskDir_Preserved()
    {
        using var repo = TestRepository.Create();

        // Create and commit a tracked artifact file.
        var artifactDir = Path.Combine(repo.Root, ".relay");
        Directory.CreateDirectory(artifactDir);
        var artifactFile = Path.Combine(artifactDir, "config.json");
        await File.WriteAllTextAsync(artifactFile, "original-config");

        // Create and commit a tracked task file.
        var tasksDir = Path.Combine(repo.Root, "llm-tasks");
        Directory.CreateDirectory(tasksDir);
        var taskFile = Path.Combine(tasksDir, "task.md");
        await File.WriteAllTextAsync(taskFile, "original-task");

        var sim = new GitSimEngine();
        sim.InitRepo(repo.Root);
        await sim.Git(repo.Root, "add", ".");
        sim.Commit(repo.Root, "seed");

        // Modify both tracked files — simulate agent edits during stage 5.
        await File.WriteAllTextAsync(artifactFile, "modified-config");
        await File.WriteAllTextAsync(taskFile, "modified-task");

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: "llm-tasks", sim, CancellationToken.None);

        // Neither tracked file should be reverted.
        Assert.Equal("modified-config", await File.ReadAllTextAsync(artifactFile));
        Assert.Equal("modified-task", await File.ReadAllTextAsync(taskFile));
        Assert.DoesNotContain(".relay/config.json", result.TrackedDiscarded, StringComparer.Ordinal);
        Assert.DoesNotContain("llm-tasks/task.md", result.TrackedDiscarded, StringComparer.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════
    // Defect 3: failing revert surfaces an error (no silent discard)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_FailingRevert_SurfacesError()
    {
        using var repo = TestRepository.Create();
        var prodFile = await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");

        // Modify the tracked production file.
        await File.WriteAllTextAsync(prodFile, "modified");

        var gitInvoker = new InterceptedGitInvoker(
            repo.Root,
            argv => argv.Any(a => a == "checkout" || a == "rm"),
            _ => Task.FromResult((1, "simulated git failure", false)));

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, gitInvoker, cancellationToken: CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("revert/delete failures", result.Error, StringComparison.Ordinal);
        Assert.Contains("src/app.cs", result.Error, StringComparison.Ordinal);
        // With the cat-file -e probe, an in-HEAD path that fails checkout
        // transiently is NOT deleted and NOT added to TrackedDiscarded.
        Assert.DoesNotContain("src/app.cs", result.TrackedDiscarded, StringComparer.Ordinal);
        Assert.True(File.Exists(prodFile), "in-HEAD file must survive transient checkout failure");
    }
}
