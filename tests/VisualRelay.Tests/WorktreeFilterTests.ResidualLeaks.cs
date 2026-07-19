using VisualRelay.Core.Execution;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// Red-first tests for the residual correctness leaks in
/// <see cref="WorktreeFilter.DiscardNonTestEditsAsync"/> — each lets a
/// non-test edit survive into stage 6: (1) TAB/newline in a path C-quoted and
/// never dequoted; (2) leading/trailing whitespace mangled by a <c>Trim()</c>;
/// (3) a COPY (<c>C</c>) record's destination dropped or mis-protected;
/// (4) a prod→test rename leaving a stray staged deletion (over-exclude).
/// Leaks 1/2 are fixed together by NUL-delimited (<c>-z</c>) parsing.
/// </summary>
public sealed partial class WorktreeFilterTests
{
    // ═══════════════════════════════════════════════════════════════
    // Leak 1: TAB in a non-test path is reverted (not missed)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A tracked production file whose name contains a TAB is C-quoted by
    /// git in non-<c>-z</c> output (<c>"tab\tfile.txt"</c>) regardless of
    /// <c>core.quotePath</c>.  The quoted literal never matches the real
    /// on-disk path, so <c>git checkout</c> misses it and the edit leaks.
    /// With <c>-z</c> the path is emitted verbatim and the edit is reverted.
    /// </summary>
    [Fact]
    public async Task TabInTrackedPath_NonTestEditIsReverted()
    {
        using var repo = TestRepository.Create();
        var relPath = "src/tab\tfile.cs";        // literal TAB in the name
        var full = await InitRepoWithTrackedFile(repo.Root, relPath, "original");

        // Agent modifies the production file.
        await File.WriteAllTextAsync(full, "modified-by-agent");

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, new GitSimEngine(), CancellationToken.None);

