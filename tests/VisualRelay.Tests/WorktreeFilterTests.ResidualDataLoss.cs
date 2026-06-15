using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Red-first tests for the two residual data-loss holes left after 11e5d16
/// in <see cref="WorktreeFilter.DiscardNonTestEditsAsync"/>:
///   Hole 1 — a case-only rename (<c>git mv Foo.cs foo.cs</c>) collapses the
///            host-gated rename-pair dictionary into one self-referential
///            entry, defeating the testFile exclusion guard and destroying
///            the only surviving copy of the test content.
///   Hole 2 — a non-zero <c>git rm --cached</c> exit (the <c>AM new.cs</c>
///            staged-content-differs case) is swallowed and File.Delete runs
///            anyway, destroying the worktree file while leaving the index
///            staging a now-missing blob, with no Error surfaced.
/// </summary>
public sealed partial class WorktreeFilterTests
{
    // ═══════════════════════════════════════════════════════════════
    // Hole 1: case-only rename whose endpoint is a testFile
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Root-cause unit test for Hole 1.  The rename-pair testFile guard must
    /// exclude BOTH endpoints of a case-only rename (<c>git mv Foo.cs foo.cs</c>)
    /// when either endpoint is a declared testFile.
    /// <para>
    /// The pre-fix guard built a <see cref="Dictionary{TKey,TValue}"/> keyed by
    /// the host-gated comparer; under OrdinalIgnoreCase (macOS / Windows) the
    /// two insertions <c>["Foo.cs"]="foo.cs"</c> then <c>["foo.cs"]="Foo.cs"</c>
    /// collapse into ONE self-referential entry <c>{Foo.cs: Foo.cs}</c>, and the
    /// <c>CompareOrdinal(key,value) &gt;= 0</c> dedup gate then skips it — so the
    /// exclusion set comes back EMPTY and the guard silently fails to protect
    /// the rename.  End-to-end this is currently masked by the OrdinalIgnoreCase
    /// testSet membership filter (both case-folded endpoints match the declared
    /// testFile), but the guard itself is defective and must not depend on that
    /// coincidence — it is the sole protection for a distinct-name rename whose
    /// partner is a testFile.  This test pins the guard's contract directly.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("foo.cs")] // destination endpoint is the declared testFile
    [InlineData("Foo.cs")] // source endpoint is the declared testFile
    public void ComputeRenameExclusions_CaseOnlyRenameWithTestFileEndpoint_ExcludesBothEndpoints(
        string declaredTestFile)
    {
        // Only the host-gated OrdinalIgnoreCase comparer (macOS / Windows)
        // collapses the pair; on a case-sensitive host the dictionary never
        // collapsed and the guard already worked, so assert the behaviour the
        // production code uses on THIS host.
        var pathComparer = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var testSet = new HashSet<string>([declaredTestFile], pathComparer);

        // A single case-only rename pair, exactly as AddNameStatusLines records
        // it from `git diff --cached --name-status -M` ("R100  Foo.cs  foo.cs").
        var renamePairs = new List<(string Old, string New)> { ("Foo.cs", "foo.cs") };

        var exclude = WorktreeFilter.ComputeRenameExclusions(renamePairs, testSet, pathComparer);

        // ── CRITICAL assertion ──────────────────────────────────
        // BOTH endpoints must be excluded — i.e. exclude.Contains is true for
        // each, which is exactly how DiscardNonTestEditsAsync filters them out
        // of dirtyTracked (it uses the set's own host-gated comparer).  Pre-fix
        // the set is EMPTY (dictionary collapse + self-comparing dedup gate), so
        // neither endpoint is protected.  (The set itself may store one folded
        // entry under OrdinalIgnoreCase; membership is what matters.)
        Assert.True(exclude.Contains("Foo.cs"),
            "source endpoint Foo.cs must be excluded from reversion");
        Assert.True(exclude.Contains("foo.cs"),
            "destination endpoint foo.cs must be excluded from reversion");
    }

