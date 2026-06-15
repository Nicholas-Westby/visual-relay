using VisualRelay.Core.Execution;

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
            repo.Root, [], tasksDir: null, CancellationToken.None);

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
            repo.Root, ["+tests/NewTests.cs"], tasksDir: null, CancellationToken.None);

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
            repo.Root, [@"tests\FooTests.cs"], tasksDir: null, CancellationToken.None);

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
            repo.Root, ["./tests/BarTests.cs"], tasksDir: null, CancellationToken.None);

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
            repo.Root, ["tests/footests.cs"], tasksDir: null, CancellationToken.None);

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
        await File.WriteAllTextAsync(bPath, "B");
        TestGit.Run(repo.Root, "add", "b.txt");
        TestGit.Run(repo.Root, "commit", "-m", "add b");

        // Modify a.txt in the working tree.
        await File.WriteAllTextAsync(aPath, "A-modified");
        // Stage a rename: b.txt → c.txt (git mv stages the rename).
        TestGit.Run(repo.Root, "mv", "b.txt", "c.txt");

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, CancellationToken.None);

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

        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "test@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Test");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "seed");

        // Modify both tracked files — simulate agent edits during stage 5.
        await File.WriteAllTextAsync(artifactFile, "modified-config");
        await File.WriteAllTextAsync(taskFile, "modified-task");

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: "llm-tasks", CancellationToken.None);

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

        GitInvoker.ResetForTests();
        var myRoot = repo.Root;
        // envRemove must match what GitInvoker sets after resolution so
        // the delegated ProcessCapture call strips DEVELOPER_DIR / SDKROOT
        // from the child process — otherwise a stale nix-store value in the
        // inherited environment breaks git on macOS.
        var envRemove = new HashSet<string>(StringComparer.Ordinal) { "DEVELOPER_DIR", "SDKROOT" };
        GitInvoker.Override = (binary, args, rootPath, ct, timeout, env) =>
        {
            // Guard: only intercept commands targeting this test's repo.
            if (rootPath != myRoot)
            {
                return ProcessCapture.RunAsync(
                    binary,
                    ["-C", rootPath, .. args],
                    rootPath,
                    timeout ?? TimeSpan.FromSeconds(30),
                    ct,
                    env,
                    envRemove: envRemove);
            }

            // Fail checkout and rm commands to simulate an unrecoverable revert.
            if (args.Any(a => a == "checkout" || a == "rm"))
                return Task.FromResult((1, "simulated git failure", false));

            // Delegate enumeration commands to real git.
            return ProcessCapture.RunAsync(
                binary,
                ["-C", rootPath, .. args],
                rootPath,
                timeout ?? TimeSpan.FromSeconds(30),
                ct,
                env,
                envRemove: envRemove);
        };

        try
        {
            var result = await WorktreeFilter.DiscardNonTestEditsAsync(
                repo.Root, [], tasksDir: null, CancellationToken.None);

            Assert.NotNull(result.Error);
            Assert.Contains("revert failures", result.Error, StringComparison.Ordinal);
            Assert.Contains("src/app.cs", result.TrackedDiscarded, StringComparer.Ordinal);
        }
        finally
        {
            GitInvoker.ResetForTests();
        }
    }
}
