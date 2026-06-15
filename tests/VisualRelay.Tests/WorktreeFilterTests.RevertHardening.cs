using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Red-first tests for data-loss defects A and B in
/// <see cref="WorktreeFilter.DiscardNonTestEditsAsync"/> revert logic:
/// rename-pair testFile guard and cat-file -e probe before deletion.
/// </summary>
public sealed partial class WorktreeFilterTests
{
    // ═══════════════════════════════════════════════════════════════
    // Defect A: rename source is a testFile → permanent data loss
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// When a staged rename's OLD name is a declared testFile, the
    /// filter must NOT destroy the rename destination — that is the
    /// only surviving copy of the test content.  Currently the
    /// destination leaks into nonTestTracked and is deleted.
    /// </summary>
    [Fact]
    public async Task RenameSourceIsTestFile_PreservesBothEndpoints()
    {
        using var repo = TestRepository.Create();

        // Set up both files BEFORE the first git commit so the
        // rename stays staged (not committed).
        var bPath = Path.Combine(repo.Root, "b.txt");
        var prodPath = Path.Combine(repo.Root, "src", "app.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(prodPath)!);
        await File.WriteAllTextAsync(bPath, "test-content");
        await File.WriteAllTextAsync(prodPath, "original");

        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "test@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Test");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "seed");

        // Stage a rename: b.txt → c.txt (git mv stages the rename).
        TestGit.Run(repo.Root, "mv", "b.txt", "c.txt");

        // Modify the production file in the working tree.
        await File.WriteAllTextAsync(prodPath, "modified");