    /// <summary>
    /// End-to-end safety assertion for the case-only rename: with the rename
    /// destination declared as the testFile, its content must survive intact
    /// and the rename must be left staged (no spurious discard, no error).
    /// </summary>
    [Fact]
    public async Task CaseOnlyRenameOfTestFile_PreservesTestContent()
    {
        using var repo = TestRepository.Create();

        // A production file so the batch has unrelated work to do.
        var prodPath = await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "prod-original");

        // Foo.cs is committed, then case-renamed to foo.cs.  On the default
        // case-insensitive macOS / Windows volume both names map to one
        // on-disk file; the content "test-content" lives at that inode.
        var fooPath = Path.Combine(repo.Root, "Foo.cs");
        await File.WriteAllTextAsync(fooPath, "test-content");
        TestGit.Run(repo.Root, "add", "Foo.cs");
        TestGit.Run(repo.Root, "commit", "-m", "add Foo.cs");
        TestGit.Run(repo.Root, "mv", "Foo.cs", "foo.cs");

        // Modify the production file so it appears dirty.
        await File.WriteAllTextAsync(prodPath, "prod-modified");

        // Declare foo.cs (the rename destination) as the testFile.
        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, ["foo.cs"], tasksDir: null, CancellationToken.None);

        // The case-renamed test file must still exist on disk with its
        // content intact, and must not be reported as discarded.
        var renamedPath = Path.Combine(repo.Root, "foo.cs");
        Assert.True(File.Exists(renamedPath),
            "case-renamed test file foo.cs must survive — it is the only copy of the test content");
        Assert.Equal("test-content", await File.ReadAllTextAsync(renamedPath));
        Assert.DoesNotContain("foo.cs", result.TrackedDiscarded, StringComparer.OrdinalIgnoreCase);
        Assert.Null(result.Error);

        // The unrelated production file must still be reverted.
        Assert.Equal("prod-original", await File.ReadAllTextAsync(prodPath));
    }

    // ═══════════════════════════════════════════════════════════════
    // Hole 2: AM new.cs — git rm --cached fails non-zero, File.Delete runs
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// An <c>AM new.cs</c> file (git add a new file, then edit it again) is
    /// absent from HEAD, so checkout fails and the cat-file probe confirms
    /// absence.  Plain <c>git rm --cached --ignore-unmatch</c> then fails
    /// with <c>fatal: ... staged content different ... use -f</c> (exit 1) —
    /// <c>--ignore-unmatch</c> only masks the absent-pathspec case, not this
    /// one.  The fix must NOT silently leave the index staging a blob for a
    /// File.Delete'd worktree file: either the unstage is clean (no dangling
    /// <c>AD</c>/staged-add index entry) or an Error is surfaced.
    /// </summary>
    [Fact]
    public async Task AddedThenModifiedNewFile_RmCachedFailure_NoInconsistentIndex()
    {
        using var repo = TestRepository.Create();

        // Baseline commit so the repo is valid and HEAD exists.
        await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");

        // Create a NEW non-test file, stage it, then modify it again → "AM".
        var newPath = Path.Combine(repo.Root, "src", "new.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        await File.WriteAllTextAsync(newPath, "v1");
        TestGit.Run(repo.Root, "add", "src/new.cs");
        await File.WriteAllTextAsync(newPath, "v2-modified");

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, CancellationToken.None);

        // Determine the post-condition: read the index for src/new.cs.
        var staged = TestGit.Run(repo.Root, "ls-files", "--stage", "--", "src/new.cs").Trim();
        var fileOnDisk = File.Exists(newPath);

        // ── CRITICAL assertion ──────────────────────────────────
        // The forbidden state is: worktree file deleted AND the index still
        // stages a blob for it (a dangling staged-add pointing at a missing
        // file) with NO Error surfaced.  Acceptable outcomes are either:
        //   (a) the unstage was clean — the index no longer stages new.cs; or
        //   (b) an Error was surfaced flagging the inconsistency.
        var indexStillStages = staged.Length > 0;
        var inconsistentSilent = !fileOnDisk && indexStillStages && result.Error is null;

        Assert.False(inconsistentSilent,
            "must not File.Delete the worktree file while the index still stages a " +
            $"missing blob with no Error. index='{staged}', onDisk={fileOnDisk}, error='{result.Error}'");
    }
}