        Assert.Null(result.Error);
        // ── CRITICAL: the TAB-named production edit must be reverted ──
        Assert.Equal("original", await File.ReadAllTextAsync(full));
        Assert.Contains(relPath, result.TrackedDiscarded, StringComparer.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════
    // Leak 1 (newline variant): NEWLINE in a non-test path is reverted
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A tracked production file whose name contains a literal NEWLINE is
    /// C-quoted in non-<c>-z</c> output and would also be split by the
    /// line-oriented capture reader.  With <c>-z</c> the embedded newline is
    /// preserved verbatim before its NUL terminator (the capture reader strips
    /// it on read but <c>AppendLine</c> re-inserts it), so the path round-trips
    /// and the edit is reverted — and no spurious empty path leaks into the
    /// result lists.
    /// </summary>
    [Fact]
    public async Task NewlineInTrackedPath_NonTestEditIsReverted()
    {
        using var repo = TestRepository.Create();
        var relPath = "src/line\nbreak.cs";       // literal newline in the name
        var full = await InitRepoWithTrackedFile(repo.Root, relPath, "original");

        await File.WriteAllTextAsync(full, "modified-by-agent");

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, new GitSimEngine(), CancellationToken.None);

        Assert.Null(result.Error);
        // ── CRITICAL: the newline-named production edit must be reverted ──
        Assert.Equal("original", await File.ReadAllTextAsync(full));
        Assert.Contains(relPath, result.TrackedDiscarded, StringComparer.Ordinal);
        // No phantom newline-only entry leaks into the result list.
        Assert.DoesNotContain("\n", result.TrackedDiscarded, StringComparer.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════
    // Leak 2: trailing-whitespace non-test path is reverted (not mangled)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A tracked production file whose name ends in a space survives the
    /// enumeration only if the parser does NOT <c>Trim()</c> the git-emitted
    /// path.  The old <c>line.Trim()</c> stripped the trailing space, so the
    /// mangled path missed the real file and the edit leaked.
    /// </summary>
    [Fact]
    public async Task TrailingSpaceInTrackedPath_NonTestEditIsReverted()
    {
        using var repo = TestRepository.Create();
        // A true trailing space at the very end of the filename — the
        // old line.Trim() in the parser stripped this, mangling the path.
        var relPath = "src/trailingspace.cs ";
        var full = await InitRepoWithTrackedFile(repo.Root, relPath, "original");

        await File.WriteAllTextAsync(full, "modified-by-agent");

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, new GitSimEngine(), CancellationToken.None);

        Assert.Null(result.Error);
        // ── CRITICAL: the trailing-space production edit must be reverted ──
        Assert.Equal("original", await File.ReadAllTextAsync(full));
        Assert.Contains(relPath, result.TrackedDiscarded, StringComparer.Ordinal);
    }

    // Leak 3 (COPY record's destination, both the non-test and the dangerous
    // copy-from-testFile-to-prod variants) lives in the companion file
    // WorktreeFilterTests.CopyRecords.cs (kept under the file-size guard).

    // ═══════════════════════════════════════════════════════════════
    // Leak 4: prod→test rename leaves no stray staged deletion
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// When a staged rename's DESTINATION is a testFile but its SOURCE is a
    /// production file, excluding BOTH endpoints leaves the staged DELETION
    /// of the old production path surviving into stage 6 (a production
    /// change leaked).  The conservative fix restores the production source
    /// from HEAD (reverting its staged deletion) while leaving the test
    /// destination intact — so stage 6 sees the production tree unchanged
    /// and the authored test content preserved.
    /// <para>
    /// GAP: GitSim's <c>diff --name-status</c> does not perform <c>-M</c>/<c>-C</c>
    /// rename detection, so the <c>R100</c> record this fact depends on is
    /// injected via <see cref="InterceptedGitInvoker"/> for the one
    /// <c>--name-status --cached</c> call (same technique as
    /// WorktreeFilterTests.CopyRecords.cs / RevertHardening.cs). The SOURCE
    /// (<c>prod.cs</c>) is not rename-protected, so it DOES reach
    /// <c>checkout HEAD -- prod.cs</c> — but it is committed (present in
    /// HEAD), so GitSim's checkout handles it correctly without needing the
    /// HeadCheckoutAwareGitInvoker correction (that gap only bites a path
    /// ABSENT from HEAD, and the one absent path here — <c>some.Tests.cs</c> —
    /// is excluded before the revert loop runs).
    /// </para>
    /// </summary>
    [Fact]
    public async Task ProdToTestRename_RestoresProdSource_NoStrayStagedDeletion()
    {
        using var repo = TestRepository.Create();

        // prod.cs is a production file committed to HEAD.
        var prodPath = await InitRepoWithTrackedFile(repo.Root, "prod.cs", "prod-content");

        // Stage rename: prod.cs → some.Tests.cs. GitSim has no `mv` verb —
        // move the file for real, then `add -A` stages it at the index level.
        var sim = new GitSimEngine();
        File.Move(prodPath, Path.Combine(repo.Root, "some.Tests.cs"));
        await sim.Git(repo.Root, "add", "-A");

        // Inject the R100 record for this rename (GAP above).
        var gitInvoker = new InterceptedGitInvoker(
            repo.Root,
            argv => argv.Contains("--name-status") && argv.Contains("--cached"),
            _ => Task.FromResult((0, "R100\0prod.cs\0some.Tests.cs\0", false)));

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, ["some.Tests.cs"], tasksDir: null, gitInvoker, cancellationToken: CancellationToken.None);

        Assert.Null(result.Error);

        // ── CRITICAL: no stray staged deletion of the production source ──
        // The index must NOT stage prod.cs as deleted heading into stage 6.
        var stagedStatus = (await sim.Git(repo.Root, "diff", "--cached", "--name-status", "-M")).Output;
        Assert.DoesNotContain("prod.cs", stagedStatus, StringComparison.Ordinal);

        // Production source restored on disk from HEAD.
        Assert.True(File.Exists(prodPath),
            "production source prod.cs must be restored (its staged deletion reverted)");
        Assert.Equal("prod-content", await File.ReadAllTextAsync(prodPath));

        // The test destination must survive — it holds the authored content.
        var destPath = Path.Combine(repo.Root, "some.Tests.cs");
        Assert.True(File.Exists(destPath),
            "test destination some.Tests.cs must survive");
        Assert.Equal("prod-content", await File.ReadAllTextAsync(destPath));
    }
}