        await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, ["b.txt"], tasksDir: null, CancellationToken.None);

        // ── CRITICAL assertion ──────────────────────────────────
        // The rename destination c.txt must survive — it holds the
        // testFile content that git mv moved from b.txt.
        var cPath = Path.Combine(repo.Root, "c.txt");
        Assert.True(File.Exists(cPath),
            "rename destination c.txt must exist — testFile content must not be destroyed");
        Assert.Equal("test-content", await File.ReadAllTextAsync(cPath));

        // b.txt was removed by `git mv` (rename source).  That is
        // expected — the rename is left intact.
        Assert.False(File.Exists(bPath),
            "b.txt was the rename source — no longer exists on disk");

        // Production file must still be reverted.
        Assert.Equal("original", await File.ReadAllTextAsync(prodPath));
    }

    // ═══════════════════════════════════════════════════════════════
    // Defect A (mirror) + Leak 4: prod→test rename restores the prod
    //   source (no stray staged deletion) and keeps the test destination
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// When a staged rename's NEW name is a declared testFile and its OLD
    /// name is a PRODUCTION file (prod→test), the filter keeps the test
    /// destination intact AND restores the production source from HEAD.
    /// <para>
    /// Restoring the source reverts its staged DELETION — leak 4: the old
    /// behaviour excluded BOTH endpoints, leaving the staged deletion of the
    /// production source surviving into stage 6 (a production change leaking
    /// past the filter).  Restoring <c>prod.cs</c> via
    /// <c>git checkout HEAD -- prod.cs</c> touches a distinct path, so the
    /// test destination <c>my.Tests.cs</c> (which holds the only surviving
    /// copy of the content) is NOT disturbed: no test-content loss, no stray
    /// staged deletion.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ProdToTestRename_RestoresProdSource_KeepsTestDest()
    {
        using var repo = TestRepository.Create();

        // prod.cs is a production file committed to HEAD.
        var prodPath = await InitRepoWithTrackedFile(repo.Root, "prod.cs", "prod-content");

        // Stage rename: prod.cs → my.Tests.cs.  The destination is
        // the declared testFile.
        TestGit.Run(repo.Root, "mv", "prod.cs", "my.Tests.cs");

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, ["my.Tests.cs"], tasksDir: null, CancellationToken.None);

        Assert.Null(result.Error);

        // ── The test destination must survive — it holds the content. ──
        var destPath = Path.Combine(repo.Root, "my.Tests.cs");
        Assert.True(File.Exists(destPath),
            "rename destination my.Tests.cs must exist");
        Assert.Equal("prod-content", await File.ReadAllTextAsync(destPath));

        // ── Leak 4: the production source must be restored (its staged
        // deletion reverted) so no stray staged deletion reaches stage 6. ──
        Assert.True(File.Exists(prodPath),
            "production source prod.cs must be restored — staged deletion reverted");
        Assert.Equal("prod-content", await File.ReadAllTextAsync(prodPath));

        var stagedStatus = TestGit.Run(repo.Root, "diff", "--cached", "--name-status", "-M");
        Assert.DoesNotContain("prod.cs", stagedStatus, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════
    // Defect A regression-guard: neither endpoint a testFile →
    //   legitimate rename revert still works
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// When NEITHER endpoint of a staged rename is a testFile, both
    /// endpoints must be fully reverted: the old name restored from
    /// HEAD, the new name deleted.  Regression-guard for the
    /// legitimate rename-revert path.
    /// </summary>
    [Fact]
    public async Task RenameNeitherTestFile_BothEndpointsReverted()
    {
        using var repo = TestRepository.Create();

        // Both a.txt and b.txt are production files.
        var aPath = await InitRepoWithTrackedFile(repo.Root, "a.txt", "A-content");
        var bPath = Path.Combine(repo.Root, "b.txt");
        await File.WriteAllTextAsync(bPath, "B-content");
        TestGit.Run(repo.Root, "add", "b.txt");
        TestGit.Run(repo.Root, "commit", "-m", "add b");

        // Stage rename: a.txt → c.txt.
        TestGit.Run(repo.Root, "mv", "a.txt", "c.txt");

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, CancellationToken.None);

        Assert.Null(result.Error);

        // Old name a.txt must be restored from HEAD.
        Assert.True(File.Exists(aPath),
            "rename source a.txt must be restored from HEAD");
        Assert.Equal("A-content", await File.ReadAllTextAsync(aPath));

        // New name c.txt must be deleted.
        var cPath = Path.Combine(repo.Root, "c.txt");
        Assert.False(File.Exists(cPath),
            "rename destination c.txt must be deleted");

        // Unrelated file b.txt untouched.
        Assert.Equal("B-content", await File.ReadAllTextAsync(bPath));
    }

    // ═══════════════════════════════════════════════════════════════
    // Defect B: transient checkout failure on an in-HEAD path
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A <c>git checkout</c> that fails for a transient reason
    /// (exit ≠ 0 but the path IS in HEAD) must NOT trigger deletion.
    /// Only a positive <c>cat-file -e</c> confirmation that the path
    /// is absent from HEAD may allow the unstage+delete path.
    /// </summary>
    [Fact]
    public async Task TransientCheckoutFailureOnInHeadPath_DoesNotDelete()
    {
        using var repo = TestRepository.Create();
        var prodPath = await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");

        // Modify the tracked production file so it appears dirty.
        await File.WriteAllTextAsync(prodPath, "modified");

        GitInvoker.ResetForTests();
        var myRoot = repo.Root;
        var envRemove = new HashSet<string>(StringComparer.Ordinal) { "DEVELOPER_DIR", "SDKROOT" };
        GitInvoker.Override = (binary, args, rootPath, ct, timeout, env) =>
        {
            var argv = args as string[] ?? args.ToArray();
            if (rootPath != myRoot)
            {
                return ProcessCapture.RunAsync(
                    binary, ["-C", rootPath, .. argv], rootPath,
                    timeout ?? TimeSpan.FromSeconds(30), ct, env, envRemove: envRemove);
            }

            // Make `git checkout HEAD -- src/app.cs` fail with exit 1
            // even though the path IS in HEAD (simulates a transient
            // failure like an index.lock race or EIO).
            if (argv.Any(a => a == "checkout"))
                return Task.FromResult((1, "simulated transient checkout failure", false));

            // All other commands (diff, ls-files, etc.) run normally.
            return ProcessCapture.RunAsync(
                binary, ["-C", rootPath, .. argv], rootPath,
                timeout ?? TimeSpan.FromSeconds(30), ct, env, envRemove: envRemove);
        };

        try
        {
            var result = await WorktreeFilter.DiscardNonTestEditsAsync(
                repo.Root, [], tasksDir: null, CancellationToken.None);

            // ── CRITICAL assertion ──────────────────────────────
            // The production file must still exist — it was in HEAD
            // and the checkout failure was transient, NOT proof of
            // absence.  Currently it IS deleted (data loss).
            Assert.True(File.Exists(prodPath),
                "in-HEAD path must survive a transient checkout failure");

            // An Error must be surfaced so the run is flagged.
            Assert.NotNull(result.Error);
            Assert.Contains("src/app.cs", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            GitInvoker.ResetForTests();
        }
    }
}
