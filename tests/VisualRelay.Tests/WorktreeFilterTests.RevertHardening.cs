using VisualRelay.Core.Execution;
using GitSimEngine = VisualRelay.GitSim.GitSim;

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
    /// <para>
    /// GAP: GitSim's <c>diff --name-status</c> does not perform
    /// <c>-M</c>/<c>-C</c> rename detection (a rename reads back as a plain
    /// add+delete), so the <c>R100</c> record this fact depends on is injected
    /// via <see cref="InterceptedGitInvoker"/> for the one <c>--name-status
    /// --cached</c> call (same technique as WorktreeFilterTests.CopyRecords.cs);
    /// the physical rename (and its real <c>add -A</c> staging) still happens
    /// for real. Both endpoints are excluded by the rename-pair guard before the
    /// revert loop runs, so neither ever reaches <c>checkout</c> — this fact
    /// does not need the HeadCheckoutAwareGitInvoker correction.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RenameSourceIsTestFile_PreservesBothEndpoints()
    {
        using var repo = TestRepository.Create();

        // Set up both files BEFORE the first git commit so the
        // rename stays staged (not committed).
        var bPath = Path.Combine(repo.Root, "b.txt");
        var prodPath = Path.Combine(repo.Root, "src", "app.cs");
        var sim = new GitSimEngine();
        sim.InitRepo(repo.Root);
        sim.Seed(repo.Root, "b.txt", "test-content");
        sim.Seed(repo.Root, "src/app.cs", "original");
        sim.Commit(repo.Root, "seed");

        // Stage a rename: b.txt → c.txt. GitSim has no `mv` verb — move the
        // file for real, then `add -A` stages it at the index level exactly as
        // `git mv` would (old path removed, new path added).
        File.Move(bPath, Path.Combine(repo.Root, "c.txt"));
        await sim.Git(repo.Root, "add", "-A");

        // Modify the production file in the working tree.
        await File.WriteAllTextAsync(prodPath, "modified");

        // Inject the R100 record `git diff --cached --name-status -M -C -z`
        // would emit for this rename (GAP above).
        var gitInvoker = new InterceptedGitInvoker(
            repo.Root,
            argv => argv.Contains("--name-status") && argv.Contains("--cached"),
            _ => Task.FromResult((0, "R100\0b.txt\0c.txt\0", false)));

        await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, ["b.txt"], tasksDir: null, gitInvoker, cancellationToken: CancellationToken.None);

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
    /// <para>
    /// GAP: same GitSim rename-detection limitation as
    /// <see cref="RenameSourceIsTestFile_PreservesBothEndpoints"/> — the
    /// <c>R100</c> record is injected via <see cref="InterceptedGitInvoker"/>
    /// for the one <c>--name-status --cached</c> call. Unlike that fact, the
    /// SOURCE (<c>prod.cs</c>) is NOT rename-protected (it is not itself a
    /// testFile) so it DOES reach <c>checkout HEAD -- prod.cs</c> — but
    /// <c>prod.cs</c> is committed (present in HEAD), so GitSim's checkout
    /// handles it correctly without needing the HeadCheckoutAwareGitInvoker
    /// correction (that gap only bites an ABSENT-from-HEAD path, and the one
    /// absent path here — <c>my.Tests.cs</c> — is excluded before the revert
    /// loop runs).
    /// </para>
    /// </summary>
    [Fact]
    public async Task ProdToTestRename_RestoresProdSource_KeepsTestDest()
    {
        using var repo = TestRepository.Create();

        // prod.cs is a production file committed to HEAD.
        var prodPath = await InitRepoWithTrackedFile(repo.Root, "prod.cs", "prod-content");

        // Stage rename: prod.cs → my.Tests.cs. GitSim has no `mv` verb — move
        // the file for real, then `add -A` stages it at the index level.
        var sim = new GitSimEngine();
        File.Move(prodPath, Path.Combine(repo.Root, "my.Tests.cs"));
        await sim.Git(repo.Root, "add", "-A");

        // Inject the R100 record for this rename (GAP above).
        var gitInvoker = new InterceptedGitInvoker(
            repo.Root,
            argv => argv.Contains("--name-status") && argv.Contains("--cached"),
            _ => Task.FromResult((0, "R100\0prod.cs\0my.Tests.cs\0", false)));

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, ["my.Tests.cs"], tasksDir: null, gitInvoker, cancellationToken: CancellationToken.None);

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

        var stagedStatus = (await sim.Git(repo.Root, "diff", "--cached", "--name-status", "-M")).Output;
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
    /// <para>
    /// GAP: this fact needs no rename-record injection (no testFile is
    /// declared, so the outcome is identical whether GitSim reports the
    /// rename as R100 or as a plain add+delete — see
    /// WorktreeFilterTests.DataLossFixes.cs's
    /// <c>DiscardNonTestEditsAsync_StagedRename_DoesNotAbortReverts</c> for the
    /// same reasoning). It DOES need the <see cref="HeadCheckoutAwareGitInvoker"/>
    /// correction (WorktreeFilterTests.cs): <c>c.txt</c> is staged but never
    /// committed (absent from HEAD), and real git's <c>checkout HEAD -- c.txt</c>
    /// failing is what drives the cat-file probe + rm --cached + delete path
    /// that removes it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RenameNeitherTestFile_BothEndpointsReverted()
    {
        using var repo = TestRepository.Create();

        // Both a.txt and b.txt are production files.
        var aPath = await InitRepoWithTrackedFile(repo.Root, "a.txt", "A-content");
        var bPath = Path.Combine(repo.Root, "b.txt");
        var sim = new GitSimEngine();
        sim.Seed(repo.Root, "b.txt", "B-content");
        sim.Commit(repo.Root, "add b");

        // Stage rename: a.txt → c.txt (move for real, then `add -A` stages it).
        File.Move(aPath, Path.Combine(repo.Root, "c.txt"));
        await sim.Git(repo.Root, "add", "-A");

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, new HeadCheckoutAwareGitInvoker(sim), CancellationToken.None);

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

        var gitInvoker = new InterceptedGitInvoker(
            repo.Root,
            argv => argv.Any(a => a == "checkout"),
            _ => Task.FromResult((1, "simulated transient checkout failure", false)));

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, gitInvoker, cancellationToken: CancellationToken.None);

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
}
